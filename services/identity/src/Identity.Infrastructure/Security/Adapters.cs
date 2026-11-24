using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Common;
using BuildingBlocks.Security;
using Identity.Application;
using Identity.Application.Abstractions;
using Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace Identity.Infrastructure.Security;

/// <summary>ASP.NET Core's PBKDF2 hasher (HMAC-SHA512, 100k iterations, versioned format that supports transparent upgrades).</summary>
internal sealed class AspNetPasswordHasher : Application.Abstractions.IPasswordHasher
{
    private readonly PasswordHasher<User> _inner = new();
    private readonly Lazy<string> _dummyHash;

    public AspNetPasswordHasher() => _dummyHash = new Lazy<string>(() => _inner.HashPassword(null!, Guid.NewGuid().ToString("N")));

    public string Hash(string password) => _inner.HashPassword(null!, password);

    public PasswordVerification Verify(string hash, string password) =>
        _inner.VerifyHashedPassword(null!, hash, password) switch
        {
            PasswordVerificationResult.Success => PasswordVerification.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.SuccessRehashNeeded,
            _ => PasswordVerification.Failed,
        };

    public void BurnVerification(string password) => _inner.VerifyHashedPassword(null!, _dummyHash.Value, password);
}

/// <summary>Issues RS256 access tokens with the claims from docs/05-api-design.md section 4.</summary>
internal sealed class JwtAccessTokenService(SigningKeyStore keys, IOptions<JwtOptions> jwt, IOptions<AuthOptions> auth) : IAccessTokenService
{
    private static readonly JsonWebTokenHandler Handler = new();

    public AccessToken Create(User user, DateTime now)
    {
        var jti = Ids.NewGuidV7().ToString();
        var expires = now + auth.Value.AccessTokenLifetime;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Value.Issuer,
            Audience = jwt.Value.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(keys.SigningKey, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = user.Id.ToString(),
                ["jti"] = jti,
                ["email"] = user.Email,
                ["email_verified"] = user.EmailConfirmed,
                ["name"] = user.FullName ?? user.Email,
                ["role"] = user.RoleNames.ToArray(),
                ["perm"] = user.Permissions.ToArray(),
            },
        };

        return new AccessToken(Handler.CreateToken(descriptor), jti, expires);
    }
}

internal sealed class JtiTokenRevocation(IJtiBlacklist blacklist) : ITokenRevocation
{
    public Task RevokeAccessTokenAsync(string jti, TimeSpan remainingLifetime, CancellationToken ct) =>
        blacklist.RevokeAsync(jti, remainingLifetime, ct);
}

/// <summary>Redis counters with TTL (BR-03). If Redis is down the throttle is skipped rather than blocking every login.</summary>
internal sealed class RedisLoginThrottle(IConnectionMultiplexer redis, IOptions<AuthOptions> options, ILogger<RedisLoginThrottle> logger)
    : ILoginThrottle
{
    // INCR and set the TTL atomically so a counter can never exist without an expiry.
    private const string IncrementWithTtl =
        "local c = redis.call('INCR', KEYS[1]) if c == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end return c";

    public Task<bool> IsIpBlockedAsync(string ip, CancellationToken ct) =>
        IsAtLeast($"auth:login-attempts:{ip}", options.Value.IpAttemptLimit);

    public Task RecordIpFailureAsync(string ip, CancellationToken ct) =>
        Increment($"auth:login-attempts:{ip}", TimeSpan.FromMinutes(options.Value.IpWindowMinutes));

    public Task<bool> IsUnknownEmailLockedAsync(string normalizedEmail, CancellationToken ct) =>
        IsAtLeast(UnknownKey(normalizedEmail), options.Value.MaxFailedLogins);

    public Task RecordUnknownEmailFailureAsync(string normalizedEmail, CancellationToken ct) =>
        Increment(UnknownKey(normalizedEmail), options.Value.Lockout);

    // Hashed so no email address is stored in a Redis key.
    private static string UnknownKey(string normalizedEmail) =>
        "auth:login-unknown:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail)))[..32].ToLowerInvariant();

    private async Task<bool> IsAtLeast(string key, int limit)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key);
            return value.HasValue && (long)value >= limit;
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Login throttle unavailable; skipping the check");
            return false;
        }
    }

    private async Task Increment(string key, TimeSpan window)
    {
        try
        {
            await redis.GetDatabase().ScriptEvaluateAsync(IncrementWithTtl, [key], [(long)window.TotalMilliseconds]);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Login throttle unavailable; failure not recorded");
        }
    }
}
