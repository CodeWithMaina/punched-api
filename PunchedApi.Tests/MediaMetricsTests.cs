using System.Diagnostics.Metrics;
using PunchedApi.Application.Media;

namespace PunchedApi.Tests;

/// <summary>
/// Instrumentation contract tests. These matter because telemetry can silently become both a
/// cardinality explosion and a data leak: an ID-valued metric tag creates one time series per
/// upload and puts tenant/user identifiers into a metrics backend that often has weaker access
/// control than the database.
/// </summary>
public sealed class MediaMetricsTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<(string Name, KeyValuePair<string, object?>[] Tags, double Value)> _observations = [];

    public MediaMetricsTests()
    {
        _listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name != MediaMetrics.MeterName) return;
            _listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            _observations.Add((instrument.Name, tags.ToArray(), measurement)));
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            _observations.Add((instrument.Name, tags.ToArray(), measurement)));
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void MeterNameIsThePreExistingPunchedMediaMeter()
    {
        // The brief forbids introducing a new monitoring vendor; these instruments must hang off
        // the meter the deployment can already export.
        Assert.Equal("Punched.Media", MediaMetrics.MeterName);
    }

    [Theory]
    [InlineData("business-logo")]
    [InlineData("business-cover")]
    [InlineData("business-gallery")]
    [InlineData("user-avatar")]
    [InlineData("service-image")]
    [InlineData("loyalty-program-image")]
    [InlineData("review-image")]
    public void KnownPurposesPassThroughAsTags(string purpose)
    {
        _observations.Clear();
        MediaMetrics.RecordQueueDelay(TimeSpan.FromMilliseconds(10), purpose);

        var observation = _observations.Single(x => x.Name == "media.processing.queue.delay.ms");
        Assert.Equal(purpose, Tag(observation.Tags, "purpose"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-purpose")]
    [InlineData("../../etc/passwd")]
    [InlineData("<script>alert(1)</script>")]
    public void UnknownOrMaliciousPurposesCollapseToAClosedValue(string purpose)
    {
        // A free-form purpose would let a client mint unbounded tag values. It must collapse.
        _observations.Clear();
        MediaMetrics.RecordQueueDelay(TimeSpan.FromMilliseconds(10), purpose);

        var observation = _observations.Single(x => x.Name == "media.processing.queue.delay.ms");
        Assert.Equal("unknown", Tag(observation.Tags, "purpose"));
    }

    private static object? Tag(IReadOnlyList<KeyValuePair<string, object?>>? tags, string key)
        => tags?.FirstOrDefault(t => t.Key == key).Value;

    [Fact]
    public void NoLifecycleMetricEverUsesAnIdentifierAsATag()
    {
        // IDs, keys, filenames and URLs must never appear as metric tags. If this test ever
        // fails, the metrics backend is about to receive unbounded per-upload cardinality.
        var mediaId = Guid.NewGuid().ToString("N");
        var businessId = Guid.NewGuid().ToString("N");
        var sourceKey = $"pending/businesses/{businessId}/{mediaId}.bin";
        var deliveryUrl = $"https://media.example.test/delivery/{mediaId}/gallery-640w.webp";

        _observations.Clear();
        MediaMetrics.RecordUploadIntentDuration(TimeSpan.FromMilliseconds(12), "business-logo", true, 4096);
        MediaMetrics.RecordCompletionDuration(TimeSpan.FromMilliseconds(30), "business-logo", "success");
        MediaMetrics.RecordCompletionReplayed("business-logo");
        MediaMetrics.RecordCompletionFailed("business-logo", "size_mismatch");
        MediaMetrics.RecordProcessingDuration(TimeSpan.FromMilliseconds(500), "business-gallery");
        MediaMetrics.RecordQueueDelay(TimeSpan.FromMilliseconds(80), "business-gallery");
        MediaMetrics.RecordRetrievalDuration(TimeSpan.FromMilliseconds(5), "business-gallery", "success");
        MediaMetrics.RecordUploadToReady(TimeSpan.FromMilliseconds(900), "business-gallery");
        MediaMetrics.RecordStaleLeaseAge(TimeSpan.FromMilliseconds(300_000), "business-gallery");
        MediaMetrics.RecordStaleLeasesReclaimed("business-gallery", 2);
        MediaMetrics.RecordProcessingRecovered("business-gallery", 3);
        MediaMetrics.RecordAbandonedPendingPurged("business-gallery");
        MediaMetrics.RecordVariantsGenerated(4, "business-gallery");
        MediaMetrics.RecordSourceBytes(4096, "business-gallery");
        MediaMetrics.RecordUploadIntentFailed("business-logo");

        Assert.NotEmpty(_observations);
        foreach (var (_, tags, _) in _observations)
        {
            foreach (var (key, value) in tags ?? [])
            {
                var rendered = value?.ToString() ?? string.Empty;
                Assert.DoesNotContain(key,
                    new[] { "mediaid", "media_id", "businessid", "business_id", "userid", "key", "url", "filename" },
                    StringComparer.OrdinalIgnoreCase);
                Assert.False(rendered.Contains(mediaId, StringComparison.OrdinalIgnoreCase), $"A media id leaked into tag '{key}'.");
                Assert.False(rendered.Contains(businessId, StringComparison.OrdinalIgnoreCase), $"A business id leaked into tag '{key}'.");
                Assert.False(rendered.Contains(sourceKey, StringComparison.Ordinal), $"A source key leaked into tag '{key}'.");
                Assert.False(rendered.Contains("X-Amz-Signature", StringComparison.OrdinalIgnoreCase), $"A presigned URL leaked into tag '{key}'.");
                Assert.False(rendered.Contains(deliveryUrl, StringComparison.Ordinal), $"A delivery URL leaked into tag '{key}'.");
            }
        }
    }

    [Theory]
    [InlineData(0L, "unknown")]
    [InlineData(-5L, "unknown")]
    [InlineData(50 * 1024L, "le100kb")]
    [InlineData(1024 * 1024L, "le1mb")]
    [InlineData(5 * 1024 * 1024L, "le5mb")]
    [InlineData(10 * 1024 * 1024L, "le10mb")]
    [InlineData(12 * 1024 * 1024L, "gt10mb")]
    public void SizeBandsAreBounded(long bytes, string expected)
        => Assert.Equal(expected, MediaMetricDimensions.SizeBand(bytes));

    [Theory]
    [InlineData(0, "0")]
    [InlineData(2, "1-2")]
    [InlineData(4, "3-4")]
    [InlineData(6, "5-6")]
    [InlineData(40, "gt6")]
    public void VariantBandsAreBounded(int count, string expected)
        => Assert.Equal(expected, MediaMetricDimensions.VariantBand(count));

    [Theory]
    [InlineData(1, "1")]
    [InlineData(3, "3")]
    [InlineData(99, "gt3")]
    public void AttemptBandsAreBounded(int attempt, string expected)
        => Assert.Equal(expected, MediaMetricDimensions.AttemptBand(attempt));

    [Fact]
    public void RecordingMetricsCannotThrowForHostileInput()
    {
        // Telemetry must never break an upload or a processing attempt.
        var exception = Record.Exception(() =>
        {
            MediaMetrics.RecordUploadIntentDuration(TimeSpan.MinValue, null!, false, null);
            MediaMetrics.RecordCompletionDuration(TimeSpan.MaxValue, null!, null!);
            MediaMetrics.RecordQueueDelay(TimeSpan.Zero, null!);
            MediaMetrics.RecordRetrievalDuration(TimeSpan.Zero, null!, null!);
            MediaMetrics.RecordUploadToReady(TimeSpan.Zero, null!);
            MediaMetrics.RecordStaleLeaseAge(TimeSpan.Zero, null!);
            MediaMetrics.RecordProcessingDuration(TimeSpan.Zero, null!);
            MediaMetrics.RecordSourceBytes(-1, null!);
            MediaMetrics.RecordVariantsGenerated(-1, null!);
            MediaMetrics.RecordStaleLeasesReclaimed(null!, 0);
            MediaMetrics.RecordStaleLeasesReclaimed(null!, -3);
            MediaMetrics.RecordProcessingRecovered(null!, 0);
            MediaMetrics.RecordAbandonedPendingPurged(null!);
            MediaMetrics.RecordUploadIntentFailed(null!);
            MediaMetrics.RecordCompletionFailed(null!, null!);
            MediaMetrics.RecordCompletionReplayed(null!);
            MediaMetrics.RecordUploadCreated();
            MediaMetrics.RecordUploadCompleted();
            MediaMetrics.RecordProcessingStarted();
            MediaMetrics.RecordProcessingSucceeded();
            MediaMetrics.RecordProcessingFailed();
            MediaMetrics.RecordProcessingRetry();
            MediaMetrics.RecordCleanupDeleted();
            MediaMetrics.RecordCleanupFailed();
        });

        Assert.Null(exception);
    }
}
