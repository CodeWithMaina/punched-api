using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace PunchedApi.Infrastructure.Caching;

/// <summary>
/// Redis-backed <see cref="ICacheStore"/> used when <c>Redis:Enabled=true</c>.
///
/// <para>Every call is bounded and fails soft: a timeout, a dropped connection or
/// an authentication failure returns a cache miss and logs at Debug. A cache is
/// never allowed to turn into an outage — the caller falls through to PostgreSQL,
/// which remains the source of truth.</para>
/// </summary>
public sealed class RedisCacheStore : ICacheStore
{
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisSettings _settings;
    private readonly ILogger<RedisCacheStore> _logger;

    public RedisCacheStore(
        IConnectionMultiplexer redis,
        IOptions<RedisSettings> settings,
        ILogger<RedisCacheStore> logger)
    {
        _redis = redis;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await _redis.GetDatabase()
                .StringGetAsync(Key(key))
                .WaitAsync(OperationTimeout, cancellationToken);

            return value.HasValue ? value.ToString() : null;
        }
        catch (Exception ex) when (IsCacheFailure(ex) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Redis read failed for {CacheKey}; treating as a miss.", key);
            return null;
        }
    }

    public async Task SetStringAsync(
        string key,
        string value,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _redis.GetDatabase()
                .StringSetAsync(Key(key), value, ttl)
                .WaitAsync(OperationTimeout, cancellationToken);
        }
        catch (Exception ex) when (IsCacheFailure(ex) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Redis write failed for {CacheKey}; continuing without caching.", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _redis.GetDatabase()
                .KeyDeleteAsync(Key(key))
                .WaitAsync(OperationTimeout, cancellationToken);
        }
        catch (Exception ex) when (IsCacheFailure(ex) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Redis delete failed for {CacheKey}.", key);
        }
    }

    private RedisKey Key(string key) => _settings.InstanceName + key;

    private TimeSpan OperationTimeout =>
        TimeSpan.FromMilliseconds(Math.Clamp(_settings.OperationTimeoutMs, 50, 5000));

    /// <summary>
    /// Failures that mean "the cache is unavailable", never "the caller is wrong".
    /// A cancellation triggered by the caller is rethrown by the filter guard above.
    /// </summary>
    private static bool IsCacheFailure(Exception ex) =>
        ex is RedisException or TimeoutException or ObjectDisposedException or InvalidOperationException
            or System.Net.Sockets.SocketException;
}
