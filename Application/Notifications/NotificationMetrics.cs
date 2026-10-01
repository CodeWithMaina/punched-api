using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PunchedApi.Application.Notifications;

internal static class NotificationMetrics
{
    private static readonly Meter Meter = new("PunchedApi.Notifications");
    private static readonly Counter<long> Sent = Meter.CreateCounter<long>("notifications_sent_total");
    private static readonly Counter<long> Suppressed = Meter.CreateCounter<long>("notifications_suppressed_total");
    private static readonly Counter<long> Failed = Meter.CreateCounter<long>("notifications_failed_total");
    private static readonly Histogram<double> DeliveryDuration =
        Meter.CreateHistogram<double>("notifications_delivery_duration_ms", "ms");
    private static long _pendingQueueDepth;

    static NotificationMetrics()
    {
        Meter.CreateObservableGauge(
            "notifications_queue_depth",
            () => Interlocked.Read(ref _pendingQueueDepth),
            description: "Number of pending notification outbox rows.");
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

    public static void RecordDeliveryDuration(string channel, double milliseconds) =>
        DeliveryDuration.Record(milliseconds, new KeyValuePair<string, object?>("channel", channel));

    public static void RecordQueueDepth(long pendingRows) => Interlocked.Exchange(ref _pendingQueueDepth, pendingRows);
}