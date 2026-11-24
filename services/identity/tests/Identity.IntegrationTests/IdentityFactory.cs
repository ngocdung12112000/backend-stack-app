using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Identity.Application.Abstractions;
using Identity.Application.Users;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.MySql;
using Testcontainers.Redis;

namespace Identity.IntegrationTests;

/// <summary>Real MySQL 8.4 and Redis 7 in containers; the API runs in-process with migrations applied at start-up.</summary>
public sealed class IdentityFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MySqlContainer _mysql = new MySqlBuilder().WithImage("mysql:8.4").WithDatabase("identity")
        .WithUsername("identity").WithPassword("identity").Build();
    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();

    public CapturingEmailSender Emails { get; } = new();
    public OffsetTimeProvider Clock { get; } = new();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_mysql.StartAsync(), _redis.StartAsync());

        // Environment variables are visible to the eager configuration reads in Program.cs.
        Environment.SetEnvironmentVariable("ConnectionStrings__Identity", _mysql.GetConnectionString());
        Environment.SetEnvironmentVariable("Redis__ConnectionString", _redis.GetConnectionString());
        Environment.SetEnvironmentVariable("Signing__GenerateIfMissing", "true");
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "true");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _mysql.DisposeAsync();
        await _redis.DisposeAsync();
    }

    public async Task<Guid> CreateAdminAsync(string email, string password)
    {
        using var scope = Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new CreateAdminCommand(email, password, "Test Admin"));
        return result.Value;
    }
}

[CollectionDefinition(Name)]
public sealed class IdentityCollection : ICollectionFixture<IdentityFactory>
{
    public const string Name = "identity";
}

/// <summary>Real time plus an offset the tests can move forward (to pass the 15-minute lockout) and reset again.</summary>
public sealed class OffsetTimeProvider : TimeProvider
{
    public TimeSpan Offset { get; set; }
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
}

public sealed class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public ValueTask EnqueueAsync(EmailMessage message, CancellationToken ct = default)
    {
        _sent.Enqueue(message);
        return ValueTask.CompletedTask;
    }

    public EmailMessage LastTo(string email) => _sent.Last(m => m.To.Equals(email, StringComparison.OrdinalIgnoreCase));
    public bool AnyTo(string email) => _sent.Any(m => m.To.Equals(email, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pulls a query-string value (e.g. token) out of the link in the email text body.</summary>
    public static string QueryValue(EmailMessage message, string key)
    {
        var start = message.TextBody.IndexOf(key + "=", StringComparison.Ordinal) + key.Length + 1;
        var end = message.TextBody.IndexOfAny(['&', '\n', ' '], start);
        return Uri.UnescapeDataString(end < 0 ? message.TextBody[start..] : message.TextBody[start..end]);
    }
}

/// <summary>Thin typed client; every instance gets its own spoofed client IP so the per-IP throttle never couples tests.</summary>
public sealed class IdentityClient
{
    public const string Password = "Passw0rd!";
    private readonly HttpClient _http;

    public IdentityClient(IdentityFactory factory)
    {
        _http = factory.CreateClient();
        Ip = $"10.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}";
        _http.DefaultRequestHeaders.Add("X-Forwarded-For", Ip);
    }

    public string Ip { get; }

    public static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    public Task<HttpResponseMessage> Post(string path, object? body = null, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return _http.SendAsync(request);
    }

    public Task<HttpResponseMessage> Get(string path, string? bearer = null) => Send(HttpMethod.Get, path, bearer);
    public Task<HttpResponseMessage> Delete(string path, string? bearer = null) => Send(HttpMethod.Delete, path, bearer);

    public Task<HttpResponseMessage> Put(string path, object body, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(body) };
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return _http.SendAsync(request);
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? bearer)
    {
        var request = new HttpRequestMessage(method, path);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return _http.SendAsync(request);
    }

    public async Task<Guid> Register(string email, string password = Password)
    {
        var response = await Post("/api/v1/auth/register", new { email, password, fullName = "Test User" });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetGuid();
    }

    public Task<HttpResponseMessage> LoginRaw(string email, string password = Password) =>
        Post("/api/v1/auth/login", new { email, password });

    public async Task<Tokens> Login(string email, string password = Password)
    {
        var response = await LoginRaw(email, password);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Tokens(json.GetProperty("accessToken").GetString()!, json.GetProperty("refreshToken").GetString()!);
    }

    public static async Task<string> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
}

public sealed record Tokens(string Access, string Refresh);
