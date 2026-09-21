using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Loyalty;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

/// <summary>
/// Event-driven stamping, the shared balance-mutation routine, reward unlocking
/// and the activity/audit projections. Split from the main file to keep each
/// concern readable (mirrors the existing <c>BusinessService.Analytics.*</c> split).
/// </summary>
public partial class LoyaltyStampingService
{
    // ═══════════════════════════════════════════════════════════
    //  AUTOMATIC (EVENT-DRIVEN) STAMPING
    // ═══════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<ApiResponse<StampTransactionResponse>> CreditFromEventAsync(
        AutomaticStampCredit credit,
        CancellationToken cancellationToken = default)
    {
        if (credit.Amount < 1)
            return ApiResponse<StampTransactionResponse>.Fail(
                "INVALID_AMOUNT", "Automatic stamp amount must be at least 1.");

        // Defence in depth: never award stamps without the loyalty entitlement.
        if (!await _entitlementService.IsModuleEnabledAsync(credit.BusinessId, LoyaltyModuleKey))
            return ApiResponse<StampTransactionResponse>.Fail(
                "MODULE_DISABLED", "The 'loyalty' module is not enabled for this business.");

        // Idempotency: a retried event returns the original transaction untouched.
        var existing = await _context.StampTransactions
            .Include(t => t.Program)
            .FirstOrDefaultAsync(t => t.IdempotencyKey == credit.IdempotencyKey, cancellationToken);

        if (existing != null)
        {
            _logger.LogDebug(
                "Automatic stamp credit already applied for {IdempotencyKey}; skipping duplicate.",
                credit.IdempotencyKey);
            return ApiResponse<StampTransactionResponse>.Ok(MapTransaction(existing, existing.Program));
        }

        var card = await _context.LoyaltyCards
            .Include(c => c.Program)
            .Include(c => c.Customer)
            .FirstOrDefaultAsync(
                c => c.Id == credit.CardId && c.BusinessId == credit.BusinessId, cancellationToken);

        if (card == null)
            return ApiResponse<StampTransactionResponse>.Fail(
                "NOT_FOUND", "Loyalty card not found for this business.");

        // Tenant integrity: the credit must name the program the card is actually on.
        if (card.ProgramId != credit.ProgramId)
            return ApiResponse<StampTransactionResponse>.Fail(
                "PROGRAM_MISMATCH", "The earning rule's program does not match the customer's card.");

        var lifecycleError = ValidateProgramCanEarn(card.Program);
        if (lifecycleError != null)
            return ApiResponse<StampTransactionResponse>.Fail(lifecycleError.Value.Code, lifecycleError.Value.Message);

        var result = await ApplyBalanceChangeAsync(
            card,
            amount: credit.Amount,
            direction: StampTransactionDirection.Credit,
            source: credit.Source,
            sourceId: credit.SourceId,
            earningRuleId: credit.EarningRuleId,
            isAutomatic: true,
            reason: credit.Reason,
            actorUserId: null,
            actorRole: "System",
            idempotencyKey: credit.IdempotencyKey,
            metadataJson: null);

        if (!result.Success)
            return ApiResponse<StampTransactionResponse>.Fail(result.Error!.Code, result.Error!.Message);

        return ApiResponse<StampTransactionResponse>.Ok(result.Data!.Transaction);
    }

    // ═══════════════════════════════════════════════════════════
    //  REWARD REDEMPTION CONSUMPTION
    // ═══════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<ApiResponse<StampTransactionResponse>> ConsumeForRedemptionAsync(
        Guid businessId, Guid cardId, int stamps, Guid entitlementId, string reason)
    {
        if (stamps < 0)
            return ApiResponse<StampTransactionResponse>.Fail(
                "INVALID_AMOUNT", "Redemption consumption must be non-negative.");

        // Tenant integrity: the card must belong to the redeeming business.
        var card = await _context.LoyaltyCards
            .Include(c => c.Program)
            .FirstOrDefaultAsync(c => c.Id == cardId && c.BusinessId == businessId);

        if (card == null)
            return ApiResponse<StampTransactionResponse>.Fail(
                "NOT_FOUND", "Loyalty card not found for this business.");

        // Idempotency: a retried redemption returns the original debit untouched.
        // Note the idempotency key is scoped to the entitlement, not to
        // (program, rule, source, sourceId) like automatic earning, because a
        // card can redeem different entitlements over time.
        var idempotencyKey = $"REDEMPTION:{cardId}:{entitlementId}";

        var existing = await _context.StampTransactions
            .Include(t => t.Program)
            .FirstOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey);

        if (existing != null)
        {
            _logger.LogDebug(
                "Redemption consumption already applied for {IdempotencyKey}; skipping duplicate.",
                idempotencyKey);
            return ApiResponse<StampTransactionResponse>.Ok(MapTransaction(existing, existing.Program));
        }

        var result = await ApplyBalanceChangeAsync(
            card,
            amount: stamps,
            direction: StampTransactionDirection.Debit,
            source: StampTransactions.Redemption,
            sourceId: entitlementId,
            earningRuleId: null,
            isAutomatic: true,
            reason: reason,
            actorUserId: null,
            actorRole: "System",
            idempotencyKey: idempotencyKey,
            metadataJson: null);

        if (!result.Success)
            return ApiResponse<StampTransactionResponse>.Fail(result.Error!.Code, result.Error!.Message);

        return ApiResponse<StampTransactionResponse>.Ok(result.Data!.Transaction);
    }
}