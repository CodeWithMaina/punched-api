using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════════
//  Admin notification management DTOs
//
//  These mirror the operational module the platform console renders:
//  the log explorer, delivery monitoring, provider status, global
//  templates, per-business notification management and analytics.
//
//  Every DTO here is a *projection* — EF entities are never returned.
//  Recipient identifiers are always masked before they leave the
//  service, so an administrator without the underlying record cannot
//  read a raw address from a list response.
// ═══════════════════════════════════════════════════════════════════

/// <summary>One row of the notification log explorer.</summary>
public class AdminNotificationLogItemDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("businessId")]
    public Guid? BusinessId { get; set; }

    [JsonPropertyName("businessName")]
    public string? BusinessName { get; set; }

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("notificationType")]
    public string NotificationType { get; set; } = string.Empty;

    [JsonPropertyName("templateKey")]
    public string? TemplateKey { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("classification")]
    public string Classification { get; set; } = "unknown";

    /// <summary>Masked recipient identity — never the raw address or number.</summary>
    [JsonPropertyName("recipientMasked")]
    public string RecipientMasked { get; set; } = string.Empty;

    [JsonPropertyName("attempts")]
    public int Attempts { get; set; }

    [JsonPropertyName("retryEligible")]
    public bool RetryEligible { get; set; }

    [JsonPropertyName("cancellable")]
    public bool Cancellable { get; set; }

    [JsonPropertyName("ruleId")]
    public string? RuleId { get; set; }

    [JsonPropertyName("sourceJobId")]
    public string? SourceJobId { get; set; }

    [JsonPropertyName("errorSummary")]
    public string? ErrorSummary { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }

    [JsonPropertyName("nextAttemptAtUtc")]
    public DateTime? NextAttemptAtUtc { get; set; }

    [JsonPropertyName("deliveredAtUtc")]
    public DateTime? DeliveredAtUtc { get; set; }

    [JsonPropertyName("deadLetteredAtUtc")]
    public DateTime? DeadLetteredAtUtc { get; set; }
}

/// <summary>Full detail for one notification record.</summary>
public sealed class AdminNotificationLogDetailDto : AdminNotificationLogItemDto
{
    [JsonPropertyName("recipientUserId")]
    public Guid RecipientUserId { get; set; }

    [JsonPropertyName("sanitizedError")]
    public string? SanitizedError { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }

    /// <summary>Payload with sensitive keys removed (never raw secrets).</summary>
    [JsonPropertyName("sanitizedPayloadJson")]
    public string SanitizedPayloadJson { get; set; } = "{}";

    [JsonPropertyName("deliveryAttempts")]
    public List<AdminNotificationAttemptDto> DeliveryAttempts { get; set; } = [];

    /// <summary>Resolved template currently in force for this (business, type, channel).</summary>
    [JsonPropertyName("templateSource")]
    public string TemplateSource { get; set; } = string.Empty;

    [JsonPropertyName("templateVersion")]
    public int? TemplateVersion { get; set; }
}

/// <summary>Channel configuration + health for the provider page.</summary>
public sealed class AdminNotificationProviderDto
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    /// <summary>Whether a delivery implementation is registered in this deployment.</summary>
    [JsonPropertyName("registered")]
    public bool Registered { get; set; }

    /// <summary>Whether the deployment's configuration has this channel switched on.</summary>
    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unconfigured";

    /// <summary>Non-secret configuration values only.</summary>
    [JsonPropertyName("settings")]
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Credentials that exist but are never returned in plaintext.</summary>
    [JsonPropertyName("maskedCredentials")]
    public Dictionary<string, string> MaskedCredentials { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Health derived from real delivery evidence, never from configuration alone.</summary>
    [JsonPropertyName("health")]
    public AdminNotificationProviderHealthDto Health { get; set; } = new();

    [JsonPropertyName("supportsConnectionTest")]
    public bool SupportsConnectionTest { get; set; }
}

/// <summary>Health evidence for one channel.</summary>
public sealed class AdminNotificationProviderHealthDto
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "unknown";

    [JsonPropertyName("evidence")]
    public string Evidence { get; set; } = string.Empty;

    [JsonPropertyName("recentAttempts")]
    public long RecentAttempts { get; set; }

    [JsonPropertyName("recentFailures")]
    public long RecentFailures { get; set; }

    [JsonPropertyName("lastSuccessfulDeliveryAtUtc")]
    public DateTime? LastSuccessfulDeliveryAtUtc { get; set; }
}

