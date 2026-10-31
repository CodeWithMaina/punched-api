using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// A business-managed job title / position label (Senior Stylist, Master
/// Barber, Tattoo Artist, Spa Therapist, Receptionist, Manager, ...).
///
/// <para><b>A title describes what someone does. It never determines what they
/// are authorised to do.</b> Positions are deliberately decoupled from both
/// service eligibility (<see cref="StaffServiceAssignment"/>) and permissions
/// (<see cref="BusinessRole"/>) — assigning a position grants nothing.</para>
///
/// <para>Labels are free-form per business, so no global enum is imposed. A
/// reusable, business-scoped entity (rather than a bare string on the
/// membership) is justified because businesses must be able to rename and
/// reorder their own titles, and because renaming a title must not rewrite the
/// title a staff member historically held — see
/// <see cref="StaffPositionHistory"/>.</para>
/// </summary>
public class StaffPosition : BaseEntity
{
    [Required]
    public Guid BusinessId { get; set; }

    [Required]
    [MaxLength(80)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Description { get; set; }

    /// <summary>Owner-controlled display order (ascending).</summary>
    public int SortOrder { get; set; }

    /// <summary>False keeps existing assignments but removes the label from assignment pickers.</summary>
    public bool IsActive { get; set; } = true;

    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual Business Business { get; set; } = null!;
}

/// <summary>
/// Immutable record of a staff member's position at a point in time.
///
/// <para><see cref="PositionName"/> is a <b>snapshot</b>, not a join: renaming
/// or deleting a <see cref="StaffPosition"/> therefore never rewrites history.
/// Every change (assignment, reassignment, clearing) appends a row.</para>
/// </summary>
public class StaffPositionHistory : BaseEntity
{
    [Required]
    public Guid BusinessId { get; set; }

    [Required]
    public Guid StaffUserId { get; set; }

    /// <summary>The position that was assigned, when it still exists.</summary>
    public Guid? PositionId { get; set; }

    /// <summary>The position label as it was at the time (null when the position was cleared).</summary>
    [MaxLength(80)]
    public string? PositionName { get; set; }

    public Guid? ChangedByUserId { get; set; }

    [Required]
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(300)]
    public string? Note { get; set; }
}
