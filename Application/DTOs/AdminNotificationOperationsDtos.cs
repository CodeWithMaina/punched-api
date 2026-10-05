using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

public sealed class AdminNotificationOperationsOverviewDto
{
    [JsonPropertyName("range")]
    public string Range { get; set; } = string.Empty;

    [JsonPropertyName("fromUtc")]
    public DateTime FromUtc { get; set; }

    [JsonPropertyName("toUtc")]
    public DateTime ToUtc { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }

    [JsonPropertyName("notificationRecords")]
    public long NotificationRecords { get; set; }

    [JsonPropertyName("totalAttempts")]
    public long? TotalAttempts { get; set; }

    [JsonPropertyName("retryAttempts")]
    public long? RetryAttempts { get; set; }

    [JsonPropertyName("attemptHistoryComplete")]
    public bool AttemptHistoryComplete { get; set; }

    [JsonPropertyName("successfulDeliveries")]
    public long SuccessfulDeliveries { get; set; }

    [JsonPropertyName("terminalFailures")]
    public long TerminalFailures { get; set; }

    [JsonPropertyName("pendingNotifications")]
    public long PendingNotifications { get; set; }

    [JsonPropertyName("retryExhaustedNotifications")]
    public long RetryExhaustedNotifications { get; set; }

    [JsonPropertyName("permanentFailures")]
    public long PermanentFailures { get; set; }

    [JsonPropertyName("deliverySuccessRatePercent")]
    public double? DeliverySuccessRatePercent { get; set; }

    [JsonPropertyName("processingNotifications")]
    public long ProcessingNotifications { get; set; }

    [JsonPropertyName("oldestPendingAgeSeconds")]
    public double? OldestPendingAgeSeconds { get; set; }

    [JsonPropertyName("lastSuccessfulDeliveryAtUtc")]
    public DateTime? LastSuccessfulDeliveryAtUtc { get; set; }

    [JsonPropertyName("workerActivityAvailable")]
    public bool WorkerActivityAvailable { get; set; }

    [JsonPropertyName("channels")]
    public List<AdminNotificationChannelHealthDto> Channels { get; set; } = [];
}

public sealed class AdminNotificationTrendPointDto
{
    [JsonPropertyName("bucketUtc")]
    public DateTime BucketUtc { get; set; }

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("successfulAttempts")]
    public long SuccessfulAttempts { get; set; }

    [JsonPropertyName("failedAttempts")]
    public long FailedAttempts { get; set; }
}

public sealed class AdminNotificationTrendsDto
{
    [JsonPropertyName("range")]
    public string Range { get; set; } = string.Empty;

    [JsonPropertyName("fromUtc")]
    public DateTime FromUtc { get; set; }

    [JsonPropertyName("toUtc")]
    public DateTime ToUtc { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }

    [JsonPropertyName("attemptHistoryComplete")]
    public bool AttemptHistoryComplete { get; set; }

    [JsonPropertyName("totalAttempts")]
    public long? TotalAttempts { get; set; }

    [JsonPropertyName("retryAttempts")]
    public long? RetryAttempts { get; set; }

    [JsonPropertyName("successfulAttempts")]
    public long SuccessfulAttempts { get; set; }

    [JsonPropertyName("failedAttempts")]
    public long FailedAttempts { get; set; }

    [JsonPropertyName("points")]
    public List<AdminNotificationTrendPointDto> Points { get; set; } = [];
}

public sealed class AdminNotificationChannelHealthDto
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("implemented")]
    public bool Implemented { get; set; }

    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("activeInRange")]
    public bool ActiveInRange { get; set; }

    [JsonPropertyName("recordsInRange")]
    public long RecordsInRange { get; set; }

    [JsonPropertyName("sentInRange")]
    public long SentInRange { get; set; }

    [JsonPropertyName("failedInRange")]
    public long FailedInRange { get; set; }

    [JsonPropertyName("pendingNow")]
    public long PendingNow { get; set; }

    [JsonPropertyName("processingNow")]
    public long ProcessingNow { get; set; }
}

public sealed class AdminNotificationFailureQuery
{
    public string Range { get; set; } = "24h";
    public string? Channel { get; set; }
    public string? Classification { get; set; }
    public bool? RetryEligible { get; set; }
    public string? NotificationType { get; set; }
    public Guid? BusinessId { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public class AdminNotificationFailureDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("businessId")]
    public Guid? BusinessId { get; set; }

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("notificationType")]
    public string NotificationType { get; set; } = string.Empty;

    [JsonPropertyName("classification")]
    public string Classification { get; set; } = "unknown";

    [JsonPropertyName("retryFailures")]
    public int RetryFailures { get; set; }

    [JsonPropertyName("deliveryAttempts")]
    public int? DeliveryAttempts { get; set; }

    [JsonPropertyName("attemptHistoryComplete")]
    public bool AttemptHistoryComplete { get; set; }

    [JsonPropertyName("lastAttemptAtUtc")]
    public DateTime? LastAttemptAtUtc { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("retryEligible")]
    public bool RetryEligible { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class AdminNotificationFailureDetailDto : AdminNotificationFailureDto
{
    [JsonPropertyName("sanitizedError")]
    public string SanitizedError { get; set; } = string.Empty;

    [JsonPropertyName("attemptsHistory")]
    public List<AdminNotificationAttemptDto> AttemptsHistory { get; set; } = [];
}

public sealed class AdminNotificationAttemptDto
{
    [JsonPropertyName("startedAtUtc")]
    public DateTime StartedAtUtc { get; set; }

    [JsonPropertyName("completedAtUtc")]
    public DateTime? CompletedAtUtc { get; set; }

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;
}

public sealed class AdminNotificationRetryResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}