using Microsoft.Extensions.Logging.Abstractions;
using PunchedApi.Application.BackgroundProcessing;

namespace PunchedApi.Tests.BackgroundProcessing;

public class AdaptivePollLoopTests
{
    private static PollingProfile LatencyFirst(int active = 1, int firstIdle = 1, int maxIdle = 1) => new()
    {
        ActiveIntervalSeconds = active,
        FirstIdleIntervalSeconds = firstIdle,
        MaxIdleIntervalSeconds = maxIdle,
        BackoffMultiplier = 1,
        JitterRatio = 0,
        MaxIdleDurationSeconds = 0,
        ScaleToZeroFriendly = false
    };

    [Fact]
    public async Task Work_found_keeps_the_worker_on_the_active_interval()
    {
        var polls = 0;
        using var cts = new CancellationTokenSource();

        var loop = AdaptivePollLoop.RunAsync(
            "test-worker",
            LatencyFirst(),
            NullLogger.Instance,
            BackgroundProcessingMetrics.Shared,
            _ =>
            {
                Interlocked.Increment(ref polls);
                return Task.FromResult(new PollWorkResult(5, 5, MoreWorkAvailable: true));
            },
            wakeChannel: null,
            cts.Token);

        await WaitUntilAsync(() => Volatile.Read(ref polls) >= 3, TimeSpan.FromSeconds(5));
        cts.Cancel();
        await loop;

        Assert.True(Volatile.Read(ref polls) >= 3);
    }

    [Fact]
    public async Task Cancellation_during_the_idle_wait_ends_the_loop_promptly()
    {
        var polls = 0;
        using var cts = new CancellationTokenSource();

        var loop = AdaptivePollLoop.RunAsync(
            "test-worker",
            LatencyFirst(firstIdle: 30, maxIdle: 30),
            NullLogger.Instance,
            BackgroundProcessingMetrics.Shared,
            _ =>
            {
                Interlocked.Increment(ref polls);
                return Task.FromResult(PollWorkResult.Empty);
            },
            wakeChannel: null,
            cts.Token);

        await WaitUntilAsync(() => Volatile.Read(ref polls) >= 1, TimeSpan.FromSeconds(2));

        var startedAt = DateTime.UtcNow;
        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True((DateTime.UtcNow - startedAt).TotalSeconds < 5);
        Assert.Equal(1, Volatile.Read(ref polls));
    }

    [Fact]
    public async Task A_transient_failure_does_not_stop_the_worker()
    {
        var polls = 0;
        using var cts = new CancellationTokenSource();

        var loop = AdaptivePollLoop.RunAsync(
            "test-worker",
            LatencyFirst(),
            NullLogger.Instance,
            BackgroundProcessingMetrics.Shared,
            _ =>
            {
                var attempt = Interlocked.Increment(ref polls);
                if (attempt == 1)
                {
                    throw new TimeoutException("simulated cold-start timeout");
                }

                return Task.FromResult(PollWorkResult.Empty);
            },
            wakeChannel: null,
            cts.Token);

        await WaitUntilAsync(() => Volatile.Read(ref polls) >= 2, TimeSpan.FromSeconds(5));
        cts.Cancel();
        await loop;

        Assert.True(Volatile.Read(ref polls) >= 2, "a transient database failure must be retried, not fatal");
    }

    [Fact]
    public async Task A_wake_signal_shortens_the_idle_wait()
    {
        var polls = 0;
        var channel = new LocalPollWakeChannel();
        using var cts = new CancellationTokenSource();

        var loop = AdaptivePollLoop.RunAsync(
            "test-worker",
            LatencyFirst(active: 1, firstIdle: 30, maxIdle: 30),
            NullLogger.Instance,
            BackgroundProcessingMetrics.Shared,
            _ =>
            {
                Interlocked.Increment(ref polls);
                return Task.FromResult(PollWorkResult.Empty);
            },
            channel,
            cts.Token);

        await WaitUntilAsync(() => Volatile.Read(ref polls) >= 1, TimeSpan.FromSeconds(2));

        // Without the hint this worker would sleep for 30 s.
        channel.Notify("test");

        await WaitUntilAsync(() => Volatile.Read(ref polls) >= 2, TimeSpan.FromSeconds(3));
        cts.Cancel();
        await loop;

        Assert.True(Volatile.Read(ref polls) >= 2);
    }

    [Fact]
    public async Task A_disabled_profile_returns_without_polling()
    {
        var polls = 0;
        var profile = LatencyFirst();
        profile.Enabled = false;

        await AdaptivePollLoop.RunAsync(
            "test-worker",
            profile,
            NullLogger.Instance,
            BackgroundProcessingMetrics.Shared,
            _ =>
            {
                Interlocked.Increment(ref polls);
                return Task.FromResult(PollWorkResult.Empty);
            },
            wakeChannel: null,
            CancellationToken.None);

        Assert.Equal(0, Volatile.Read(ref polls));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
    }
}
