using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Append-only domain audit record for subscription/tier/module mutations.
/// Records who did what, to which tier and/or business, with a before/after
/// payload and a human-readable reason. Never updated or deleted.
/// </summary>
public class SubscriptionAuditLog
{
    /// <summary>Unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Action vocabulary, e.g. "TIER_CREATED", "TIER_PUBLISHED",
    /// "BUSINESS_SUBSCRIPTION_CHANGED", "BUSINESS_MODULE_FORCE_ENABLED".
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string Action { get; set; } = string.Empty;

    /// <summary>User id of the actor who performed the mutation (admin id).</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>FK to the affected business (null when the action targets a tier only).</summary>
    public Guid? TargetBusinessId { get; set; }

    /// <summary>FK to the affected tier/plan (null when the action targets a business only).</summary>
    public Guid? TargetPlanId { get; set; }

    /// <summary>Optional JSON payload with relevant before/after information.</summary>
    public string? PayloadJson { get; set; }

    /// <summary>Human-readable reason/detail for the action.</summary>
    [MaxLength(1000)]
    public string? Reason { get; set; }

    /// <summary>UTC timestamp when the action was recorded.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ── Navigation ──────────────────────────────────────────
    /// <summary>The actor user (if recorded).</summary>
    public virtual User? ActorUser { get; set; }

    /// <summary>The affected business (if applicable).</summary>
    public virtual Business? TargetBusiness { get; set; }

    /// <summary>The affected tier/plan (if applicable).</summary>
    public virtual SubscriptionPlan? TargetPlan { get; set; }
}