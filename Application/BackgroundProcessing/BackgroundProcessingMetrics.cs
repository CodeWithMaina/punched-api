using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace PunchedApi.Application.BackgroundProcessing;

/// <summary>Outcome of one poll cycle.</summary>
/// <param name="Claimed">Rows claimed/discovered by this cycle.</param>
/// <param name="Processed">Rows fully processed by this cycle.</param>
/// <param name="MoreWorkAvailable">
/// The queue had at least as much work as the batch could take, so the next poll
/// should run immediately rather than ramp down.
/// </param>
public readonly record struct PollWorkResult(int Claimed, int Processed, bool MoreWorkAvailable = false)
{
    /// <summary>True when this cycle found anything to do (and should stay hot).</summary>
    public bool WorkFound => Claimed > 0 || Processed > 0 || MoreWorkAvailable;

    public static readonly PollWorkResult Empty = new(0, 0);
    public static readonly PollWorkResult Failed = new(0, 0);
}

/// <summary>
/// Lightweight, dependency-free telemetry for background processing, following the
/// <c>System.Diagnostics.Metrics</c> pattern already used by
/// <c>NotificationMetrics</c> and <c>MediaMetrics</c>.
///
/// <para>Everything is tagged by worker name, so an operator can answer "which worker
/// is keeping the database awake?" from the same instruments that report throughput.
/// No exporter is required: the counters are inert until something listens, exactly
/// like the existing meters.</para>
/// </summary>
public sealed class BackgroundProcessingMetrics : IDisposable
{
    /// <summary>
    /// Fallback for code paths that are constructed by hand (tests, tooling) rather
    /// than resolved from DI. The DI registration should always be preferred.
    /// </summary>
    public static readonly BackgroundProcessingMetrics Shared = new();

    private readonly Meter _meter = new("PunchedApi.BackgroundProcessing");

    private readonly Counter<long> _cycles;
    private readonly Counter<long> _discovered;
    private readonly Counter<long> _processed;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _wakeSignals;
    private readonly Histogram<double> _processingDuration;
    private readonly Histogram<double> _pollInterval;
    private readonly ConcurrentDictionary<string, double> _currentIntervalSeconds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _idleSeconds = new(StringComparer.Ordinal);

    public BackgroundProcessingMetrics()
    {
        _cycles = _meter.CreateCounter<long>(
            "background_poll_cycles_total", description: "Poll cycles executed, by worker.");
        _discovered = _meter.CreateCounter<long>(
            "background_jobs_discovered_total", description: "Jobs discovered/claimed, by worker.");
        _processed = _meter.CreateCounter<long>(
            "background_jobs_processed_total", description: "Jobs processed, by worker.");
        _failures = _meter.CreateCounter<long>(
            "background_poll_failures_total", description: "Poll cycles that threw, by worker.");
        _wakeSignals = _meter.CreateCounter<long>(
            "background_wake_signals_total", description: "Wake-up hints received, by worker.");
        _processingDuration = _meter.CreateHistogram<double>(
            "background_poll_cycle_duration_ms", "ms", "Wall-clock duration of one poll cycle, by worker.");
        _pollInterval = _meter.CreateHistogram<double>(
            "background_poll_interval_seconds", "s", "Delay chosen after a poll cycle, by worker.");

        _meter.CreateObservableGauge(
            "background_poll_current_interval_seconds",
            Observe(_currentIntervalSeconds),
            "s",
            "The delay currently in force for a worker (0 while active).");
        _meter.CreateObservableGauge(
            "background_idle_seconds",
            Observe(_idleSeconds),
            "s",
            "How long a worker has been idle; 0 while it is finding work.");
    }

    public void RecordCycle(string worker, PollWorkResult result, double durationMs, double delaySeconds)
    {
        var tag = new KeyValuePair<string, object?>("worker", worker);
        _cycles.Add(1, tag);
        _processingDuration.Record(durationMs, tag);
        _pollInterval.Record(delaySeconds, tag);

        if (result.Claimed > 0) _discovered.Add(result.Claimed, tag);
        if (result.Processed > 0) _processed.Add(result.Processed, tag);

        _currentIntervalSeconds[worker] = delaySeconds;
        if (result.WorkFound) _idleSeconds[worker] = 0;
    }

    public void RecordFailure(string worker) =>
        _failures.Add(1, new KeyValuePair<string, object?>("worker", worker));

    public void RecordWake(string worker) =>
        _wakeSignals.Add(1, new KeyValuePair<string, object?>("worker", worker));

    public void RecordIdle(string worker, double idleSeconds) => _idleSeconds[worker] = idleSeconds;

    private static Func<IEnumerable<Measurement<double>>> Observe(
        ConcurrentDictionary<string, double> values) =>
        () => values.Select(pair =>
            new Measurement<double>(pair.Value, new KeyValuePair<string, object?>("worker", pair.Key)));

    public void Dispose() => _meter.Dispose();
}
