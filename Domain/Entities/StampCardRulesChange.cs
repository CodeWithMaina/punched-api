using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Immutable audit record of a change to a stamp card's *business rules*
/// (required stamps / reward). Distinct from presentation changes, which must
/// never be recorded here because they never affect loyalty state.
///
/// Why this exists (§36, no silent data mutation): changing a card's required
/// stamp count is a material change to every in-flight customer's progress
/// semantics. The system therefore refuses to silently rewrite existing
/// enrolments: the change applies to *new* cycles only, unless the caller
/// explicitly opts in via <see cref="AppliedToExistingCards"/>, and either way
/// this row records who changed what, when, and how many cards were affected.
/// </summary>
public class StampCardRulesChange : BaseEntity
{
    /// <summary>FK to the stamp card whose rules changed.</summary>
    [Required]
    public Guid StampCardId { get; set; }

    /// <summary>Denormalized FK to the owning business (tenant scope for every read).</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>Business/Admin user who made the change (null for system writes).</summary>
    public Guid? ChangedByUserId { get; set; }

    /// <summary>Actor role at the time of the change (e.g. "Business", "Admin").</summary>
    [MaxLength(20)]
    public string? ChangedByRole { get; set; }

    /// <summary>Name of the changed rule: <c>StampsRequired</c>, <c>RewardDescription</c>, <c>RewardValue</c>.</summary>
    [Required]
    [MaxLength(40)]
    public string Field { get; set; } = string.Empty;

    /// <summary>Previous value, serialized as a string so one column covers every field.</summary>
    [MaxLength(500)]
    public string? OldValue { get; set; }

    /// <summary>New value, serialized as a string.</summary>
    [MaxLength(500)]
    public string? NewValue { get; set; }

    /// <summary>
    /// True when the caller explicitly asked for the change to be pushed onto
    /// existing in-flight enrolments. False (the default) means existing
    /// customers keep the requirement they enrolled under.
    /// </summary>
    [Required]
    public bool AppliedToExistingCards { get; set; }

    /// <summary>How many <see cref="LoyaltyCard"/> rows were re-snapshotted (0 when not applied).</summary>
    [Required]
    public int AffectedCards { get; set; }

    /// <summary>Free-form reason supplied by the caller (§33 "reason where appropriate").</summary>
    [MaxLength(500)]
    public string? Reason { get; set; }

    /// <summary>Version number the card moved to as a result of this change.</summary>
    [Required]
    public int RulesVersion { get; set; } = 1;

    // ── Navigation ──────────────────────────────────────────
    public virtual StampCard StampCard { get; set; } = null!;
}
