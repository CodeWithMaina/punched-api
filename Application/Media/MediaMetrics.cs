using System.Diagnostics;
using System.Diagnostics.Metrics;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Media;

/// <summary>
/// Bounded, safe-to-export dimensions for media metrics.
///
/// LABEL SAFETY: every value here is drawn from a small, closed set. Media IDs, business IDs,
/// user IDs, filenames, object keys, presigned URLs and delivery URLs are NEVER used as metric
/// tags — an ID-valued tag creates one time series per upload, which is both a cardinality
/// explosion and an information leak. Per-request correlation lives in structured logs and
/// traces instead (see <see cref="MediaLifecycleLogger"/>).
/// </summary>
public static class MediaMetricDimensions
{
    /// <summary>Closed set of upload purposes, so the tag can never be attacker-controlled.</summary>
    public static string Purpose(string? purpose) =>
        MediaPurposes.All.Contains(purpose ?? string.Empty) ? purpose! : "unknown";

    /// <summary>Coarse file-size bands. Keeps tag cardinality at 5 regardless of actual size.</summary>
    public static string SizeBand(long? bytes)
    {
        if (bytes is null or <= 0) return "unknown";
        return bytes.Value switch
        {
            <= 100 * 1024 => "le100kb",
            <= 1024 * 1024 => "le1mb",
            <= 5 * 1024 * 1024 => "le5mb",
            <= 10 * 1024 * 1024 => "le10mb",
            _ => "gt10mb"
        };
    }

    /// <summary>Variant-count band: bounded, but still grouped so the tag set stays stable.</summary>
    public static string VariantBand(int count) => count switch
    {
        <= 0 => "0",
        <= 2 => "1-2",
        <= 4 => "3-4",
        <= 6 => "5-6",
        _ => "gt6"
    };

    /// <summary>Attempt-number band, so a retry counter cannot grow unbounded cardinality.</summary>
    public static string AttemptBand(int attempt) => attempt switch
    {
        <= 1 => "1",
        <= 2 => "2",
        <= 3 => "3",
        _ => "gt3"
    };

    public static string Outcome(string? outcome) => string.IsNullOrWhiteSpace(outcome) ? "unknown" : outcome!;
}

/// <summary>
/// Media lifecycle telemetry. All instruments hang off the existing <c>Punched.Media</c>
/// meter, so a deployment can export them with whatever collector it already attaches
/// (OpenTelemetry, Prometheus via a meter listener, etc.) — no new monitoring vendor is
/// introduced by this file.
///
/// FAILURE ISOLATION: every public method is defensive. Recording a metric must never be able
/// to fail an upload or a processing attempt, so instrument writes are wrapped and any
/// exception is swallowed at the call boundary.
/// </summary>
public static class MediaMetrics
{
    /// <summary>Name of the pre-existing meter this code contributes to. Export this meter.</summary>
    public const string MeterName = "Punched.Media";

    public static readonly Meter Meter = new(MeterName);

    // ── Pre-existing lifecycle counters ────────────────────────────────────
    private static readonly Counter<long> UploadCreated = Meter.CreateCounter<long>("media.upload.created");
    private static readonly Counter<long> UploadCompleted = Meter.CreateCounter<long>("media.upload.completed");
    private static readonly Counter<long> ProcessingStarted = Meter.CreateCounter<long>("media.processing.started");
    private static readonly Counter<long> ProcessingSucceeded = Meter.CreateCounter<long>("media.processing.succeeded");
    private static readonly Counter<long> ProcessingFailed = Meter.CreateCounter<long>("media.processing.failed");
    private static readonly Counter<long> ProcessingRetry = Meter.CreateCounter<long>("media.processing.retry");
    private static readonly Counter<long> CleanupDeleted = Meter.CreateCounter<long>("media.cleanup.deleted");
    private static readonly Counter<long> CleanupFailed = Meter.CreateCounter<long>("media.cleanup.failed");

