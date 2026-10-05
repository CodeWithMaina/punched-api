using System.Text.Json;

namespace PunchedApi.Application.Notifications;

internal sealed class NotificationDeliveryAttemptHistory
{
    private const int MaximumEntries = 50;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public int Version { get; set; } = 1;
    public bool Complete { get; set; } = true;
    public List<NotificationDeliveryAttempt> Attempts { get; set; } = [];

    public static NotificationDeliveryAttemptHistory ForNewRecord() => new();

    public static NotificationDeliveryAttemptHistory Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new() { Complete = false };

        try
        {
            var history = JsonSerializer.Deserialize<NotificationDeliveryAttemptHistory>(json, JsonOptions);
            return history ?? new() { Complete = false };
        }
        catch (JsonException)
        {
            return new() { Complete = false };
        }
    }

    public NotificationDeliveryAttempt Start(DateTime startedAtUtc)
    {
        var attempt = new NotificationDeliveryAttempt
        {
            StartedAtUtc = DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc),
            Outcome = "started"
        };
        Attempts.Add(attempt);
        if (Attempts.Count > MaximumEntries)
        {
            Attempts.RemoveAt(0);
            Complete = false;
        }

        return attempt;
    }

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);
}

internal sealed class NotificationDeliveryAttempt
{
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string Outcome { get; set; } = "started";
}