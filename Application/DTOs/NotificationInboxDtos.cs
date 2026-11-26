using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════════
//  In-app notification inbox
//
//  The caller's own notifications, filtered, sorted and paged
//  server-side. These types are shared by every console (business,
//  staff, admin, customer) because the inbox belongs to the
//  authenticated user, not to a role.
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Filters, sort and page window for the authenticated user's inbox.
///
/// <para>Nothing here can widen what the caller sees: the query is always narrowed to the
/// caller's own rows first, and <see cref="BusinessId"/> — when supplied — is validated against
/// their own active membership before it is applied.</para>
/// </summary>
public sealed class NotificationInboxQuery
{
    [JsonPropertyName("page")]
    public int Page { get; set; } = 1;

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; } = 25;

    /// <summary><c>unread</c>, <c>read</c> or <c>all</c> (default).</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Exact notification type, e.g. <c>appointment.booked</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Preference category, e.g. <c>APPOINTMENT</c>.</summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("fromUtc")]
    public DateTime? FromUtc { get; set; }

    [JsonPropertyName("toUtc")]
    public DateTime? ToUtc { get; set; }

    /// <summary><c>createdAt</c> (default), <c>type</c> or <c>status</c>.</summary>
    [JsonPropertyName("sort")]
    public string? Sort { get; set; }

    /// <summary><c>asc</c> or <c>desc</c> (default).</summary>
    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    /// <summary>
    /// Narrows to one business. Verified server-side against the caller's memberships, and
    /// platform-wide rows (no business) always remain visible.
    /// </summary>
    [JsonPropertyName("businessId")]
    public Guid? BusinessId { get; set; }
}

/// <summary>One page of the caller's notifications, with the counts an inbox header needs.</summary>
public sealed class NotificationInboxPageDto
{
    [JsonPropertyName("items")]
    public List<NotificationDto> Items { get; set; } = new();

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; } = 1;

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; } = 25;

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }

    /// <summary>Unread rows for the same scope — the badge figure, independent of the page.</summary>
    [JsonPropertyName("unreadCount")]
    public int UnreadCount { get; set; }

    [JsonPropertyName("availableTypes")]
    public List<string> AvailableTypes { get; set; } = new();

    [JsonPropertyName("availableCategories")]
    public List<string> AvailableCategories { get; set; } = new();
}
