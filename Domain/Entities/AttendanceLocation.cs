using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// An attendance point (site / entrance / counter) — the unit a QR
/// credential belongs to. Punched has no reusable Location entity, so this
/// is a new table. Name uniqueness per business is enforced in the service
/// layer (Phase 2), not via a functional index (the codebase uses none).
/// </summary>
public class AttendanceLocation : BaseEntity
{
    /// <summary>
    /// FK to the business this location belongs to.
    /// </summary>
    public Guid BusinessId { get; set; }

    /// <summary>
    /// Display name of the attendance point.
    /// </summary>
    [Required]
    [MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional longer description.
    /// </summary>
    [MaxLength(300)]
    public string? Description { get; set; }

    /// <summary>
    /// Deactivating blocks new scans here while all history is retained.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// FK to the user who created this location.
    /// </summary>
    public Guid CreatedByUserId { get; set; }
}
