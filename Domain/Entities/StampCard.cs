using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// An individual stamp card customers can use within a loyalty program (campaign).
/// A campaign owns one or more stamp cards (one-to-many); each card defines its
/// own stamp goal, reward and visual design. The classic program-level fields on
/// <see cref="LoyaltyProgram"/> remain the default/stamp-pipeline values so
/// existing stamping behaviour is unchanged.
/// </summary>
public class StampCard : BaseEntity
{
    /// <summary>FK to the campaign (LoyaltyProgram) that owns this card.</summary>
    [Required]
    public Guid ProgramId { get; set; }

    /// <summary>Denormalized FK to the owning Business (fast ownership checks).</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>Display name (e.g. "Coffee Card", "VIP Coffee Card"). Max 100 chars.</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "Stamp Card";

    /// <summary>Optional human-readable description shown in the campaign manager.</summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>Number of stamps required to complete this card (1-100).</summary>
    [Required]
    [Range(1, 100)]
    public int StampsRequired { get; set; } = 10;

    /// <summary>Human-readable reward for completing the card. Max 200 chars.</summary>
    [Required]
    [MaxLength(200)]
    public string RewardDescription { get; set; } = string.Empty;

    /// <summary>Monetary value of the reward in KES.</summary>
    [Required]
    public decimal RewardValue { get; set; }

    /// <summary>Lifecycle status (see <see cref="StampCardStatus"/>).</summary>
    public StampCardStatus Status { get; set; } = StampCardStatus.Draft;

    /// <summary>
    /// FK to the reusable <see cref="CardDesign"/> controlling this card's visuals.
    /// Null means the card uses the app's built-in default rendering.
    /// </summary>
    public Guid? CardDesignId { get; set; }

    // ── Navigation ──────────────────────────────────────────
    public virtual LoyaltyProgram Program { get; set; } = null!;
    public virtual Business Business { get; set; } = null!;
    public virtual CardDesign? CardDesign { get; set; }
}
