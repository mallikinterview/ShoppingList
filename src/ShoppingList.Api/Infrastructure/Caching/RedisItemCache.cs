using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShoppingList.Api.Configuration;
using StackExchange.Redis;

namespace ShoppingList.Api.Infrastructure.Caching;

internal sealed class RedisItemCache(
    RedisConnection connection,
    IOptions<RedisSettings> options,
    ILogger<RedisItemCache> logger) : IItemCache
{
    private readonly RedisSettings _settings = options.Value;

    // Per-key in-process gate. Without it, N concurrent misses on the same expensive query all
    // execute it — a cache stampede, which is worst precisely when the cache is coldest and load
    // is highest. This is process-local rather than a distributed lock: a distributed lock adds
    // a round trip to every miss and a whole failure mode of its own, and with a handful of
    // replicas the remaining duplicate work is a few queries, not a thundering herd.
    private static readonly SemaphoreSlim[] KeyLocks =
        [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<T?> GetOrCreateAsync<T>(
        string key,
        Guid userId,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct) where T : class
    {
        // Redis unavailable is not an error: it degrades to computing every time. Throwing here
        // would turn a cache outage into an API outage, which is the opposite of what a cache
        // is for.
        if (!connection.IsAvailable)
        {
            logger.LogDebug("Redis unavailable; serving {Key} from source.", key);
            return await factory(ct);
        }

        try
        {
            var db = connection.Database;
            var version = await GetVersionAsync(db, userId);
            var versionedKey = CacheKeys.Versioned(version, key);

            var cached = await db.StringGetAsync(versionedKey);
            if (TryDeserialise<T>(cached, out var hit))
            {
                return hit;
            }

            var gate = KeyLocks[(uint)versionedKey.GetHashCode(StringComparison.Ordinal) % KeyLocks.Length];
            await gate.WaitAsync(ct);

            try
            {
                // Re-checked under the gate: while waiting, the first caller through has very
                // likely populated the key, and recomputing would defeat the point of waiting.
                cached = await db.StringGetAsync(versionedKey);
                if (TryDeserialise<T>(cached, out var raced))
                {
                    return raced;
                }

                var value = await factory(ct);

                await db.StringSetAsync(
                    versionedKey,
                    JsonSerializer.Serialize(value, SerializerOptions),
                    NextExpiry());

                return value;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Redis operation failed for {Key}; falling back to source.", key);
            return await factory(ct);
        }
    }

    public async Task InvalidateUserAsync(Guid userId, CancellationToken ct)
    {
        if (!connection.IsAvailable)
        {
            return;
        }

        try
        {
            // One INCR invalidates everything cached for this user — searches across all
            // variants and filter combinations, single-item reads and list pages alike — without
            // enumerating keys. The orphaned entries expire on their own TTL.
            var db = connection.Database;
            var version = await db.StringIncrementAsync(CacheKeys.UserVersion(userId));

            logger.LogDebug("Invalidated cache for user {UserId}; version is now {Version}.", userId, version);
        }
        catch (RedisException ex)
        {
            // A failed invalidation means stale reads until the TTL expires — degraded, not
            // broken, and not worth failing the write that triggered it.
            logger.LogWarning(ex, "Failed to invalidate cache for user {UserId}; entries will expire on TTL.", userId);
        }
    }

    private static async Task<long> GetVersionAsync(IDatabase db, Guid userId)
    {
        var value = await db.StringGetAsync(CacheKeys.UserVersion(userId));

        // RedisValue converts implicitly to several types, so the target must be explicit
        // or the overload resolution is ambiguous.
        return value.HasValue && long.TryParse((string?)value, out var version) ? version : 0;
    }

    private static bool TryDeserialise<T>(RedisValue value, [NotNullWhen(true)] out T? result)
        where T : class
    {
        result = value.HasValue
            ? JsonSerializer.Deserialize<T>((string)value!, SerializerOptions)
            : null;

        return result is not null;
    }

    /// <summary>
    /// TTL with random jitter. A fixed TTL means keys written in the same burst expire in the
    /// same burst and stampede the database together; jitter spreads that front out.
    /// </summary>
    private TimeSpan NextExpiry() =>
        TimeSpan.FromSeconds(_settings.DefaultTtlSeconds + Random.Shared.Next(0, _settings.TtlJitterSeconds + 1));
}
