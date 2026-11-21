using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.API.Hosting;

/// <summary>One dependency's state, as reported by readiness.</summary>
public sealed record DependencyCheck(string Name, bool Healthy, string Detail);

/// <summary>Aggregate readiness: every essential dependency answered.</summary>
public sealed record ReadinessResult(bool Ready, IReadOnlyList<DependencyCheck> Checks);

/// <summary>
/// Bounded, cached probes for the readiness endpoint.
///
/// <para>Design rules, all of them deliberate:</para>
/// <list type="bullet">
///   <item><b>No expensive query.</b> The database is probed with
///   <c>CanConnectAsync</c> (open/validate a pooled connection), never a
///   <c>COUNT</c> or a scan.</item>
///   <item><b>Bounded.</b> Each probe has its own timeout, so a hung Neon link or a
///   wedged Redis cannot hold a probe (or a Docker health check) open.</item>
///   <item><b>Cached.</b> A positive database result is reused for a few seconds.
///   Docker probes every 20 s; without this, every probe of every container would
///   cost a Frankfurt round trip.</item>
///   <item><b>No external AI provider calls.</b> Nothing in the readiness path
///   touches a paid third-party API.</item>
/// </list>
/// </summary>
public sealed class ReadinessProbe
{
    private const string DatabaseCacheKey = "health:ready:database";
    private const string DatabaseFailureCacheKey = "health:ready:database:failed";
    private const string RedisCacheKey = "health:ready:redis";
    private const string RedisFailureCacheKey = "health:ready:redis:failed";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceProvider _services;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ReadinessProbe> _logger;
    private readonly HealthOptions _options;

    public ReadinessProbe(
        IServiceScopeFactory scopeFactory,
        IServiceProvider services,
        IMemoryCache cache,
        ILogger<ReadinessProbe> logger,
        IOptions<HealthOptions>? options = null)
    {
        _scopeFactory = scopeFactory;
        _services = services;
        _cache = cache;
        _logger = logger;
        _options = options?.Value ?? new HealthOptions();
    }

    public async Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken)
    {
        var checks = new List<DependencyCheck>(2)
        {
            await CheckDatabaseAsync(cancellationToken),
            await CheckRedisAsync(cancellationToken)
        };

        return new ReadinessResult(checks.All(c => c.Healthy), checks);
    }

    private async Task<DependencyCheck> CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(DatabaseCacheKey, out bool cached) && cached)
        {
            return new DependencyCheck("database", true, "connected");
        }

        if (_cache.TryGetValue(DatabaseFailureCacheKey, out bool failed) && failed)
        {
            return new DependencyCheck("database", false, "unreachable");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.DatabaseProbeTimeoutSeconds, 1, 60)));

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var connected = await db.Database.CanConnectAsync(timeout.Token);
            if (connected)
            {
                _cache.Set(DatabaseCacheKey, true, TimeSpan.FromSeconds(
                    Math.Clamp(_options.DatabaseProbeCacheSeconds, 1, 300)));
                _cache.Remove(DatabaseFailureCacheKey);
                return new DependencyCheck("database", true, "connected");
            }

            CacheDatabaseFailure();
            return new DependencyCheck("database", false, "unreachable");
        }
        catch (Exception ex)
        {
            // The exception text is logged, never returned: /health/ready is
            // anonymous, and provider messages can carry host names and users.
            _logger.LogWarning(ex, "Readiness probe: database check failed.");
            _cache.Remove(DatabaseCacheKey);
            CacheDatabaseFailure();
            return new DependencyCheck("database", false, "unreachable");
        }
    }

    /// <summary>
    /// Remembers a failure for a couple of seconds only: recovery (a Neon compute
    /// resuming) must be visible almost immediately, while a burst of probes during a
    /// wake-up must not each pay a timeout.
    /// </summary>
    private void CacheDatabaseFailure() =>
        _cache.Set(DatabaseFailureCacheKey, true, TimeSpan.FromSeconds(
            Math.Clamp(_options.DatabaseProbeNegativeCacheSeconds, 0, 30)));

    private async Task<DependencyCheck> CheckRedisAsync(CancellationToken cancellationToken)
    {
        var multiplexer = _services.GetService<IConnectionMultiplexer>();
        if (multiplexer is null)
        {
            return new DependencyCheck("redis", true, "disabled");
        }

        if (_cache.TryGetValue(RedisCacheKey, out string? cachedDetail) && cachedDetail is not null)
        {
            return new DependencyCheck("redis", true, cachedDetail);
        }

        if (_cache.TryGetValue(RedisFailureCacheKey, out bool failed) && failed)
        {
            return new DependencyCheck("redis", false, "unreachable");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RedisProbeTimeoutSeconds, 1, 10)));

            var latency = await multiplexer.GetDatabase()
                .PingAsync()
                .WaitAsync(timeout.Token);

            var detail = $"connected ({latency.TotalMilliseconds:F0} ms)";
            _cache.Set(RedisCacheKey, detail, TimeSpan.FromSeconds(
                Math.Clamp(_options.RedisProbeCacheSeconds, 0, 60)));
            _cache.Remove(RedisFailureCacheKey);
            return new DependencyCheck("redis", true, detail);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Readiness probe: Redis check failed.");
            _cache.Remove(RedisCacheKey);
            // Redis is a cache: a short failure memory keeps a broken Redis from
            // adding timeout latency to every probe without hiding a real outage.
            _cache.Set(RedisFailureCacheKey, true, TimeSpan.FromSeconds(
                Math.Clamp(_options.RedisProbeCacheSeconds, 0, 60)));
            return new DependencyCheck("redis", false, "unreachable");
        }
    }

    /// <summary>
    /// Pending notification outbox rows. Used only by the diagnostic
    /// <c>/health</c> endpoint (explicitly requested), never by readiness: the
    /// pending count is the queue depth of the notification pipeline and is
    /// useful when investigating a backlog.
    /// </summary>
    public async Task<long?> GetNotificationQueueDepthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.DatabaseProbeTimeoutSeconds, 1, 60)));

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            return await db.NotificationLogs
                .AsNoTracking()
                .CountAsync(n => n.Status == "pending", timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Queue depth unavailable.");
            return null;
        }
    }
}
