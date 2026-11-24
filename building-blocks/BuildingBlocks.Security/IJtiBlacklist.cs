using StackExchange.Redis;

namespace BuildingBlocks.Security;

/// <summary>Revoked access-token ids. Entries live exactly as long as the token would have.</summary>
public interface IJtiBlacklist
{
    Task RevokeAsync(string jti, TimeSpan ttl, CancellationToken ct = default);
    Task<bool> IsRevokedAsync(string jti, CancellationToken ct = default);
}

public sealed class RedisJtiBlacklist(IConnectionMultiplexer redis) : IJtiBlacklist
{
    private static string Key(string jti) => $"auth:revoked-jti:{jti}";

    public async Task RevokeAsync(string jti, TimeSpan ttl, CancellationToken ct = default)
    {
        if (ttl <= TimeSpan.Zero) return; // already expired, nothing to blacklist
        await redis.GetDatabase().StringSetAsync(Key(jti), "1", ttl);
    }

    public async Task<bool> IsRevokedAsync(string jti, CancellationToken ct = default) =>
        await redis.GetDatabase().KeyExistsAsync(Key(jti));
}