    // ── Latency histograms ─────────────────────────────────────────────────
    /// <summary>Server-side duration of POST /v1/media/uploads.</summary>
    private static readonly Histogram<double> UploadIntentDuration = Meter.CreateHistogram<double>("media.upload.intent.duration.ms");
    /// <summary>Server-side duration of POST /v1/media/{id}/complete (HEAD + state transition).</summary>
    private static readonly Histogram<double> CompletionDuration = Meter.CreateHistogram<double>("media.upload.completion.duration.ms");
    /// <summary>Pure image work. Excludes queue delay by construction.</summary>
    private static readonly Histogram<double> ProcessingDuration = Meter.CreateHistogram<double>("media.processing.duration.ms");
    /// <summary>Completion → worker claim. Pure waiting, never mixed into processing duration.</summary>
    private static readonly Histogram<double> QueueDelay = Meter.CreateHistogram<double>("media.processing.queue.delay.ms");
    /// <summary>GET /v1/media/public/{id} latency.</summary>
    private static readonly Histogram<double> RetrievalDuration = Meter.CreateHistogram<double>("media.retrieval.duration.ms");
    /// <summary>
    /// Upload acceptance → Ready, computed from two SERVER-side timestamps on the same row
    /// (single clock domain). The headline "how long until this image is usable" figure.
    /// </summary>
    private static readonly Histogram<double> UploadToReadyDuration = Meter.CreateHistogram<double>("media.upload_to_ready.duration.ms");
    /// <summary>How long a stale lease was held before the cleanup worker reclaimed the row.</summary>
    private static readonly Histogram<double> StaleLeaseAge = Meter.CreateHistogram<double>("media.processing.stale_lease.age.ms");
    /// <summary>Size of the accepted original, for cost/throughput analysis.</summary>
    private static readonly Histogram<long> SourceBytes = Meter.CreateHistogram<long>("media.source.bytes");

    // ── Failure / recovery counters ────────────────────────────────────────
    private static readonly Counter<long> UploadIntentFailed = Meter.CreateCounter<long>("media.upload.intent.failed");
    private static readonly Counter<long> CompletionFailed = Meter.CreateCounter<long>("media.upload.completion.failed");
    private static readonly Counter<long> CompletionReplayed = Meter.CreateCounter<long>("media.upload.completion.replayed");
    private static readonly Counter<long> StaleLeasesReclaimed = Meter.CreateCounter<long>("media.processing.stale_lease.reclaimed");
    private static readonly Counter<long> ProcessingRecovered = Meter.CreateCounter<long>("media.processing.recovered");
    private static readonly Counter<long> AbandonedPendingPurged = Meter.CreateCounter<long>("media.cleanup.abandoned_pending.purged");
    private static readonly Counter<long> VariantsGenerated = Meter.CreateCounter<long>("media.variants.generated");

    private static readonly KeyValuePair<string, object?>[] NoTags = [];

    // ── Pre-existing recording API (call signatures preserved) ─────────────
    public static void RecordUploadCreated() => SafeAdd(UploadCreated, 1, NoTags);
    public static void RecordUploadCompleted() => SafeAdd(UploadCompleted, 1, NoTags);
    public static void RecordProcessingStarted() => SafeAdd(ProcessingStarted, 1, NoTags);
    public static void RecordProcessingSucceeded() => SafeAdd(ProcessingSucceeded, 1, NoTags);
    public static void RecordProcessingFailed() => SafeAdd(ProcessingFailed, 1, NoTags);
    public static void RecordProcessingRetry() => SafeAdd(ProcessingRetry, 1, NoTags);
    public static void RecordCleanupDeleted() => SafeAdd(CleanupDeleted, 1, NoTags);
    public static void RecordCleanupFailed() => SafeAdd(CleanupFailed, 1, NoTags);

    /// <summary>Pure image-processing duration. Queue delay is recorded separately.</summary>
    public static void RecordProcessingDuration(TimeSpan elapsed, string purpose)
        => SafeRecord(ProcessingDuration, elapsed.TotalMilliseconds, PurposeTags(purpose));

    // ── New recording API ──────────────────────────────────────────────────

    /// <summary>
    /// POST /v1/media/uploads latency. Timed inside the action method, so it covers only
    /// server work: validation, owner resolution, idempotency lookup, INSERT and presign.
    /// </summary>
    public static void RecordUploadIntentDuration(TimeSpan elapsed, string purpose, bool success, long? declaredBytes)
        => SafeRecord(UploadIntentDuration, elapsed.TotalMilliseconds, PurposeTags(purpose,
            ("outcome", success ? "success" : "failure"),
            ("size_band", MediaMetricDimensions.SizeBand(declaredBytes))));

    /// <summary>POST /v1/media/{id}/complete latency: the HEAD probe plus the state transition.</summary>
    public static void RecordCompletionDuration(TimeSpan elapsed, string purpose, string outcome)
        => SafeRecord(CompletionDuration, elapsed.TotalMilliseconds, PurposeTags(purpose, ("outcome", MediaMetricDimensions.Outcome(outcome))));

