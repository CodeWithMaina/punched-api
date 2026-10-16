using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Channel-level suppression for one (business, user, channel) triple. While an
/// unexpired row exists, the notification pipeline refuses to deliver on that
/// channel regardless of user preferences — the operational pause always wins.
/// One active row per triple (unique index); re-suppressing updates the row.
/// </summary>
public class NotificationSuppression : BaseEntity
{
    /// <summary>Tenant the suppression is scoped to (FK to businesses).</summary>
    public Guid BusinessId { get; set; }

    /// <summary>User whose channel is suppressed (FK to users).</summary>
    public Guid UserId { get; set; }

    /// <summary>Suppressed channel — one of <c>NotificationChannel</c>.</summary>
    [Required]
    [MaxLength(20)]
    public string Channel { get; set; } = string.Empty;

    /// <summary>
    /// Machine-readable reason, e.g. <c>ops_pause</c>, <c>hard_bounce</c>,
    /// <c>complaint</c>, <c>budget_exceeded</c>. Classifies the suppression for
    /// operators; never rendered to the user.
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Who created the row: <c>admin</c>, <c>system</c> or <c>user</c>.</summary>
    [Required]
    [MaxLength(64)]
    public string Source { get; set; } = string.Empty;

    /// <summary>UTC expiry; null means the suppression never expires on its own.</summary>
    public DateTime? ExpiresAt { get; set; }
}