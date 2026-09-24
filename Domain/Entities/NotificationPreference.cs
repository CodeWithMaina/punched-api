using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// One preference override for a (category, channel) pair. Deliberately
/// SPARSE: a row exists only when it deviates from the code default in
/// <c>NotificationDefaults</c> — absence *is* "inherit", which is why a user
/// with no preferences has zero rows and new notification types need no
/// migration.
/// </summary>
/// <remarks>
/// Three shapes are stored in one table (enforced by a CHECK constraint):
/// <list type="bullet">
///   <item>global user preference — <see cref="UserId"/> set, <see cref="BusinessId"/> null;</item>
///   <item>per-business user preference — both set;</item>
///   <item>business kill-switch — <see cref="UserId"/> null, <see cref="BusinessId"/> set.</item>
/// </list>
/// </remarks>
public class NotificationPreference : BaseEntity
{
    /// <summary>Override owner; null for a business-wide kill-switch row.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Tenant scope; null for a global user preference.</summary>
    public Guid? BusinessId { get; set; }

    /// <summary>Preference granularity — one of <c>NotificationCategory</c>.</summary>
    [Required]
    [MaxLength(30)]
    public string Category { get; set; } = string.Empty;

    /// <summary>One of <c>NotificationChannel</c>.</summary>
    [Required]
    [MaxLength(20)]
    public string Channel { get; set; } = string.Empty;

    /// <summary>False suppresses, true does not force-open (a business can only close — R1).</summary>
    public bool Enabled { get; set; }

    /// <summary>Last write timestamp (the resolver caches for 60s; R6 evicts on write).</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
