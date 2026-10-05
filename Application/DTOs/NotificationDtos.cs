using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  Notification module DTOs (Phase 1)
//  Preference reads return the RESOLVED view so the PWA renders
//  checkboxes without re-implementing the resolution rules.
// ═══════════════════════════════════════════════════════════════

/// <summary>Unread badge count for the caller's inbox.</summary>
public class UnreadCountResponse
{
    [JsonPropertyName("unreadCount")]
    public int UnreadCount { get; set; }
}

/// <summary>
/// One resolved (category, channel) preference: the effective value after
/// business + user overrides, whether the platform ships the channel yet, and
/// which level decided it (<c>system</c> | <c>business</c> | <c>user</c>).
/// </summary>
public class ResolvedPreferenceDto
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// False when this deployment has not shipped a sender for the channel yet
    /// (sms/push before their phase). The client greys the row out instead of
    /// offering a choice that cannot be honoured.
    /// </summary>
    [JsonPropertyName("available")]
    public bool Available { get; set; } = true;

    [JsonPropertyName("source")]
    public string Source { get; set; } = PreferenceSource.System;
}

/// <summary>The three levels a preference value can come from.</summary>
public static class PreferenceSource
{
    public const string System = "system";
    public const string Business = "business";
    public const string User = "user";
}

/// <summary>One (category, channel) toggle in a preference write.</summary>
public class NotificationPreferenceItem
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

/// <summary>
/// Upsert the caller's own preference rows. <see cref="BusinessId"/> scopes the
/// override to one business; null writes a global user preference. There is
/// deliberately no force/override flag — the server owns <c>Force</c>.
/// </summary>
public class UpdateNotificationPreferencesRequest
{
    [JsonPropertyName("businessId")]
    public Guid? BusinessId { get; set; }

    [JsonPropertyName("preferences")]
    public List<NotificationPreferenceItem> Preferences { get; set; } = new();
}
