using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Durable version history for a <see cref="NotificationTemplateOverride"/>.
/// Every material change to an override writes a snapshot here before the
/// current row is updated, so a business can inspect history and restore any
/// prior version as a new (current) version. One row per (override, version).
/// </summary>
[Table("notification_template_override_versions")]
public class NotificationTemplateOverrideVersion : BaseEntity
{
    /// <summary>The override this version belongs to.</summary>
    [Required]
    public Guid OverrideId { get; set; }

    /// <summary>Tenant scope — denormalised for efficient business-scoped queries.</summary>
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

    /// <summary>Version number at the time of the snapshot.</summary>
    public int Version { get; set; }

    /// <summary>User who wrote this version.</summary>
    [Required]
    [MaxLength(200)]
    public string UpdatedBy { get; set; } = string.Empty;

    /// <summary>Foreign key to the override.</summary>
    [ForeignKey(nameof(OverrideId))]
    public virtual NotificationTemplateOverride? Override { get; set; }
}
