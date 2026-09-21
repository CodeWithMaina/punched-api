using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// The reward a customer unlocks by collecting <see cref="RequiredStamps"/>
/// stamps in a program. A program owns zero or more rewards; the program's
/// scalar <see cref="LoyaltyProgram.RewardDescription"/>/<see cref="LoyaltyProgram.StampsRequired"/>
/// remain the single-reward default so legacy programs are unaffected.
/// </summary>
public class Reward : BaseEntity
{
    /// <summary>FK to the owning program.</summary>
    [Required]
    public Guid ProgramId { get; set; }

    /// <summary>Denormalized FK to the owning business.</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>Display name (e.g. "Free Haircut"). Max 200 characters.</summary>
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional customer-facing description. Max 500 characters.</summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Stamps required to unlock this reward, measured against the card's
    /// current cycle balance. Must be ≥ 1.
    /// </summary>
    [Required]
    [Range(1, 100)]
    public int RequiredStamps { get; set; } = 10;

    /// <summary>How the reward is delivered.</summary>
    [Required]
    public RewardType Type { get; set; } = RewardType.FreeService;

    /// <summary>
    /// Monetary value in KES, used by the payout pipeline when the reward is
    /// redeemable for cash value. Zero for non-monetary rewards.
    /// </summary>
    [Range(0, 1_000_000)]
    public decimal Value { get; set; }

    /// <summary>Percentage (1-100) when <see cref="Type"/> is <see cref="RewardType.PercentageDiscount"/>.</summary>
    [Range(0, 100)]
    public int? Percentage { get; set; }

    /// <summary>Lifecycle status; only Active rewards unlock entitlements.</summary>
    [Required]
    public RewardStatus Status { get; set; } = RewardStatus.Draft;

    /// <summary>
    /// Valid for this many hours after unlocking. 0 (default) means no expiry,
    /// matching the legacy program-level behaviour.
    /// </summary>
    [Range(0, 8760)]
    public int ExpirationHours { get; set; }

    /// <summary>
    /// Stamps consumed from the card when this reward is redeemed. Defaults to
    /// <see cref="RequiredStamps"/> — the customer "spends" the cycle they earned.
    /// </summary>
    [Range(0, 100)]
    public int StampsToConsume { get; set; }

    /// <summary>Optional FK to the <see cref="ServiceCatalogItem"/> delivered when Type is FreeService.</summary>
    public Guid? ServiceCatalogItemId { get; set; }

    // ── Navigation ──────────────────────────────────────────
    public virtual LoyaltyProgram Program { get; set; } = null!;
    public virtual Business Business { get; set; } = null!;
    public virtual ICollection<RewardEntitlement> Entitlements { get; set; } = new List<RewardEntitlement>();
}