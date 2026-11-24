using BuildingBlocks.Security;
using Identity.Application;
using Identity.Application.Abstractions;
using Identity.Infrastructure.Email;
using Identity.Infrastructure.Persistence;
using Identity.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Identity.Infrastructure;

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>Apply EF migrations when the API starts. Local/compose only; production runs the migration Job instead.</summary>
    public bool MigrateOnStartup { get; set; }
}

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<AuthOptions>().Bind(config.GetSection(AuthOptions.Section))
            .Validate(o => o.AccessTokenMinutes is > 0 and <= 60 && o.MaxFailedLogins > 0 && o.LockoutMinutes > 0
                           && o.IpAttemptLimit > 0 && Uri.IsWellFormedUriString(o.FrontendBaseUrl, UriKind.Absolute),
                "Invalid Auth options.")
            .ValidateOnStart();
        services.AddOptions<JwtOptions>().Bind(config.GetSection(JwtOptions.Section)).ValidateOnStart();
        services.AddOptions<SigningOptions>().Bind(config.GetSection(SigningOptions.Section)).ValidateOnStart();
        services.AddOptions<EmailOptions>().Bind(config.GetSection(EmailOptions.Section))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Host) && o.Port > 0, "Email:Host and Email:Port are required.")
            .ValidateOnStart();
        services.AddOptions<DatabaseOptions>().Bind(config.GetSection(DatabaseOptions.Section));

        var connectionString = config.GetConnectionString("Identity");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:Identity is required.");

        services.AddDbContext<IdentityDb>(o => o.UseMySql(connectionString, ServerVersion));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<IdentityDb>());
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IEmailTokenRepository, EmailTokenRepository>();
        services.AddScoped<IOutbox, EfOutbox>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<Application.Abstractions.IPasswordHasher, AspNetPasswordHasher>();
        services.AddSingleton<SigningKeyStore>();
        services.AddSingleton<IAccessTokenService, JwtAccessTokenService>();

        services.AddJtiBlacklist(config); // also registers the shared Redis connection
        services.AddSingleton<ITokenRevocation, JtiTokenRevocation>();
        services.AddSingleton<ILoginThrottle, RedisLoginThrottle>();

        services.AddSingleton<QueuedEmailSender>();
        services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<QueuedEmailSender>());
        services.AddHostedService<EmailDispatcher>();

        // Identity validates its own tokens with the key it signs with (no HTTP round trip to itself).
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<SigningKeyStore>((o, keys) =>
            {
                o.TokenValidationParameters.IssuerSigningKeyResolver = null;
                o.TokenValidationParameters.IssuerSigningKey = keys.SigningKey;
                o.TokenValidationParameters.IssuerSigningKeys = [keys.SigningKey];
            });

        services.AddHealthChecks()
            .AddCheck<MySqlReadinessCheck>("mysql", tags: ["ready"])
            .AddCheck<RedisReadinessCheck>("redis", tags: ["ready"]);

        return services;
    }

    // Pinned (not auto-detected) so startup never needs a database connection and the model is stable for migrations.
    internal static readonly ServerVersion ServerVersion = new MySqlServerVersion(new Version(8, 4, 0));

    /// <summary>Applies pending migrations, waiting for MySQL to accept connections (compose start-up race).</summary>
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDb>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Identity.Migrations");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync(ct);
                logger.LogInformation("Identity database is up to date");
                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Migration attempt {Attempt} failed; retrying", attempt);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }
}

internal sealed class MySqlReadinessCheck(IdentityDb db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("MySQL is unreachable");
}

internal sealed class RedisReadinessCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis is unreachable", ex);
        }
    }
}

/// <summary>Lets <c>dotnet ef migrations</c> build the context without running the API.</summary>
internal sealed class IdentityDbFactory : IDesignTimeDbContextFactory<IdentityDb>
{
    public IdentityDb CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Identity")
                 ?? "Server=localhost;Port=3306;Database=identity;User=identity;Password=identity";
        var options = new DbContextOptionsBuilder<IdentityDb>().UseMySql(cs, DependencyInjection.ServerVersion).Options;
        return new IdentityDb(options);
    }
}
