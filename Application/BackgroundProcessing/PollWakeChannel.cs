using StackExchange.Redis;
using PunchedApi.Infrastructure.Caching;

namespace PunchedApi.Application.BackgroundProcessing;

/// <summary>
/// A best-effort "there may be new work" hint.
///
/// <para><b>It is a hint, never a queue.</b> PostgreSQL remains the durable source of
/// truth: losing a wake-up only costs one polling interval, and a spurious wake-up
/// only costs one cheap query. Nothing is ever removed from the database because a
/// hint was delivered.</para>
///
/// <para>This is what makes deep idle polling affordable: an enqueued booking
/// confirmation reaches the worker immediately over Redis, so the polling interval
/// can be minutes long without making notifications slow.</para>
/// </summary>
public interface IPollWakeChannel
{
    /// <summary>Signals subscribed processes. Must never throw or block the caller.</summary>
    void Notify(string reason);

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for a signal. Returns true when woken,
    /// false on timeout; throws only when <paramref name="ct"/> is cancelled.
    /// </summary>
    Task<bool> WaitForWakeAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// In-process implementation: the default when Redis is disabled (local development,
/// tests, or a single-container deployment). Correct but only wakes the same process.
/// </summary>
public sealed class LocalPollWakeChannel : IPollWakeChannel
{
    private TaskCompletionSource _signal = NewSource();
    private int _pending;

    public void Notify(string reason)
    {
        // Latch first, then complete: a signal that arrives while no waiter exists
        // is remembered, so a commit that lands between two polls is not lost.
        Interlocked.Exchange(ref _pending, 1);

        var previous = Interlocked.Exchange(ref _signal, NewSource());
        previous.TrySetResult();
    }

    public async Task<bool> WaitForWakeAsync(TimeSpan timeout, CancellationToken ct)
    {
        var pending = Volatile.Read(ref _signal);

        // Consume a latched signal before waiting.
        if (Interlocked.Exchange(ref _pending, 0) == 1) return true;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeout, timeoutCts.Token);

        var winner = await Task.WhenAny(pending.Task, delay).ConfigureAwait(false);

        if (winner == pending.Task)
        {
            timeoutCts.Cancel();
            Interlocked.Exchange(ref _pending, 0);
            return true;
        }

        ct.ThrowIfCancellationRequested();

        // A signal may have landed in the gap between the latch check and the wait.
        if (Interlocked.Exchange(ref _pending, 0) == 1) return true;

        return false;
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Cross-process implementation: publishes and subscribes on a Redis channel derived
/// from <see cref="RedisSettings.InstanceName"/>, and raises the local signal when a
/// message arrives.
///
/// <para>Redis is already an ephemeral cache in this deployment and every call on it
/// is bounded and fail-soft, so this adds no new failure mode: if Redis is down, the
/// subscription is silent and polling degrades to its configured cadence.</para>
/// </summary>
public sealed class RedisPollWakeChannel : IPollWakeChannel, IDisposable
{
    private readonly ISubscriber _subscriber;
    private readonly RedisChannel _channel;
    private readonly LocalPollWakeChannel _local;
    private readonly ILogger<RedisPollWakeChannel> _logger;
    private int _subscribed;

    public RedisPollWakeChannel(
        IConnectionMultiplexer redis,
        RedisSettings settings,
        LocalPollWakeChannel local,
        ILogger<RedisPollWakeChannel> logger)
    {
        _local = local;
        _logger = logger;
        _subscriber = redis.GetSubscriber();
        _channel = RedisChannel.Literal(settings.InstanceName + "background:wake");

        try
        {
            _subscriber.Subscribe(_channel, (_, _) => _local.Notify("redis"));
            Interlocked.Exchange(ref _subscribed, 1);
        }
        catch (Exception ex)
        {
            // A cache that cannot subscribe must not stop the workers: they simply
            // fall back to their polling cadence.
            _logger.LogWarning(ex, "Background wake-up subscription failed; workers will poll on their configured cadence.");
        }
    }

    public void Notify(string reason)
    {
        if (Volatile.Read(ref _subscribed) == 0) return;

        try
        {
            // Fire-and-forget: the producer is inside a request (often inside a
            // transaction's commit path) and must never wait on Redis or fail because
            // Redis is unavailable.
            _subscriber.Publish(_channel, reason, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Background wake-up publish failed; polling remains the fallback.");
        }
    }

    public Task<bool> WaitForWakeAsync(TimeSpan timeout, CancellationToken ct) =>
        _local.WaitForWakeAsync(timeout, ct);

    public void Dispose()
    {
        if (Volatile.Read(ref _subscribed) == 0) return;

        try
        {
            _subscriber.Unsubscribe(_channel);
        }
        catch
        {
            // Disposing during shutdown: nothing to recover.
        }
    }
}
