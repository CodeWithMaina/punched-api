namespace PunchedApi.Application.BackgroundProcessing;

/// <summary>
/// The idle backoff state machine behind adaptive polling. Pure and deterministic
/// (inject <paramref name="random"/> to remove jitter in tests), so every boundary
/// — ramp progression, jitter bounds, reset, scale-to-zero floor — is unit-testable
/// without a clock, a database or a running host.
/// </summary>
public sealed class PollingSchedule
{
    private readonly PollingProfile _profile;
    private readonly Func<double> _random;

    private double _baseIdleSeconds;
    private double _idleElapsedSeconds;

    public PollingSchedule(PollingProfile profile, Func<double>? random = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _random = random ?? Random.Shared.NextDouble;
        CurrentDelay = TimeSpan.FromSeconds(Math.Max(1, profile.ActiveIntervalSeconds));
    }

    /// <summary>Number of consecutive polls that found nothing.</summary>
    public int ConsecutiveEmptyPolls { get; private set; }

    /// <summary>Wall-clock seconds spent in the current idle streak (sum of delays).</summary>
    public double IdleElapsedSeconds => _idleElapsedSeconds;

    /// <summary>The delay most recently returned.</summary>
    public TimeSpan CurrentDelay { get; private set; }

    /// <summary>
    /// Computes the next delay. <paramref name="workFound"/> resets the ramp: a poll
    /// that discovered (or drained) work is followed by the short active interval, so
    /// a backlog is processed promptly.
    /// </summary>
    public TimeSpan Next(bool workFound)
    {
        if (workFound)
        {
            Reset();
            CurrentDelay = Jittered(_profile.ActiveIntervalSeconds, symmetrical: true);
            return CurrentDelay;
        }

        ConsecutiveEmptyPolls++;

        var maxIdle = _profile.EffectiveMaxIdleIntervalSeconds;

        if (ConsecutiveEmptyPolls == 1)
        {
            _baseIdleSeconds = _profile.FirstIdleIntervalSeconds;
        }
        else if (_profile.MaxIdleDurationSeconds > 0 && _idleElapsedSeconds >= _profile.MaxIdleDurationSeconds)
        {
            // The operator said "reach deep idle after N seconds"; honour it exactly
            // instead of letting the multiplier decide when the night begins.
            _baseIdleSeconds = maxIdle;
        }
        else
        {
            _baseIdleSeconds = Math.Min(
                Math.Max(_baseIdleSeconds, _profile.FirstIdleIntervalSeconds) * _profile.BackoffMultiplier,
                maxIdle);
        }

        _baseIdleSeconds = Math.Clamp(_baseIdleSeconds, 1, maxIdle);

        // Scale-to-zero mode only ever jitters upward: a symmetric jitter could pull
        // the interval back under Neon's 5-minute window and keep the compute awake.
        var symmetrical = !_profile.ScaleToZeroFriendly;
        CurrentDelay = Jittered(_baseIdleSeconds, symmetrical);

        if (_profile.ScaleToZeroFriendly)
        {
            var floor = TimeSpan.FromSeconds(Math.Max(1, _profile.ScaleToZeroIdleFloorSeconds));
            if (CurrentDelay < floor) CurrentDelay = floor;
        }

        _idleElapsedSeconds += CurrentDelay.TotalSeconds;
        return CurrentDelay;
    }

    /// <summary>
    /// Delay after a wake signal that found nothing yet (the row was committed a few
    /// milliseconds after the producer signalled). Stay at the first idle interval —
    /// the commit race must not cost a full deep-idle wait.
    /// </summary>
    public TimeSpan NextAfterWake()
    {
        ConsecutiveEmptyPolls = 0;
        _baseIdleSeconds = _profile.FirstIdleIntervalSeconds;
        _idleElapsedSeconds = 0;
        CurrentDelay = Jittered(_profile.FirstIdleIntervalSeconds, symmetrical: true);

        if (_profile.ScaleToZeroFriendly)
        {
            // Fast polling for a short grace window is a deliberate exception to the
            // floor: it is bounded by WakeGraceSeconds, not by the idle cadence.
            var grace = TimeSpan.FromSeconds(Math.Max(1, _profile.WakeGraceSeconds));
            if (CurrentDelay > grace) CurrentDelay = grace;
        }

        return CurrentDelay;
    }

    /// <summary>Back to the active interval (work was found, or the worker restarted).</summary>
    public void Reset()
    {
        ConsecutiveEmptyPolls = 0;
        _baseIdleSeconds = 0;
        _idleElapsedSeconds = 0;
    }

    private TimeSpan Jittered(double seconds, bool symmetrical)
    {
        var ratio = Math.Clamp(_profile.JitterRatio, 0, 0.5);
        var factor = ratio == 0
            ? 1.0
            : symmetrical
                ? 1.0 + ((_random() * 2.0) - 1.0) * ratio
                : 1.0 + (_random() * ratio);

        var jittered = Math.Max(1.0, seconds * factor);
        return TimeSpan.FromSeconds(jittered);
    }
}
