using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace PunchedApi.Infrastructure.Caching;

/// <summary>
/// Redis connection settings (<c>Redis</c> section / <c>Redis__*</c> variables).
/// </summary>
public sealed class RedisSettings
{
    public const string SectionName = "Redis";

    /// <summary>
    /// Turns the Redis cache-aside layer on. The process still runs correctly
    /// with this false (memory-only) or with Redis unreachable.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>StackExchange.Redis connection string, e.g. <c>redis:6379,password=…</c>.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Prefix applied to every key so one Redis instance can host several environments.</summary>
    public string InstanceName { get; set; } = "punched:";

    /// <summary>Default lifetime for entries written through <see cref="ICacheStore"/>.</summary>
    public int DefaultTtlSeconds { get; set; } = 60;

    /// <summary>Upper bound on a single get/set, so a wedged Redis cannot slow a request path.</summary>
    public int OperationTimeoutMs { get; set; } = 500;
}

/// <summary>
/// Cache-aside store for short-lived, non-authoritative values.
///
/// <para><b>What may be cached:</b> derived lookups whose source of truth is a
/// database row and that are already revalidated within seconds (for example the
/// tenant scope resolved from <c>business_memberships</c>).</para>
///
/// <para><b>What may never be cached:</b> balances, payments, bookings,
/// entitlements, permissions decisions or anything else where a stale read can
/// change money, availability or access. Those stay direct database reads.</para>
///
/// <para>This is deliberately <i>not</i> a queue. Durable work stays in the
/// PostgreSQL outbox (<c>notification_log</c>, domain events, scheduled jobs)
/// where a claim, lease, retry and dead-letter already exist and an eviction can
/// never lose a job. Redis here is a cache: evictable, lossy by design.</para>
/// </summary>
public interface ICacheStore
{
    Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default);

    Task SetStringAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>In-process implementation (development, tests, Redis disabled).</summary>
public sealed class MemoryCacheStore : ICacheStore
{
    private readonly IMemoryCache _cache;

    public MemoryCacheStore(IMemoryCache cache)
    {
        _cache = cache;
    }

    public Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_cache.TryGetValue(key, out string? value) ? value : null);

    public Task SetStringAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        _cache.Set(key, value, ttl);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _cache.Remove(key);
        return Task.CompletedTask;
    }
}
