using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Per-business, per-type, per-channel template override (Phase 4).
/// Businesses can customise subject/body for any registered notification type
/// and channel. Token whitelist is validated against the type's declared
/// payload schema at save time.
/// </summary>
[Table("notification_template_overrides")]
public class NotificationTemplateOverride : BaseEntity
{
    /// <summary>Tenant scope — every override belongs to exactly one business.</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>Registered notification type, e.g. <c>appointment.reminder</c>.</summary>
    [Required]
    [MaxLength(100)]
    public string Type { get; set; } = string.Empty;

    /// <summary>Channel: email / sms / push / in_app.</summary>
    [Required]
    [MaxLength(20)]
    public string Channel { get; set; } = string.Empty;

    /// <summary>Subject template (email only; null for non-email channels).</summary>
    [MaxLength(200)]
    public string? SubjectTemplate { get; set; }

    /// <summary>HTML body template with {{token}} placeholders.</summary>
    [Required]
    public string BodyTemplateHtml { get; set; } = string.Empty;

    /// <summary>Plain-text body template.</summary>
    [Required]
    public string BodyTemplateText { get; set; } = string.Empty;

    /// <summary>Version number — incremented on each material change.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Last write timestamp (for ordering + concurrency hints).</summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>User who last wrote this override.</summary>
    [Required]
    [MaxLength(200)]
    public string UpdatedBy { get; set; } = string.Empty;

    /// <summary>Foreign key to businesses.</summary>
    [ForeignKey(nameof(BusinessId))]
    public virtual Business? Business { get; set; }
}