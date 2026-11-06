using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════════
//  Business notification workspace DTOs
//
//  These back the tenant-scoped notification settings experience:
//  the overview, per-channel configuration, the (category × channel)
//  preference matrix, template catalogue + variables, and test-send.
//
//  Nothing here ever exposes platform-owned provider credentials —
//  the business sees its own branding/sender identity only.
// ═══════════════════════════════════════════════════════════════════

/// <summary>Header statistics for the business notification overview.</summary>
public sealed class BusinessNotificationOverviewDto
{
    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("fromUtc")]
    public DateTime FromUtc { get; set; }

    [JsonPropertyName("toUtc")]
    public DateTime ToUtc { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>One of: healthy | degraded | attention | idle.</summary>
    [JsonPropertyName("systemStatus")]
    public string SystemStatus { get; set; } = "idle";

    [JsonPropertyName("statusDetail")]
    public string StatusDetail { get; set; } = string.Empty;

    [JsonPropertyName("enabledChannels")]
    public List<string> EnabledChannels { get; set; } = [];

    [JsonPropertyName("availableChannels")]
    public List<string> AvailableChannels { get; set; } = [];

    [JsonPropertyName("totalNotifications")]
    public long TotalNotifications { get; set; }

    [JsonPropertyName("delivered")]
    public long Delivered { get; set; }

    [JsonPropertyName("failed")]
    public long Failed { get; set; }

    [JsonPropertyName("pending")]
    public long Pending { get; set; }

    [JsonPropertyName("processing")]
    public long Processing { get; set; }

    [JsonPropertyName("deliverySuccessRatePercent")]
    public double? DeliverySuccessRatePercent { get; set; }

    [JsonPropertyName("byChannel")]
    public List<AdminNotificationAnalyticsCountDto> ByChannel { get; set; } = [];

    [JsonPropertyName("dailyTrend")]
    public List<AdminNotificationAnalyticsTrendPointDto> DailyTrend { get; set; } = [];

    [JsonPropertyName("recentActivity")]
    public List<BusinessNotificationActivityDto> RecentActivity { get; set; } = [];
}

/// <summary>One entry of the business's recent notification activity feed.</summary>
public sealed class BusinessNotificationActivityDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("notificationType")]
    public string NotificationType { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("recipientMasked")]
    public string RecipientMasked { get; set; } = string.Empty;

    [JsonPropertyName("isTest")]
    public bool IsTest { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; }

    [JsonPropertyName("deliveredAtUtc")]
    public DateTime? DeliveredAtUtc { get; set; }
}

/// <summary>Per-channel configuration the business can see and control.</summary>
public sealed class BusinessNotificationChannelDto
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    /// <summary>Whether the platform ships a sender for this channel.</summary>
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    /// <summary>Whether the business has this channel switched on.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Business-owned, non-secret settings (sender identity, reply-to, …).</summary>
    [JsonPropertyName("settings")]
    public Dictionary<string, string?> Settings { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Platform-owned configuration the business cannot change.</summary>
    [JsonPropertyName("platformNotes")]
    public List<string> PlatformNotes { get; set; } = [];

    [JsonPropertyName("verificationStatus")]
    public string VerificationStatus { get; set; } = "not_required";

    [JsonPropertyName("verificationDetail")]
    public string VerificationDetail { get; set; } = string.Empty;
}

/// <summary>Update the business's messaging profile / channel identity.</summary>
public sealed class UpdateBusinessNotificationChannelsRequest
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("replyTo")]
    public string? ReplyTo { get; set; }

    [JsonPropertyName("contactEmail")]
    public string? ContactEmail { get; set; }

    [JsonPropertyName("contactPhone")]
    public string? ContactPhone { get; set; }

    [JsonPropertyName("website")]
    public string? Website { get; set; }

    [JsonPropertyName("primaryColor")]
    public string? PrimaryColor { get; set; }

    [JsonPropertyName("secondaryColor")]
    public string? SecondaryColor { get; set; }

    [JsonPropertyName("footerNote")]
    public string? FooterNote { get; set; }

    [JsonPropertyName("logoMediaId")]
    public Guid? LogoMediaId { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonPropertyName("enabledChannels")]
    public List<string>? EnabledChannels { get; set; }
}

/// <summary>One (category × channel) preference row for the business.</summary>
public sealed class BusinessNotificationPreferenceDto
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("available")]
    public bool Available { get; set; } = true;

    [JsonPropertyName("source")]
    public string Source { get; set; } = PreferenceSource.System;

    /// <summary>True when an administrator set this value rather than the business.</summary>
    [JsonPropertyName("adminOverride")]
    public bool AdminOverride { get; set; }
}

/// <summary>Replace the business's (category × channel) preferences.</summary>
public sealed class UpdateBusinessNotificationPreferencesRequest
{
    [JsonPropertyName("preferences")]
    public List<NotificationPreferenceItem> Preferences { get; set; } = [];
}

/// <summary>Send a test message through one channel.</summary>
public sealed class BusinessNotificationTestSendRequest
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Test recipient — an email address or mobile number, per channel.</summary>
    [JsonPropertyName("recipient")]
    public string Recipient { get; set; } = string.Empty;
}

/// <summary>Result of a test send.</summary>
public sealed class BusinessNotificationTestSendResponse
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("renderedSubject")]
    public string? RenderedSubject { get; set; }

    [JsonPropertyName("renderedBody")]
    public string? RenderedBody { get; set; }
}

/// <summary>One template in the business template catalogue.</summary>
public sealed class BusinessNotificationTemplateDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>override | default</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "default";

    [JsonPropertyName("overrideId")]
    public Guid? OverrideId { get; set; }

    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("protected")]
    public bool Protected { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTime? UpdatedAtUtc { get; set; }

    [JsonPropertyName("availableVariables")]
    public List<string> AvailableVariables { get; set; } = [];
}

/// <summary>A template variable the business may insert.</summary>
public sealed class BusinessNotificationVariableDto
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("sampleValue")]
    public string SampleValue { get; set; } = string.Empty;
}

/// <summary>Preview a template draft with realistic sample data.</summary>
public sealed class BusinessNotificationPreviewRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("subjectTemplate")]
    public string? SubjectTemplate { get; set; }

    [JsonPropertyName("bodyTemplateHtml")]
    public string BodyTemplateHtml { get; set; } = string.Empty;

    [JsonPropertyName("bodyTemplateText")]
    public string BodyTemplateText { get; set; } = string.Empty;

    /// <summary>Optional overrides for the sample payload; omitted keys use defaults.</summary>
    [JsonPropertyName("payload")]
    public Dictionary<string, object?>? Payload { get; set; }
}