    /// <summary>Delay between completion marking the row Uploaded and the worker claiming it.</summary>
    public static void RecordQueueDelay(TimeSpan elapsed, string purpose)
        => SafeRecord(QueueDelay, elapsed.TotalMilliseconds, PurposeTags(purpose));

    /// <summary>GET /v1/media/public/{id} latency.</summary>
    public static void RecordRetrievalDuration(TimeSpan elapsed, string purpose, string outcome)
        => SafeRecord(RetrievalDuration, elapsed.TotalMilliseconds, PurposeTags(purpose, ("outcome", MediaMetricDimensions.Outcome(outcome))));

    /// <summary>Upload acceptance → Ready, from two server-side timestamps in one clock domain.</summary>
    public static void RecordUploadToReady(TimeSpan elapsed, string purpose)
        => SafeRecord(UploadToReadyDuration, elapsed.TotalMilliseconds, PurposeTags(purpose));

    /// <summary>How long a stale lease was held before the cleanup worker reclaimed it.</summary>
    public static void RecordStaleLeaseAge(TimeSpan elapsed, string purpose)
        => SafeRecord(StaleLeaseAge, elapsed.TotalMilliseconds, PurposeTags(purpose));

    /// <summary>Size of the accepted original, for cost and throughput analysis.</summary>
    public static void RecordSourceBytes(long bytes, string purpose) => SafeRecord(SourceBytes, bytes, PurposeTags(purpose));

    public static void RecordUploadIntentFailed(string purpose) => SafeAdd(UploadIntentFailed, 1, PurposeTags(purpose));
    public static void RecordCompletionFailed(string purpose, string outcome) => SafeAdd(CompletionFailed, 1, PurposeTags(purpose, ("outcome", MediaMetricDimensions.Outcome(outcome))));

    /// <summary>A duplicate completion served from the stored idempotency response.</summary>
    public static void RecordCompletionReplayed(string purpose) => SafeAdd(CompletionReplayed, 1, PurposeTags(purpose));

    /// <summary>Rows reclaimed after their processing lease expired (crash / lost heartbeat).</summary>
    public static void RecordStaleLeasesReclaimed(string purpose, int count)
    {
        if (count <= 0) return;
        SafeAdd(StaleLeasesReclaimed, count, PurposeTags(purpose));
    }

    /// <summary>An attempt that succeeded after at least one prior failure.</summary>
    public static void RecordProcessingRecovered(string purpose, int attempt)
        => SafeAdd(ProcessingRecovered, 1, PurposeTags(purpose, ("attempt_band", MediaMetricDimensions.AttemptBand(attempt))));

    /// <summary>Abandoned Pending rows whose private object the cleanup worker purged.</summary>
    public static void RecordAbandonedPendingPurged(string purpose) => SafeAdd(AbandonedPendingPurged, 1, PurposeTags(purpose));

    /// <summary>Count of delivery variants produced for one media item.</summary>
    public static void RecordVariantsGenerated(int count, string purpose)
        => SafeAdd(VariantsGenerated, count, PurposeTags(purpose, ("variant_band", MediaMetricDimensions.VariantBand(count))));

    private static KeyValuePair<string, object?>[] PurposeTags(string purpose, params (string Key, string? Value)[] extra)
    {
        var tags = new KeyValuePair<string, object?>[1 + extra.Length];
        tags[0] = new KeyValuePair<string, object?>("purpose", MediaMetricDimensions.Purpose(purpose));
        for (var i = 0; i < extra.Length; i++) tags[i + 1] = new KeyValuePair<string, object?>(extra[i].Key, extra[i].Value);
        return tags;
    }

    // ── Failure isolation ──────────────────────────────────────────────────
    // Telemetry must never break an upload or a processing attempt. The Meter API is not
    // expected to throw, but a misbehaving in-process listener must not propagate into
    // request handling, so every write is guarded.
    private static void SafeAdd(Counter<long> counter, long value, KeyValuePair<string, object?>[] tags)
    {
        try { counter.Add(value, tags); }
        catch (Exception ex) { Debug.Fail($"Media metric add failed: {ex}"); }
    }

    private static void SafeRecord<T>(Histogram<T> histogram, T value, KeyValuePair<string, object?>[] tags) where T : struct
    {
        try { histogram.Record(value, tags); }
        catch (Exception ex) { Debug.Fail($"Media metric record failed: {ex}"); }
    }
}
