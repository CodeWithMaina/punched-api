using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PunchedApi.Application.Notifications;

internal static class NotificationMetrics
{
    private static readonly Meter Meter = new("PunchedApi.Notifications");
    private static readonly Counter<long> Sent = Meter.CreateCounter<long>("notifications_sent_total");
    private static readonly Counter<long> Suppressed = Meter.CreateCounter<long>("notifications_suppressed_total");
    private static readonly Counter<long> Failed = Meter.CreateCounter<long>("notifications_failed_total");
    private static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>("notifications_dead_lettered_total");
    private static readonly Histogram<double> DeliveryDuration =
        Meter.CreateHistogram<double>("notifications_delivery_duration_ms", "ms");
    private static long _pendingQueueDepth;
    private static double _oldestPendingAgeSeconds;

    static NotificationMetrics()
    {
        Meter.CreateObservableGauge(
            "notifications_queue_depth",
            () => Interlocked.Read(ref _pendingQueueDepth),
            description: "Number of pending notification outbox rows.");

        Meter.CreateObservableGauge(
            "notifications_oldest_pending_age_seconds",
            () => Interlocked.CompareExchange(ref _oldestPendingAgeSeconds, 0, 0),
            "s",
            "Age of the oldest pending notification outbox row (0 when the queue is empty).");
    }

    public static void RecordSent(string channel) => Sent.Add(1, new KeyValuePair<string, object?>("channel", channel));

    public static void RecordSuppressed(string category, string reason)
    {
        var tags = new TagList
        {
            { "category", category },
            { "reason", reason }
        };
        Suppressed.Add(1, tags);
    }

    public static void RecordFailed(string channel) => Failed.Add(1, new KeyValuePair<string, object?>("channel", channel));

    /// <summary>Terminal dead-letter, classified by reason (retry_exhausted | permanent | unknown).</summary>
    public static void RecordDeadLettered(string channel, string reason)
    {
        var tags = new TagList
        {
            { "channel", channel },
            { "reason", reason }
        };
        DeadLettered.Add(1, tags);
    }

    public static void RecordDeliveryDuration(string channel, double milliseconds) =>
        DeliveryDuration.Record(milliseconds, new KeyValuePair<string, object?>("channel", channel));

    public static void RecordQueueDepth(long pendingRows) => Interlocked.Exchange(ref _pendingQueueDepth, pendingRows);

    /// <summary>Age of the oldest pending row — the backlog signal that matters.</summary>
    public static void RecordOldestPendingAge(double seconds) =>
        Interlocked.Exchange(ref _oldestPendingAgeSeconds, Math.Max(0, seconds));
}
