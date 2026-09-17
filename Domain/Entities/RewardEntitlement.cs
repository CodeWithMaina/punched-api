using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// A customer's unlocked, redeemable reward. Created exactly once per
/// (card, reward, earning cycle) the moment the threshold is reached, so two
/// concurrent qualifying events can never produce duplicate entitlements.
/// Redeeming transitions it to <see cref="RewardEntitlementStatus.Redeemed"/>
/// and links the <see cref="Redemption"/> that consumed the stamps; the row is
/// never deleted, preserving the full audit trail.
/// </summary>
public class RewardEntitlement : BaseEntity
{
    /// <summary>FK to the owning business (tenant scope for every read).</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>FK to the program the reward belongs to.</summary>
    [Required]
    public Guid ProgramId { get; set; }

    /// <summary>FK to the <see cref="LoyaltyCard"/> that earned this entitlement.</summary>
    [Required]
    public Guid CardId { get; set; }

    /// <summary>Denormalized FK to the customer, for customer-facing reward lists.</summary>
    [Required]
    public Guid CustomerId { get; set; }

    /// <summary>FK to the <see cref="Reward"/> that was unlocked.</summary>
    [Required]
    public Guid RewardId { get; set; }

    /// <summary>Snapshot of the reward name at unlock time (survives reward renames).</summary>
    [Required]
    [MaxLength(200)]
    public string RewardName { get; set; } = string.Empty;

    /// <summary>Snapshot of the stamps required at unlock time (survives config changes).</summary>
    [Required]
    public int RequiredStamps { get; set; }

    /// <summary>Snapshot of the stamps to consume at unlock time.</summary>
    [Required]
    public int StampsToConsume { get; set; }

    /// <summary>Snapshot of the reward value in KES at unlock time.</summary>
    public decimal RewardValue { get; set; }

    /// <summary>Lifecycle status.</summary>
    [Required]
    public RewardEntitlementStatus Status { get; set; } = RewardEntitlementStatus.Unlocked;

    /// <summary>UTC timestamp when the threshold was reached.</summary>
    [Required]
    public DateTime UnlockedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when the entitlement expires (null = never).</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>UTC timestamp when the entitlement was redeemed (null until redeemed).</summary>
    public DateTime? RedeemedAt { get; set; }

    /// <summary>FK to the <see cref="Redemption"/> created when this entitlement was redeemed.</summary>
    public Guid? RedemptionId { get; set; }

    /// <summary>
    /// Deterministic uniqueness token, shaped as <c>{cardId}:{rewardId}:{cycle}</c>,
    /// where cycle is the number of times the card has already cycled. A unique
    /// index makes concurrent unlock attempts converge on a single entitlement.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string UnlockKey { get; set; } = string.Empty;

    // ── Navigation ──────────────────────────────────────────
    public virtual LoyaltyProgram Program { get; set; } = null!;
    public virtual LoyaltyCard Card { get; set; } = null!;
    public virtual Reward Reward { get; set; } = null!;
    public virtual Redemption? Redemption { get; set; }
}