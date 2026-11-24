using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;

namespace BuildingBlocks.Security;

public static class SecurityExtensions
{
    /// <summary>
    /// Validates RS256 JWTs against the identity service's JWKS (<c>Jwt:JwksUrl</c>) or a configured PEM public key,
    /// registers the <c>perm:&lt;resource&gt;:&lt;action&gt;</c> policy provider and, when an <see cref="IJtiBlacklist"/>
    /// is registered, rejects tokens whose <c>jti</c> has been revoked (logout).
    /// Claim names are kept as issued (<c>sub</c>, <c>role</c>, <c>perm</c>, <c>jti</c>); no legacy claim mapping.
    /// </summary>
    public static IServiceCollection AddJwtValidation(
        this IServiceCollection services, IConfiguration config, Action<JwtOptions>? configure = null)
    {
        var opts = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
        configure?.Invoke(opts);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;
                o.RequireHttpsMetadata = false; // cluster-internal HTTP; TLS terminates at the ingress

                var parameters = new TokenValidationParameters
                {
                    ValidIssuer = opts.Issuer,
                    ValidAudience = opts.Audience,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                    ClockSkew = TimeSpan.FromSeconds(30),
                };

                if (!string.IsNullOrWhiteSpace(opts.PublicKeyPem))
                {
                    var rsa = RSA.Create();
                    rsa.ImportFromPem(opts.PublicKeyPem);
                    parameters.IssuerSigningKey = new RsaSecurityKey(rsa);
                }
                else if (!string.IsNullOrWhiteSpace(opts.JwksUrl))
                {
                    var jwks = new JwksKeyProvider(opts.JwksUrl);
                    parameters.IssuerSigningKeyResolver = (_, _, kid, _) => jwks.GetKeys(kid);
                }

                o.TokenValidationParameters = parameters;
                o.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async ctx =>
                    {
                        var blacklist = ctx.HttpContext.RequestServices.GetService<IJtiBlacklist>();
                        var jti = ctx.Principal?.FindFirst("jti")?.Value;
                        if (blacklist is null || string.IsNullOrEmpty(jti)) return;

                        try
                        {
                            if (await blacklist.IsRevokedAsync(jti, ctx.HttpContext.RequestAborted))
                                ctx.Fail("Token has been revoked.");
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Fail open: a Redis outage must not take every authenticated call down.
                            // Access tokens are short-lived (15 min), which bounds the exposure.
                            ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                                .CreateLogger("BuildingBlocks.Security.JtiBlacklist")
                                .LogError(ex, "jti blacklist unavailable; allowing the request");
                        }
                    },
                };
            });

        services.AddAuthorization();
        services.Replace(ServiceDescriptor.Singleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>());
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        return services;
    }

    /// <summary>Redis-backed access-token blacklist (<c>auth:revoked-jti:{jti}</c>). Needs <c>Redis:ConnectionString</c>.</summary>
    public static IServiceCollection AddJtiBlacklist(this IServiceCollection services, IConfiguration config)
    {
        services.AddRedisConnection(config);
        services.TryAddSingleton<IJtiBlacklist, RedisJtiBlacklist>();
        return services;
    }

    /// <summary>Registers a lazily connecting <c>IConnectionMultiplexer</c> from <c>Redis:ConnectionString</c> (idempotent).</summary>
    public static IServiceCollection AddRedisConnection(this IServiceCollection services, IConfiguration config)
    {
        var cs = config["Redis:ConnectionString"] ?? "localhost:6379";
        services.TryAddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
        {
            var options = StackExchange.Redis.ConfigurationOptions.Parse(cs);
            options.AbortOnConnectFail = false; // keep retrying in the background; startup must not depend on Redis
            options.ConnectTimeout = 2000;
            options.AsyncTimeout = 1000;
            options.SyncTimeout = 1000;
            return StackExchange.Redis.ConnectionMultiplexer.Connect(options);
        });
        return services;
    }
}

/// <summary>Caches the identity service's JWKS and re-fetches it (rate limited) when a token carries an unknown kid.</summary>
internal sealed class JwksKeyProvider
{
    private readonly ConfigurationManager<JsonWebKeySet> _manager;

    public JwksKeyProvider(string url)
    {
        _manager = new ConfigurationManager<JsonWebKeySet>(url, new JwksRetriever(), new HttpDocumentRetriever { RequireHttps = false })
        {
            AutomaticRefreshInterval = TimeSpan.FromHours(1),
            RefreshInterval = TimeSpan.FromSeconds(30),
        };
    }

    public IEnumerable<SecurityKey> GetKeys(string? kid)
    {
        var set = _manager.GetConfigurationAsync(CancellationToken.None).GetAwaiter().GetResult();
        var keys = set.GetSigningKeys().Where(k => kid is null || k.KeyId == kid).ToList();
        if (keys.Count == 0)
            _manager.RequestRefresh(); // unknown kid: pick up rotated keys on a later request
        return keys;
    }

    private sealed class JwksRetriever : IConfigurationRetriever<JsonWebKeySet>
    {
        public async Task<JsonWebKeySet> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel) =>
            new(await retriever.GetDocumentAsync(address, cancel));
    }
}
