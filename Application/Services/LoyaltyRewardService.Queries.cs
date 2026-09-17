using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Entitlement lists and aggregate loyalty metrics.
/// </summary>
public partial class LoyaltyRewardService
{
    /// <inheritdoc />
    public async Task<ApiResponse<List<RewardEntitlementResponse>>> GetCardEntitlementsAsync(
        Guid actorUserId, Guid cardId)
    {
        var scope = await _scopeResolver.ResolveAsync(actorUserId, "rewards.view");
        if (!scope.Success)
            return ApiResponse<List<RewardEntitlementResponse>>.Fail(scope.ErrorCode!, scope.ErrorMessage!);

        var businessId = scope.Actor!.BusinessId;

        var card = await _context.LoyaltyCards
            .AsNoTracking()
            .Include(c => c.Program)
            .Include(c => c.Customer)
            .FirstOrDefaultAsync(c => c.Id == cardId && c.BusinessId == businessId);

        if (card == null)
            return ApiResponse<List<RewardEntitlementResponse>>.Fail(
                "NOT_FOUND", "Loyalty card not found for this business.");

        var entitlements = await LoadEntitlementsAsync(businessId, cardId, customerId: null);

        return ApiResponse<List<RewardEntitlementResponse>>.Ok(
            entitlements.Select(e => MapEntitlement(e, card.Program?.Name, card.Customer?.FullName)).ToList());
    }

    /// <inheritdoc />
    public async Task<ApiResponse<List<RewardEntitlementResponse>>> GetMyEntitlementsAsync(Guid customerId)
    {
        // Customer-scoped read: only the caller's own entitlements, all businesses.
        var entitlements = await LoadEntitlementsAsync(businessId: null, cardId: null, customerId);

        return ApiResponse<List<RewardEntitlementResponse>>.Ok(
            entitlements.Select(e => MapEntitlement(e, e.Card?.Program?.Name)).ToList());
    }

    /// <summary>Tenant- and/or customer-scoped entitlement read.</summary>
    private async Task<List<RewardEntitlement>> LoadEntitlementsAsync(
        Guid? businessId, Guid? cardId, Guid? customerId)
    {
        var q = _context.RewardEntitlements
            .AsNoTracking()
            .Include(e => e.Card)
            .ThenInclude(c => c.Program)
            .AsQueryable();

        if (businessId.HasValue) q = q.Where(e => e.BusinessId == businessId.Value);
        if (cardId.HasValue) q = q.Where(e => e.CardId == cardId.Value);
        if (customerId.HasValue) q = q.Where(e => e.CustomerId == customerId.Value);

        return await q.OrderByDescending(e => e.UnlockedAt).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<ApiResponse<LoyaltyMetricsResponse>> GetMetricsAsync(
        Guid actorUserId, Guid? programId = null)
    {
        var scope = await _scopeResolver.ResolveAsync(actorUserId, "loyalty.view");
        if (!scope.Success)
            return ApiResponse<LoyaltyMetricsResponse>.Fail(scope.ErrorCode!, scope.ErrorMessage!);

        var businessId = scope.Actor!.BusinessId;

        var programs = _context.LoyaltyPrograms
            .AsNoTracking()
            .Where(p => p.BusinessId == businessId);

        if (programId.HasValue) programs = programs.Where(p => p.Id == programId.Value);

        var programIds = await programs.Select(p => p.Id).ToListAsync();

        var credits = _context.StampTransactions
            .AsNoTracking()
            .Where(t => t.BusinessId == businessId
                && programIds.Contains(t.ProgramId)
                && t.Direction == StampTransactionDirection.Credit);

        var entitlements = _context.RewardEntitlements
            .AsNoTracking()
            .Where(e => e.BusinessId == businessId && programIds.Contains(e.ProgramId));

        var unlocked = await entitlements.CountAsync();
        var redeemed = await entitlements.CountAsync(e => e.Status == RewardEntitlementStatus.Redeemed);

        var metrics = new LoyaltyMetricsResponse
        {
            ActivePrograms = await programs.CountAsync(p => p.Status == ProgramStatus.Active),
            CustomersEnrolled = await _context.LoyaltyCards
                .AsNoTracking()
                .CountAsync(c => c.BusinessId == businessId && programIds.Contains(c.ProgramId)),
            StampsAwarded = await credits.SumAsync(t => (int?)t.Amount) ?? 0,
            AutomaticStamps = await credits.Where(t => t.IsAutomatic).SumAsync(t => (int?)t.Amount) ?? 0,
            ManualStamps = await credits.Where(t => !t.IsAutomatic).SumAsync(t => (int?)t.Amount) ?? 0,
            RewardsUnlocked = unlocked,
            RewardsRedeemed = redeemed
        };

        metrics.RedemptionRate = unlocked == 0
            ? 0m
            : Math.Round(redeemed / (decimal)unlocked, 4);

        return ApiResponse<LoyaltyMetricsResponse>.Ok(metrics);
    }
}