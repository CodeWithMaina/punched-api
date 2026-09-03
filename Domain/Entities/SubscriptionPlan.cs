using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// A subscription plan offered on the platform (e.g. Starter, Growth, Pro).
/// A plan bundles a set of modules via <see cref="PlanModule"/> join rows.
/// </summary>
public class SubscriptionPlan : BaseEntity
{
    /// <summary>
    /// Stable, lowercase unique key (e.g. "starter", "pro").
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of the plan.
    /// </summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Recurring price in the platform currency (KES).
    /// </summary>
    [Range(0, 1_000_000)]
    public decimal Price { get; set; }

    /// <summary>
    /// Billing interval: "monthly" or "yearly".
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string BillingInterval { get; set; } = "monthly";

    /// <summary>
    /// Soft availability flag. Inactive plans cannot be subscribed to.
    /// Backward-compatible convenience column mirrored to
    /// <see cref="LifecycleState"/> (Active ⇔ true). Must never diverge.
    /// </summary>
    public bool IsActive { get; set; } = true;

    // ── Admin tier lifecycle ─────────────────────────────────

    /// <summary>
    /// Display order used to sort tiers in admin/upgrade UIs. Null = un-ordered.
    /// </summary>
    public int? DisplayOrder { get; set; }

    /// <summary>
    /// Whether this tier is the platform default (auto-assigned to new businesses).
    /// At most one tier may be the default (enforced by a unique partial index).
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Current lifecycle state of this tier. Active tiers course assignable and
    /// have read-only module sets; Draft/Inactive are editable; Archived is terminal.
    /// </summary>
    public SubscriptionPlanLifecycleState LifecycleState { get; set; } = SubscriptionPlanLifecycleState.Draft;

    /// <summary>UTC timestamp when the tier was last published (null if never published).</summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>UTC timestamp when the tier was last deactivated (null if never deactivated).</summary>
    public DateTime? DeactivatedAt { get; set; }

    /// <summary>UTC timestamp when the tier was archived (null if never archived).</summary>
    public DateTime? ArchivedAt { get; set; }

    /// <summary>FK to the admin user who last published this tier (null if never published).</summary>
    public Guid? LastPublishedByUserId { get; set; }

    /// <summary>
    /// Convenience helper: whether this tier may be newly assigned to businesses.
    /// Only <see cref="SubscriptionPlanLifecycleState.Active"/> tiers are assignable.
    /// </summary>
    public bool IsAssignable => LifecycleState == SubscriptionPlanLifecycleState.Active;

    // ── Navigation ──────────────────────────────────────────
    /// <summary>
    /// Modules included in this plan.
    /// </summary>
    public virtual ICollection<PlanModule> PlanModules { get; set; } = new List<PlanModule>();

    /// <summary>
    /// Business subscriptions currently on this plan.
    /// </summary>
    public virtual ICollection<BusinessSubscription> BusinessSubscriptions { get; set; } = new List<BusinessSubscription>();
}