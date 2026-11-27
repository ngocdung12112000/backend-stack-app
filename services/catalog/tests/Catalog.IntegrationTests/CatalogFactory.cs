using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Catalog.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using StackExchange.Redis;
using Testcontainers.MongoDb;
using Testcontainers.Redis;

namespace Catalog.IntegrationTests;

/// <summary>Real MongoDB 7 (single-node replica set) and Redis 7 in containers; the API runs in-process.</summary>
public sealed class CatalogFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MongoDbContainer _mongo = new MongoDbBuilder().WithImage("mongo:7").WithReplicaSet().Build();
    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();
    private readonly RSA _key = RSA.Create(2048);

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_mongo.StartAsync(), _redis.StartAsync());

        // Environment variables are visible to the eager configuration reads in Program.cs.
        Environment.SetEnvironmentVariable("ConnectionStrings__Catalog", _mongo.GetConnectionString());
        Environment.SetEnvironmentVariable("Mongo__Database", "catalog_test");
        Environment.SetEnvironmentVariable("Redis__ConnectionString", _redis.GetConnectionString());
        Environment.SetEnvironmentVariable("Jwt__PublicKeyPem", _key.ExportSubjectPublicKeyInfoPem());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _mongo.DisposeAsync();
        await _redis.DisposeAsync();
    }

    public CatalogDb Db => Services.GetRequiredService<CatalogDb>();
    public IDatabase Redis => Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

    public string Token(params string[] permissions)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "stackshop-identity",
            Audience = "stackshop-api",
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(15),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_key) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString(), ["jti"] = Guid.NewGuid().ToString(), ["perm"] = permissions },
        });
    }

    public CatalogClient Admin() => new(CreateClient(), Token("catalog:write"));
    public CatalogClient Anonymous() => new(CreateClient(), null);
}

[CollectionDefinition(Name)]
public sealed class CatalogCollection : ICollectionFixture<CatalogFactory>
{
    public const string Name = "catalog";
}

/// <summary>Thin typed client over the catalog HTTP API. Every response is returned raw so tests can assert status, headers and problem codes.</summary>
public sealed class CatalogClient(HttpClient http, string? bearer)
{
    public HttpClient Http { get; } = Authorize(http, bearer);

    private static HttpClient Authorize(HttpClient http, string? bearer)
    {
        if (bearer is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    public static string Unique(string prefix = "Item") => $"{prefix} {Guid.NewGuid():N}"[..(prefix.Length + 9)];

    public Task<HttpResponseMessage> Get(string path) => Http.GetAsync(path);

    public Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return Http.SendAsync(request);
    }

    public static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();
    public static async Task<string> Code(HttpResponseMessage response) => (await Json(response)).GetProperty("code").GetString()!;
    public static string ETag(HttpResponseMessage response) => response.Headers.ETag!.Tag;

    // --- scenario helpers (each asserts success so a failing precondition is reported where it happens) ---

    public async Task<JsonElement> Created(string path, object body)
    {
        var response = await Send(HttpMethod.Post, path, body);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await Json(response);
    }

    public Task<JsonElement> CreateCategory(string? name = null, Guid? parentId = null, object? schema = null) =>
        Created("/api/v1/admin/categories", new { name = name ?? Unique("Category"), parentId, sort = 0, isActive = true, attributeSchema = schema });

    public Task<JsonElement> CreateBrand(string? name = null) => Created("/api/v1/admin/brands", new { name = name ?? Unique("Brand") });

    public Task<JsonElement> CreateDraft(Guid? categoryId = null, Guid? brandId = null, string? name = null, string? slug = null, object? attributes = null) =>
        Created("/api/v1/admin/products", new
        {
            name = name ?? Unique("Product"),
            slug,
            description = "<p>Great product</p>",
            brandId,
            categoryIds = categoryId is null ? Array.Empty<Guid>() : [categoryId.Value],
            primaryCategoryId = categoryId,
            attributes,
        });

    /// <summary>Runs a product mutation with the product's current version and returns the updated product.</summary>
    public async Task<(HttpResponseMessage Response, JsonElement Body)> Mutate(JsonElement product, HttpMethod method, string suffix, object? body = null)
    {
        var id = product.GetProperty("id").GetGuid();
        var response = await Send(method, $"/api/v1/admin/products/{id}{suffix}", body, $"\"{product.GetProperty("version").GetInt64()}\"");
        return (response, await Json(response));
    }

    public async Task<JsonElement> MutateOk(JsonElement product, HttpMethod method, string suffix, object? body = null)
    {
        var (response, json) = await Mutate(product, method, suffix, body);
        response.IsSuccessStatusCode.Should().BeTrue(json.ToString());
        return json;
    }

    public Task<JsonElement> UpsertVariant(JsonElement product, string sku, decimal price, object? options = null, decimal? compareAt = null) =>
        MutateOk(product, HttpMethod.Put, $"/variants/{sku}", new { options = options ?? new { }, price, compareAtPrice = compareAt, weightG = 100, isActive = true });

    public Task<JsonElement> SetImages(JsonElement product, params string[] urls) =>
        MutateOk(product, HttpMethod.Put, "/images", new { images = urls.Select(u => new { url = u, alt = "img" }).ToArray() });

    /// <summary>Creates a category + product with one variant and an image, and publishes it.</summary>
    public async Task<JsonElement> CreatePublished(string? name = null, decimal price = 100m, Guid? brandId = null, Guid? categoryId = null, string? sku = null)
    {
        categoryId ??= (await CreateCategory()).GetProperty("id").GetGuid();
        var p = await CreateDraft(categoryId, brandId, name);
        p = await UpsertVariant(p, sku ?? $"SKU-{Guid.NewGuid():N}"[..14], price);
        p = await SetImages(p, "https://img.example.com/1.jpg");
        return await MutateOk(p, HttpMethod.Post, "/publish");
    }
}
