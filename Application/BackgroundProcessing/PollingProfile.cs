namespace PunchedApi.Application.BackgroundProcessing;

/// <summary>
/// Polling behaviour for one background worker.
///
/// <para><b>Two independent questions, two independent knobs.</b></para>
/// <list type="number">
///   <item><b>How fast must work be discovered?</b> → <see cref="MaxIdleIntervalSeconds"/>.
///   This is a business/latency decision and has nothing to do with the database plan.</item>
///   <item><b>May the database suspend while nothing happens?</b> →
///   <see cref="ScaleToZeroFriendly"/>. When true, every worker's idle cadence is
///   lifted to at least <see cref="ScaleToZeroIdleFloorSeconds"/> and aligned to one
///   shared tick, because Neon only suspends a compute after five minutes of
///   inactivity: a worker that polls every 30 s keeps the compute awake 24/7 and
///   burns the monthly compute-hour allowance.</item>
/// </list>
///
/// <para>Both are configuration. A paid Neon plan simply sets
/// <c>BackgroundProcessing__ScaleToZeroFriendlyMode=false</c> (and a small
/// <c>MaxIdleIntervalSeconds</c>) — no code change, no schema change.</para>
///
/// <para>Every property is bindable from configuration; the values here are the
/// code defaults used when nothing is configured. A per-worker section overlays the
/// shared section, so an operator only writes the keys that differ.</para>
/// </summary>
public sealed class PollingProfile
{
    /// <summary>Run this worker at all. False logs one line and returns.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after a poll that found (and processed) work. Keep it short.</summary>
    public int ActiveIntervalSeconds { get; set; } = 2;

    /// <summary>First delay after a poll that found nothing.</summary>
    public int FirstIdleIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Worst-case idle discovery latency, and the ceiling of the backoff ramp.
    /// This is the notification-latency knob: lower it for faster delivery, raise it
    /// (or enable <see cref="ScaleToZeroFriendly"/>) to let the database sleep.
    /// </summary>
    public int MaxIdleIntervalSeconds { get; set; } = 60;

    /// <summary>Idle backoff multiplier (bounded exponential backoff).</summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Randomisation applied to every delay, as a fraction of the delay
    /// (0.15 = ±15 %). Stops several worker replicas and several workers from
    /// hammering the database in lock-step.
    /// </summary>
    public double JitterRatio { get; set; } = 0.15;

    /// <summary>
    /// How long the ramp may take to reach <see cref="MaxIdleIntervalSeconds"/>.
    /// Zero means "use the backoff multiplier immediately"; a positive value skips
    /// straight to the ceiling once this much idle time has elapsed, so a quiet
    /// night reaches deep idle predictably instead of depending on the multiplier.
    /// </summary>
    public int MaxIdleDurationSeconds { get; set; } = 300;

    /// <summary>
    /// When true, idle polling is raised to at least
    /// <see cref="ScaleToZeroIdleFloorSeconds"/> and aligned to a shared tick so the
    /// whole worker container wakes the database once per period instead of once
    /// per worker.
    /// </summary>
    public bool ScaleToZeroFriendly { get; set; }

    /// <summary>
    /// Minimum idle cadence while <see cref="ScaleToZeroFriendly"/> is on.
    /// Neon Free always suspends after 5 minutes of inactivity, so the floor must be
    /// comfortably above 300 s for the compute to actually reach the suspended
    /// state; 360 s leaves a minute of slack for the wake itself.
    /// </summary>
    public int ScaleToZeroIdleFloorSeconds { get; set; } = 360;

    /// <summary>
    /// Align idle polls to one shared tick (default). With a shared tick every idle
    /// worker runs in the same short window, so the compute sees one wake per
    /// period rather than one per worker.
    /// </summary>
    public bool AlignToSharedTick { get; set; } = true;

    /// <summary>
    /// After a wake signal (an outbox row was just committed by another process),
    /// stay at <see cref="FirstIdleIntervalSeconds"/> for this many seconds instead of
    /// jumping straight back to deep idle: a second row usually follows the first.
    /// </summary>
    public int WakeGraceSeconds { get; set; } = 30;

    /// <summary>
    /// Optional per-worker batch size. Null means "use the worker's own default".
    /// Exposed here so queue depth can be tuned with the polling cadence.
    /// </summary>
    public int? BatchSize { get; set; }

    /// <summary>
    /// Optional per-worker concurrency. Null means "use the worker's own default".
    /// The existing claim/lease guarantees make concurrency safe; this only bounds it.
    /// </summary>
    public int? Concurrency { get; set; }

