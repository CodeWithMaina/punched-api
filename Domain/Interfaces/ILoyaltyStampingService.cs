using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// The single write path for every loyalty stamp balance change. Manual awards
/// and automatic event-driven awards both funnel through here so lifecycle
/// rules, idempotency, audit and reward unlocking cannot diverge.
/// </summary>
public interface ILoyaltyStampingService
{
    /// <summary>
    /// Awards (or corrects, when negative) stamps on a customer's card on behalf
    /// of a business/staff user. Records actor, reason and timestamp.
    /// </summary>
    Task<ApiResponse<ManualStampResponse>> AwardManualStampsAsync(Guid actorUserId, ManualStampRequest request);

    /// <summary>
    /// Credits stamps produced by a domain event. Idempotent: replaying the same
    /// (program, rule, source, sourceId) never awards a second time.
    /// </summary>
    Task<ApiResponse<StampTransactionResponse>> CreditFromEventAsync(
        AutomaticStampCredit credit,
        CancellationToken cancellationToken = default);

    /// <summary>Business-facing loyalty activity/audit log, tenant-scoped and paged.</summary>
    Task<ApiResponse<LoyaltyActivityPage>> GetActivityAsync(Guid actorUserId, LoyaltyActivityQuery query);

    /// <summary>A single card's stamp transaction history (business view).</summary>
    Task<ApiResponse<LoyaltyActivityPage>> GetCardHistoryAsync(Guid actorUserId, Guid cardId, int page, int pageSize);

    /// <summary>
    /// Consumes stamps for a redeemed reward entitlement as an auditable
    /// REDEMPTION debit. Idempotent per entitlement — a retried redemption never
    /// double-consumes. Internal: called by <c>LoyaltyRewardService</c> inside
    /// its redemption transaction.
    /// </summary>
    Task<ApiResponse<StampTransactionResponse>> ConsumeForRedemptionAsync(
        Guid businessId, Guid cardId, int stamps, Guid entitlementId, string reason);
}

/// <summary>
/// Internal contract describing an event-driven stamp credit. Constructed by the
/// Loyalty event handlers, never by clients.
/// </summary>
public sealed record AutomaticStampCredit(
    Guid BusinessId,
    Guid ProgramId,
    Guid EarningRuleId,
    Guid CardId,
    Guid CustomerId,
    int Amount,
    string Source,
    Guid? SourceId,
    string? Reason)
{
    /// <summary>
    /// Deterministic idempotency token. A unique DB index on
    /// <see cref="StampTransaction.IdempotencyKey"/> enforces it, so concurrent
    /// or retried events converge on a single transaction.
    /// </summary>
    public string IdempotencyKey =>
        $"{ProgramId}:{EarningRuleId}:{Source}:{SourceId?.ToString() ?? "none"}";
}