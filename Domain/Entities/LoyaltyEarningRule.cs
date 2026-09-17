using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// A single earning rule inside a <see cref="LoyaltyProgram"/>: "when
/// &lt;source&gt; happens, award &lt;StampAmount&gt; stamps using &lt;StampingMode&gt;".
/// This is the unit the owning modules' domain events are evaluated against —
/// Loyalty owns the rule, the source module owns the event that triggers it.
/// </summary>
public class LoyaltyEarningRule : BaseEntity
{
    /// <summary>FK to the program this rule belongs to.</summary>
    [Required]
    public Guid ProgramId { get; set; }

    /// <summary>
    /// Denormalized FK to the owning business (fast, lock-free tenant checks).
    /// Never derived from client input.
    /// </summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>Qualifying event that awards stamps.</summary>
    [Required]
    public EarningSource Source { get; set; }

    /// <summary>
    /// Stamps awarded each time the rule fires. Must be ≥ 1 — a rule that awards
    /// nothing is a configuration error, not a way to disable earning (use
    /// <see cref="Status"/> for that).
    /// </summary>
    [Required]
    [Range(1, 100)]
    public int StampAmount { get; set; } = 1;

    /// <summary>
    /// Manual (default) or Automatic. Automatic rules are driven by the source
    /// module's domain events; Manual rules require a staff/business action.
    /// </summary>
    [Required]
    public StampingMode StampingMode { get; set; } = StampingMode.Manual;

    /// <summary>Lifecycle status. Only <see cref="EarningRuleStatus.Active"/> rules award stamps.</summary>
    [Required]
    public EarningRuleStatus Status { get; set; } = EarningRuleStatus.Draft;

    /// <summary>
    /// Optional human-readable label shown to staff (e.g. "Completed appointment").
    /// Max 200 characters.
    /// </summary>
    [MaxLength(200)]
    public string? Description { get; set; }

    /// <summary>
    /// Optional UUID of the qualifying <see cref="ServiceCatalogItem"/> when
    /// <see cref="Source"/> is <see cref="EarningSource.Service"/>. Null means any service.
    /// </summary>
    public Guid? QualifyingServiceId { get; set; }

    /// <summary>
    /// UTC timestamp when the rule was last activated. Automatic earning only
    /// considers events at/after this instant, which is what guarantees
    /// "no retroactive stamping" without ever scanning history.
    /// </summary>
    public DateTime? ActivatedAt { get; set; }

    // ── Navigation ──────────────────────────────────────────
    public virtual LoyaltyProgram Program { get; set; } = null!;
    public virtual Business Business { get; set; } = null!;
}
