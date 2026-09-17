using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Reward configuration, unlocked-reward entitlements and redemption.
///
/// Redemption is transactional and single-use: an entitlement can be redeemed
/// exactly once, stamps are consumed through an immutable ledger entry, and the
/// entitlement row is retained for the audit trail.
/// </summary>
public interface ILoyaltyRewardService
{
    // ── Business: reward configuration ──

    Task<ApiResponse<List<RewardResponse>>> GetRewardsAsync(Guid actorUserId, Guid programId);
    Task<ApiResponse<RewardResponse>> UpsertRewardAsync(Guid actorUserId, Guid programId, UpsertRewardRequest request);
    Task<ApiResponse<RewardResponse>> ArchiveRewardAsync(Guid actorUserId, Guid programId, Guid rewardId);
    Task<ApiResponse<bool>> DeleteRewardAsync(Guid actorUserId, Guid programId, Guid rewardId);

    // ── Entitlements ──

    /// <summary>Business view: rewards a specific customer card has unlocked.</summary>
    Task<ApiResponse<List<RewardEntitlementResponse>>> GetCardEntitlementsAsync(Guid actorUserId, Guid cardId);

    /// <summary>Customer view: the caller's own unlocked rewards across every business.</summary>
    Task<ApiResponse<List<RewardEntitlementResponse>>> GetMyEntitlementsAsync(Guid customerId);

    // ── Redemption ──

    /// <summary>
    /// Redeems an unlocked reward entitlement exactly once. A business/staff
    /// caller may redeem on a customer's behalf; a customer may redeem their own.
    /// </summary>
    Task<ApiResponse<RedeemRewardResponse>> RedeemAsync(Guid actorUserId, string role, RedeemRewardRequest request);

    // ── Analytics ──

    /// <summary>Aggregate loyalty metrics for a business, optionally one program.</summary>
    Task<ApiResponse<LoyaltyMetricsResponse>> GetMetricsAsync(Guid actorUserId, Guid? programId = null);
}