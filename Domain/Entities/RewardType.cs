namespace PunchedApi.Domain.Entities;

/// <summary>
/// How a <see cref="Reward"/> is delivered. Deliberately small for V1 —
/// the type is stored, not branched on, so new kinds can be added without
/// touching the earning/redemption pipeline.
/// </summary>
public enum RewardType
{
    /// <summary>A free service or product (e.g. "Free haircut").</summary>
    FreeService = 0,

    /// <summary>A percentage discount off a future visit.</summary>
    PercentageDiscount = 1,

    /// <summary>A fixed monetary amount off a future visit (KES).</summary>
    FixedDiscount = 2,

    /// <summary>A voucher or gift card.</summary>
    Voucher = 3,

    /// <summary>Anything else described in free text.</summary>
    Custom = 4
}

/// <summary>
/// Lifecycle of a <see cref="Reward"/>. Only <see cref="Active"/> rewards are
/// unlocked for customers; existing entitlements survive deactivation.
/// </summary>
public enum RewardStatus
{
    Draft = 0,
    Active = 1,
    Inactive = 2,
    Archived = 3
}

/// <summary>
/// Lifecycle of a <see cref="RewardEntitlement"/>: unlocked by earning, then
/// redeemed exactly once, or expired/cancelled without being redeemed.
/// </summary>
public enum RewardEntitlementStatus
{
    /// <summary>Threshold reached; the customer may redeem this reward.</summary>
    Unlocked = 0,

    /// <summary>Redeemed. Terminal — an entitlement can never be redeemed twice.</summary>
    Redeemed = 1,

    /// <summary>Passed its expiry without being redeemed.</summary>
    Expired = 2,

    /// <summary>Cancelled by the business (e.g. program archived mid-flight).</summary>
    Cancelled = 3
}
