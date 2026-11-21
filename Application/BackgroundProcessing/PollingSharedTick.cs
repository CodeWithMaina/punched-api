namespace PunchedApi.Application.BackgroundProcessing;

/// <summary>
/// The shared idle tick used when scale-to-zero-friendly mode is on.
///
/// <para>Neon suspends a compute only after five minutes without activity, and every
/// query restarts that timer. Eight workers each polling on their own schedule
/// therefore keep the compute permanently awake even at "deep idle". Collapsing them
/// onto one tick — once per period, with a few seconds of deterministic spread so the
/// processes do not stampede — makes the idle state genuinely idle: one wake window
/// per period instead of one wake per worker.</para>
///
/// <para>Pure function of (now, origin, period), so the behaviour is tested directly.</para>
/// </summary>
public static class PollingSharedTick
{
    /// <summary>Maximum spread (seconds) added after a tick to de-synchronise workers.</summary>
    private const double MaxSpreadSeconds = 5.0;

    /// <summary>
    /// Time until the next shared tick. Never returns less than one second, so a
    /// clock that lands exactly on a tick cannot produce a busy loop.
    /// </summary>
    public static TimeSpan DelayUntilNextTick(
        DateTimeOffset now,
        DateTimeOffset origin,
        int periodSeconds,
        double jitterRatio,
        string workerName)
    {
        var period = Math.Max(60, periodSeconds);
        var elapsed = (now - origin).TotalSeconds;

        var completedTicks = Math.Floor(elapsed / period);
        var nextTickAt = (completedTicks + 1) * period;
        var delay = nextTickAt - elapsed;

        if (delay < 1.0) delay = period;

        // Deterministic per-worker spread (0..MaxSpreadSeconds): all idle workers wake
        // in the same window, but not in the same millisecond.
        var spread = Math.Min(MaxSpreadSeconds, period * Math.Clamp(jitterRatio, 0, 0.5));
        if (spread > 0)
        {
            var fraction = (uint)StringComparer.Ordinal.GetHashCode(workerName) % 1000 / 1000.0;
            delay += spread * fraction;
        }

        return TimeSpan.FromSeconds(delay);
    }

    /// <summary>
    /// Quantises a worker's period onto the shared tick lattice, <b>never lengthening
    /// it</b>: a documented maximum delay (for example "reschedule requests expire
    /// within 15 minutes") must not become 18 minutes just because the tick is 6.
    ///
    /// <para>900 s with a 360 s tick becomes 720 s (two ticks), not 1080 s. A period
    /// shorter than one tick cannot be honoured without waking the database more often
    /// than the tick allows, so it becomes exactly one tick — and the caller logs that
    /// adjustment at startup so it is never silent.</para>
    /// </summary>
    public static int AlignPeriodSeconds(int periodSeconds, int tickSeconds)
    {
        var tick = Math.Max(60, tickSeconds);
        var period = Math.Max(1, periodSeconds);

        if (period <= tick) return tick;

        var wholeTicks = (int)Math.Floor(period / (double)tick);
        return Math.Max(1, wholeTicks) * tick;
    }
}
