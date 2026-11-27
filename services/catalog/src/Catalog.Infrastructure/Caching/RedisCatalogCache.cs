using System.Text.Json;
using Catalog.Application.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Catalog.Infrastructure.Caching;

/// <summary>
/// Cache-aside on Redis. On a miss one caller (per key, across all pods) takes <c>lock:{key}</c> with SET NX PX and loads the
/// value; the others poll the cache for a short while instead of all hitting MongoDB (stampede protection). If Redis is
/// unavailable every call degrades to a direct load, so the cache can never take the catalog down.
/// </summary>
public sealed class RedisCatalogCache(IConnectionMultiplexer redis, ILogger<RedisCatalogCache> logger) : ICatalogCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WaitStep = TimeSpan.FromMilliseconds(50);
    private const int MaxWaitSteps = 60; // 3 s

    private const string ReleaseScript = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    public async Task<T?> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<CancellationToken, Task<T?>> factory, CancellationToken ct) where T : class
    {
        var cacheName = key.StartsWith("cache:product:", StringComparison.Ordinal) ? "product" : "category_tree";
        var db = redis.GetDatabase();

        var (ok, cached) = await TryRead<T>(db, key);
        if (cached is not null) { Count(cacheName, "hit"); return cached; }
        Count(cacheName, "miss");
        if (!ok) return await factory(ct);

        var lockKey = "lock:" + key;
        var token = Guid.NewGuid().ToString("N");
        var acquired = false;
        try { acquired = await db.StringSetAsync(lockKey, token, LockTtl, When.NotExists); }
        catch (Exception ex) when (ex is RedisException or TimeoutException) { logger.LogWarning(ex, "Cache lock unavailable; loading without it"); }

        if (acquired)
        {
            try
            {
                (_, cached) = await TryRead<T>(db, key); // another pod may have filled it while we were acquiring the lock
                if (cached is not null) return cached;

                var value = await factory(ct);
                if (value is not null) await TryWrite(db, key, value, ttl);
                return value;
            }
            finally
            {
                try { await db.ScriptEvaluateAsync(ReleaseScript, [lockKey], [token], CommandFlags.FireAndForget); }
                catch (Exception ex) when (ex is RedisException or TimeoutException) { /* the lock expires on its own */ }
            }
        }

        // Someone else is loading: wait for their result, then fall back to loading ourselves.
        for (var i = 0; i < MaxWaitSteps; i++)
        {
            await Task.Delay(WaitStep, ct);
            (ok, cached) = await TryRead<T>(db, key);
            if (cached is not null) return cached;
            if (!ok || !await SafeKeyExists(db, lockKey)) break; // holder finished without caching (e.g. not found) or died
        }

        return await factory(ct);
    }

    public async Task RemoveAsync(IEnumerable<string> keys, CancellationToken ct)
    {
        var redisKeys = keys.Distinct().Select(k => (RedisKey)k).ToArray();
        if (redisKeys.Length == 0) return;
        try { await redis.GetDatabase().KeyDeleteAsync(redisKeys); }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // The write already committed; a stale entry now lives until its TTL. Surface it loudly.
            logger.LogError(ex, "Cache invalidation failed for {Count} key(s); entries stay stale until they expire", redisKeys.Length);
        }
    }

    private async Task<(bool Ok, T? Value)> TryRead<T>(IDatabase db, string key) where T : class
    {
        try
        {
            var raw = await db.StringGetAsync(key);
            return (true, raw.IsNullOrEmpty ? null : JsonSerializer.Deserialize<T>((string)raw!, Json));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            logger.LogWarning(ex, "Cache read failed for a {Type}; loading from the database", typeof(T).Name);
            return (ex is JsonException, null); // a corrupt entry is treated as a miss, Redis itself is still up
        }
    }

    private async Task TryWrite<T>(IDatabase db, string key, T value, TimeSpan ttl)
    {
        try { await db.StringSetAsync(key, JsonSerializer.Serialize(value, Json), ttl); }
        catch (Exception ex) when (ex is RedisException or TimeoutException) { logger.LogWarning(ex, "Cache write failed"); }
    }

    private static async Task<bool> SafeKeyExists(IDatabase db, string key)
    {
        try { return await db.KeyExistsAsync(key); }
        catch (Exception ex) when (ex is RedisException or TimeoutException) { return false; }
    }

    private static void Count(string cache, string result) =>
        CatalogMetrics.CacheRequests.Add(1, new KeyValuePair<string, object?>("cache", cache), new KeyValuePair<string, object?>("result", result));
}

/// <summary>Redis is an optimisation here, so an outage reports Degraded (still ready) rather than taking the pod out of rotation.</summary>
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
            return HealthCheckResult.Degraded("Redis is unreachable; serving without cache", ex);
        }
    }
}