/// <summary>Result of a live provider connection test.</summary>
public sealed class AdminNotificationProviderTestResultDto
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("checkedAtUtc")]
    public DateTime CheckedAtUtc { get; set; }
}

/// <summary>One platform (global) notification template, code-defined.</summary>
public sealed class AdminNotificationTemplateDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("defaultChannels")]
    public List<string> DefaultChannels { get; set; } = [];

    /// <summary>Protected system messages (OTP, security) can never be overridden.</summary>
    [JsonPropertyName("protected")]
    public bool Protected { get; set; }

    [JsonPropertyName("availableVariables")]
    public List<string> AvailableVariables { get; set; } = [];

    [JsonPropertyName("overrideCount")]
    public int OverrideCount { get; set; }
}

/// <summary>Delivery monitoring snapshot for the operations page.</summary>
public sealed class AdminNotificationDeliveryMonitoringDto
{
    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }

    [JsonPropertyName("queueDepth")]
    public long QueueDepth { get; set; }

    [JsonPropertyName("pending")]
    public long Pending { get; set; }

    [JsonPropertyName("processing")]
    public long Processing { get; set; }

    [JsonPropertyName("failed")]
    public long Failed { get; set; }

    [JsonPropertyName("deadLettered")]
    public long DeadLettered { get; set; }

    [JsonPropertyName("retryScheduled")]
    public long RetryScheduled { get; set; }

    [JsonPropertyName("oldestPendingAgeSeconds")]
    public double? OldestPendingAgeSeconds { get; set; }

    [JsonPropertyName("retryActivity24h")]
    public long RetryActivity24h { get; set; }

    [JsonPropertyName("lastSuccessfulDeliveryAtUtc")]
    public DateTime? LastSuccessfulDeliveryAtUtc { get; set; }

    [JsonPropertyName("lastWorkerActivityAtUtc")]
    public DateTime? LastWorkerActivityAtUtc { get; set; }

    [JsonPropertyName("workerState")]
    public string WorkerState { get; set; } = "unknown";

    [JsonPropertyName("workerEvidence")]
    public string WorkerEvidence { get; set; } = string.Empty;

    [JsonPropertyName("channels")]
    public List<AdminNotificationProviderDto> Channels { get; set; } = [];

    [JsonPropertyName("backlog")]
    public List<AdminNotificationBacklogBucketDto> Backlog { get; set; } = [];
}

/// <summary>One age bucket of the pending backlog.</summary>
public sealed class AdminNotificationBacklogBucketDto
{
    [JsonPropertyName("bucket")]
    public string Bucket { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public long Count { get; set; }
}

/// <summary>Per-business notification summary for the admin business list.</summary>
public class AdminNotificationBusinessSummaryDto
{
    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("businessName")]
    public string BusinessName { get; set; } = string.Empty;

    [JsonPropertyName("totalNotifications")]
    public long TotalNotifications { get; set; }

    [JsonPropertyName("sent")]
    public long Sent { get; set; }

    [JsonPropertyName("failed")]
    public long Failed { get; set; }

    [JsonPropertyName("pending")]
    public long Pending { get; set; }

    [JsonPropertyName("deliverySuccessRatePercent")]
    public double? DeliverySuccessRatePercent { get; set; }

    [JsonPropertyName("channelOverrides")]
    public List<AdminNotificationChannelOverrideDto> ChannelOverrides { get; set; } = [];

    [JsonPropertyName("deliveryDisabled")]
    public bool DeliveryDisabled { get; set; }

    [JsonPropertyName("lastNotificationAtUtc")]
    public DateTime? LastNotificationAtUtc { get; set; }
}

/// <summary>An explicit administrative channel override for one business.</summary>
public sealed class AdminNotificationChannelOverrideDto
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    /// <summary>False when an administrator suppressed the channel for this business.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>Full notification posture for one business.</summary>
public sealed class AdminNotificationBusinessDetailDto : AdminNotificationBusinessSummaryDto
{
    [JsonPropertyName("enabledChannels")]
    public List<string> EnabledChannels { get; set; } = [];

    [JsonPropertyName("preferences")]
    public List<AdminNotificationBusinessPreferenceDto> Preferences { get; set; } = [];

    [JsonPropertyName("templates")]
    public List<AdminNotificationBusinessTemplateDto> Templates { get; set; } = [];

    [JsonPropertyName("automationRules")]
    public List<AdminNotificationBusinessRuleDto> AutomationRules { get; set; } = [];

    [JsonPropertyName("recentNotifications")]
    public List<AdminNotificationLogItemDto> RecentNotifications { get; set; } = [];

