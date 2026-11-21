namespace PunchedApi.Application.BackgroundProcessing;

/// <summary>
/// Runs a time-based sweep (expire stale payments, expire reschedule requests, rebuild
/// analytics, purge expired tokens) with the same operational discipline as
/// <see cref="AdaptivePollLoop"/>:
///
/// <list type="bullet">
///   <item>the interval comes from the worker's existing setting, so business
///   behaviour is unchanged;</item>
///   <item>jitter stops several replicas from sweeping in lock-step;</item>
///   <item>in scale-to-zero-friendly mode the period is rounded up to a whole number
///   of shared ticks and the loop only wakes on that tick, so every sweep lands in the
///   same wake window instead of each one waking the database on its own schedule.</item>
/// </list>
///
/// <para>Each cycle opens its own scope, so no connection is held between sweeps.</para>
/// </summary>
public static class PeriodicWorkerLoop
{
    private static readonly DateTimeOffset TickOrigin = DateTimeOffset.UtcNow;

    public static async Task RunAsync(
        string workerName,
        PollingProfile profile,
        TimeSpan configuredInterval,
        ILogger logger,
        BackgroundProcessingMetrics metrics,
        Func<CancellationToken, Task<int>> sweep,
        CancellationToken stoppingToken)
    {
        if (!profile.Enabled)
        {
            logger.LogInformation("Worker {Worker} is disabled by configuration.", workerName);
            return;
        }

        var configuredSeconds = Math.Max(5, (int)Math.Round(configuredInterval.TotalSeconds));

        // Rounding the period up to a whole number of shared ticks means the due time
        // of every sweep lands on a tick, so "due" and "awake" coincide.
        var effectiveSeconds = profile.ScaleToZeroFriendly
            ? PollingSharedTick.AlignPeriodSeconds(configuredSeconds, profile.EffectiveMaxIdleIntervalSeconds)
            : configuredSeconds;

        var effective = TimeSpan.FromSeconds(effectiveSeconds);
        var gridPeriod = Math.Max(60, profile.EffectiveMaxIdleIntervalSeconds);

        logger.LogInformation(
            "Worker {Worker} started: every {Interval}s{Adjusted} (configured {Configured}s), scale-to-zero-friendly={ScaleToZero}.",
            workerName,
            effectiveSeconds,
            effectiveSeconds == configuredSeconds
                ? string.Empty
                : $" (aligned from {configuredSeconds}s to the shared tick)",
            configuredSeconds,
            profile.ScaleToZeroFriendly);

        var nextDue = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;

            if (profile.ScaleToZeroFriendly && profile.AlignToSharedTick)
            {
                var tickDelay = PollingSharedTick.DelayUntilNextTick(
                    now, TickOrigin, gridPeriod, profile.JitterRatio, workerName);

                if (now < nextDue)
                {
                    // Not due yet: sleep to the next tick, then re-evaluate. This keeps
                    // one wake window for the whole container.
                    var wait = nextDue - now < tickDelay ? nextDue - now : tickDelay;
                    if (!await DelayAsync(wait, stoppingToken).ConfigureAwait(false)) break;
                    continue;
                }

                await SweepAsync(workerName, logger, metrics, sweep, stoppingToken).ConfigureAwait(false);
                nextDue = DateTimeOffset.UtcNow.Add(effective);
                metrics.RecordIdle(workerName, tickDelay.TotalSeconds);

                if (!await DelayAsync(tickDelay, stoppingToken).ConfigureAwait(false)) break;
                continue;
            }

            var rows = await SweepAsync(workerName, logger, metrics, sweep, stoppingToken).ConfigureAwait(false);
            if (rows < 0) break; // cancelled

            var jitter = Math.Clamp(profile.JitterRatio, 0, 0.5);
            var factor = jitter == 0 ? 1.0 : 1.0 + ((Random.Shared.NextDouble() * 2.0) - 1.0) * jitter;
            var delay = TimeSpan.FromSeconds(Math.Max(1.0, effective.TotalSeconds * factor));

            metrics.RecordIdle(workerName, rows > 0 ? 0 : delay.TotalSeconds);

            if (!await DelayAsync(delay, stoppingToken).ConfigureAwait(false)) break;
        }

        logger.LogInformation("Worker {Worker} stopped.", workerName);
    }

    private static async Task<int> SweepAsync(
        string workerName,
        ILogger logger,
        BackgroundProcessingMetrics metrics,
        Func<CancellationToken, Task<int>> sweep,
        CancellationToken stoppingToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var rows = 0;

        try
        {
            rows = await sweep(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return -1;
        }
        catch (Exception ex)
        {
            metrics.RecordFailure(workerName);
            // A transient database failure (for example Neon waking up) must not end
            // the worker; the next tick retries, and the sweeps themselves are
            // idempotent.
            logger.LogError(ex, "Worker {Worker} sweep failed; it will run again on the next interval.", workerName);
        }

        var durationMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        metrics.RecordCycle(workerName, new PollWorkResult(rows, rows), durationMs, 0);

        return rows;
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        if (delay <= TimeSpan.Zero) return true;

        try
        {
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