    /// <summary>
    /// The idle ceiling actually in force.
    ///
    /// <para>In scale-to-zero-friendly mode the ceiling is raised to at least
    /// <see cref="ScaleToZeroIdleFloorSeconds"/> and quantised to a whole number of
    /// ticks, so every worker's cadence lands on the same absolute lattice
    /// (<c>origin + k × floor</c>). That is what turns eight independent loops into
    /// one wake window per period.</para>
    /// </summary>
    public int EffectiveMaxIdleIntervalSeconds
    {
        get
        {
            if (!ScaleToZeroFriendly)
            {
                return Math.Max(Math.Max(1, MaxIdleIntervalSeconds), Math.Max(1, FirstIdleIntervalSeconds));
            }

            var floor = Math.Max(60, ScaleToZeroIdleFloorSeconds);
            var ceiling = Math.Max(Math.Max(1, MaxIdleIntervalSeconds), floor);
            return PollingSharedTick.AlignPeriodSeconds(ceiling, floor);
        }
    }

    /// <summary>
    /// Validates the profile. Called at startup for every registered worker, so a
    /// nonsensical combination fails the deployment instead of producing a busy loop
    /// (or a worker that never polls) in production.
    /// </summary>
    public string? Validate(string workerName)
    {
        if (!Enabled) return null;

        if (ActiveIntervalSeconds < 1)
            return $"{workerName}: ActiveIntervalSeconds must be >= 1 (got {ActiveIntervalSeconds}).";
        if (FirstIdleIntervalSeconds < ActiveIntervalSeconds)
            return $"{workerName}: FirstIdleIntervalSeconds ({FirstIdleIntervalSeconds}) must be >= ActiveIntervalSeconds ({ActiveIntervalSeconds}).";
        if (MaxIdleIntervalSeconds < FirstIdleIntervalSeconds)
            return $"{workerName}: MaxIdleIntervalSeconds ({MaxIdleIntervalSeconds}) must be >= FirstIdleIntervalSeconds ({FirstIdleIntervalSeconds}).";
        if (BackoffMultiplier < 1.0 || BackoffMultiplier > 10.0)
            return $"{workerName}: BackoffMultiplier must be between 1 and 10 (got {BackoffMultiplier}).";
        if (JitterRatio < 0 || JitterRatio > 0.5)
            return $"{workerName}: JitterRatio must be between 0 and 0.5 (got {JitterRatio}).";
        if (MaxIdleDurationSeconds < 0 || MaxIdleDurationSeconds > 86_400)
            return $"{workerName}: MaxIdleDurationSeconds must be between 0 and 86400 (got {MaxIdleDurationSeconds}).";
        if (ScaleToZeroFriendly && ScaleToZeroIdleFloorSeconds < 60)
            return $"{workerName}: ScaleToZeroIdleFloorSeconds must be >= 60 when scale-to-zero-friendly mode is on (got {ScaleToZeroIdleFloorSeconds}).";
        if (ScaleToZeroFriendly && ScaleToZeroIdleFloorSeconds <= 300)
            return $"{workerName}: ScaleToZeroIdleFloorSeconds ({ScaleToZeroIdleFloorSeconds}) is not above Neon's 5-minute idle window; " +
                   "the compute would never suspend. Use at least 360 seconds, or disable scale-to-zero-friendly mode.";
        if (WakeGraceSeconds < 0 || WakeGraceSeconds > 600)
            return $"{workerName}: WakeGraceSeconds must be between 0 and 600 (got {WakeGraceSeconds}).";
        if (BatchSize is <= 0)
            return $"{workerName}: BatchSize must be positive when set (got {BatchSize}).";
        if (Concurrency is <= 0)
            return $"{workerName}: Concurrency must be positive when set (got {Concurrency}).";

        return null;
    }

    /// <summary>One-line description for the startup configuration summary (no secrets).</summary>
    public string Describe()
    {
        var mode = ScaleToZeroFriendly
            ? $"scale-to-zero-friendly (idle floor {EffectiveMaxIdleIntervalSeconds}s, shared tick)"
            : "latency-first";
        return $"active {ActiveIntervalSeconds}s -> idle {FirstIdleIntervalSeconds}s x{BackoffMultiplier:0.##} " +
               $"up to {EffectiveMaxIdleIntervalSeconds}s, jitter {JitterRatio * 100:0.#}%, {mode}";
    }
}