    [JsonPropertyName("recentFailures")]
    public List<AdminNotificationLogItemDto> RecentFailures { get; set; } = [];

    [JsonPropertyName("statistics")]
    public AdminNotificationBusinessStatisticsDto Statistics { get; set; } = new();
}

/// <summary>Per-channel statistics for one business.</summary>
public sealed class AdminNotificationBusinessStatisticsDto
{
    [JsonPropertyName("byChannel")]
    public List<AdminNotificationAnalyticsCountDto> ByChannel { get; set; } = [];

    [JsonPropertyName("byType")]
    public List<AdminNotificationAnalyticsCountDto> ByType { get; set; } = [];

    [JsonPropertyName("dailyTrend")]
    public List<AdminNotificationAnalyticsTrendPointDto> DailyTrend { get; set; } = [];
}

/// <summary>One business-level (category × channel) notification preference.</summary>
public sealed class AdminNotificationBusinessPreferenceDto
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("adminOverride")]
    public bool AdminOverride { get; set; }
}

/// <summary>One business template override in the admin business view.</summary>
public sealed class AdminNotificationBusinessTemplateDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("updatedBy")]
    public string UpdatedBy { get; set; } = string.Empty;

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>One automation rule in the admin business view.</summary>
public sealed class AdminNotificationBusinessRuleDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Analytics for the notification reporting page.</summary>
public sealed class AdminNotificationAnalyticsDto
{
    [JsonPropertyName("fromUtc")]
    public DateTime FromUtc { get; set; }

    [JsonPropertyName("toUtc")]
    public DateTime ToUtc { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }

    [JsonPropertyName("totalNotifications")]
    public long TotalNotifications { get; set; }

    [JsonPropertyName("sent")]
    public long Sent { get; set; }

    [JsonPropertyName("failed")]
    public long Failed { get; set; }

    [JsonPropertyName("pending")]
    public long Pending { get; set; }

    [JsonPropertyName("processing")]
    public long Processing { get; set; }

    [JsonPropertyName("deliverySuccessRatePercent")]
    public double? DeliverySuccessRatePercent { get; set; }

    [JsonPropertyName("failureRatePercent")]
    public double? FailureRatePercent { get; set; }

    [JsonPropertyName("totalSmsSegments")]
    public long TotalSmsSegments { get; set; }

    [JsonPropertyName("byChannel")]
    public List<AdminNotificationAnalyticsCountDto> ByChannel { get; set; } = [];

    [JsonPropertyName("byType")]
    public List<AdminNotificationAnalyticsCountDto> ByType { get; set; } = [];

    [JsonPropertyName("byBusiness")]
    public List<AdminNotificationAnalyticsCountDto> ByBusiness { get; set; } = [];

    [JsonPropertyName("providerPerformance")]
    public List<AdminNotificationProviderPerformanceDto> ProviderPerformance { get; set; } = [];

    [JsonPropertyName("dailyTrend")]
    public List<AdminNotificationAnalyticsTrendPointDto> DailyTrend { get; set; } = [];
}

/// <summary>A generic (label, counts) aggregate.</summary>
public sealed class AdminNotificationAnalyticsCountDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("sent")]
    public long Sent { get; set; }

    [JsonPropertyName("failed")]
    public long Failed { get; set; }

    [JsonPropertyName("pending")]
    public long Pending { get; set; }

    [JsonPropertyName("successRatePercent")]
    public double? SuccessRatePercent { get; set; }
}

/// <summary>Provider delivery performance for one channel.</summary>
public sealed class AdminNotificationProviderPerformanceDto
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("sent")]
    public long Sent { get; set; }

    [JsonPropertyName("failed")]
    public long Failed { get; set; }

    [JsonPropertyName("averageDeliverySeconds")]
    public double? AverageDeliverySeconds { get; set; }

    [JsonPropertyName("successRatePercent")]
    public double? SuccessRatePercent { get; set; }
}

/// <summary>One point of the daily delivery trend.</summary>
public sealed class AdminNotificationAnalyticsTrendPointDto
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("sent")]
    public long Sent { get; set; }

    [JsonPropertyName("failed")]
    public long Failed { get; set; }

    [JsonPropertyName("pending")]
    public long Pending { get; set; }
}

/// <summary>Outcome of an administrative retry or cancel action.</summary>
public sealed class AdminNotificationActionResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>Request body for an administrative channel override.</summary>
public sealed class AdminNotificationChannelOverrideRequest
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>Request body for cancelling a pending notification.</summary>
public sealed class AdminNotificationCancelRequest
{
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
