using System.Diagnostics;

namespace PunchedApi.Application.BackgroundProcessing;

/// <summary>
/// Runs a queue-draining worker with adaptive polling.
///
/// <para><b>Work available:</b> poll immediately, keep the short active interval, and
/// let the caller keep its existing batch size, atomic claim (<c>FOR UPDATE SKIP
/// LOCKED</c>), lease, retry and dead-letter behaviour.</para>
///
/// <para><b>No work:</b> escalate 1 → first idle → ×multiplier → ceiling, with
/// bounded jitter, and hold no connection or transaction while waiting (each poll
/// creates and disposes its own scope). In scale-to-zero-friendly mode the idle
/// cadence is lifted to the floor and aligned to one shared tick, so the database is
/// woken once per period instead of once per worker.</para>
///
/// <para><b>Wake-up:</b> if a wake channel is configured, the wait is raced against a
/// signal from the process that just committed an outbox row, so deep idle costs
/// nothing in latency. The signal is only a hint; the database stays authoritative.</para>
/// </summary>
public static class AdaptivePollLoop
{
    /// <summary>Process-wide anchor for the shared idle tick.</summary>
    private static readonly DateTimeOffset TickOrigin = DateTimeOffset.UtcNow;

    public static async Task RunAsync(
        string workerName,
        PollingProfile profile,
        ILogger logger,
        BackgroundProcessingMetrics metrics,
        Func<CancellationToken, Task<PollWorkResult>> poll,
        IPollWakeChannel? wakeChannel,
        CancellationToken stoppingToken)
    {
        if (!profile.Enabled)
        {
            logger.LogInformation("Worker {Worker} is disabled by configuration.", workerName);
            return;
        }

        var schedule = new PollingSchedule(profile);
        logger.LogInformation("Worker {Worker} started: {Profile}", workerName, profile.Describe());

        var wasIdle = false;
        DateTimeOffset? hotUntil = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            PollWorkResult result;

            try
            {
                result = await poll(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A transient database failure (Neon waking, a dropped pooled socket,
                // a network blip) must never end the worker: the rows are durable, so
                // the next cycle retries them. The backoff below applies, which also
                // prevents an outage from turning into a retry storm.
                metrics.RecordFailure(workerName);
                logger.LogError(ex, "Worker {Worker} poll failed; the cycle will be retried with backoff.", workerName);
                result = PollWorkResult.Failed;
            }

            var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var delay = NextDelay(workerName, profile, schedule, result.WorkFound, ref hotUntil);

            metrics.RecordCycle(workerName, result, durationMs, delay.TotalSeconds);
            if (!result.WorkFound) metrics.RecordIdle(workerName, schedule.IdleElapsedSeconds);

            if (result.WorkFound && wasIdle)
                logger.LogInformation("Worker {Worker} found work; resuming active polling.", workerName);
            else if (!result.WorkFound && !wasIdle)
                logger.LogInformation(
                    "Worker {Worker} has no work; entering idle backoff (ceiling {Ceiling}s).",
                    workerName, profile.EffectiveMaxIdleIntervalSeconds);

            wasIdle = !result.WorkFound;

            try
            {
                if (wakeChannel is null)
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var woken = await wakeChannel.WaitForWakeAsync(delay, stoppingToken).ConfigureAwait(false);
                if (!woken) continue;

                metrics.RecordWake(workerName);
                schedule.Reset();
                hotUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, profile.WakeGraceSeconds));
                logger.LogDebug("Worker {Worker} woken by an outbox signal after {Idle:0.0}s idle.", workerName, schedule.IdleElapsedSeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("Worker {Worker} stopped.", workerName);
    }

    private static TimeSpan NextDelay(
        string workerName,
        PollingProfile profile,
        PollingSchedule schedule,
        bool workFound,
        ref DateTimeOffset? hotUntil)
    {
        var now = DateTimeOffset.UtcNow;
        if (hotUntil is not null && now >= hotUntil) hotUntil = null;

        var delay = schedule.Next(workFound);

        if (workFound) return delay;

        var insideWakeGrace = hotUntil is not null && now < hotUntil.Value;
        if (profile.ScaleToZeroFriendly && profile.AlignToSharedTick && !insideWakeGrace)
        {
            return PollingSharedTick.DelayUntilNextTick(
                now,
                TickOrigin,
                profile.EffectiveMaxIdleIntervalSeconds,
                profile.JitterRatio,
                workerName);
        }

        return delay;
    }
}
