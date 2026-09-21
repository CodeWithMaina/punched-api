using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Loyalty;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Business-facing loyalty activity/audit reads and the DTO projections.
/// Every query is tenant-scoped to the caller's server-resolved business.
/// </summary>
public partial class LoyaltyStampingService
{
    /// <inheritdoc />
    public async Task<ApiResponse<LoyaltyActivityPage>> GetActivityAsync(
        Guid actorUserId, LoyaltyActivityQuery query)
    {
        var scope = await _scopeResolver.ResolveAsync(actorUserId, "loyalty.view");
        if (!scope.Success)
            return ApiResponse<LoyaltyActivityPage>.Fail(scope.ErrorCode!, scope.ErrorMessage!);

        return await QueryActivityAsync(scope.Actor!.BusinessId, query, cardId: null);
    }

    /// <inheritdoc />
    public async Task<ApiResponse<LoyaltyActivityPage>> GetCardHistoryAsync(
        Guid actorUserId, Guid cardId, int page, int pageSize)
    {
        var scope = await _scopeResolver.ResolveAsync(actorUserId, "loyalty.view");
        if (!scope.Success)
            return ApiResponse<LoyaltyActivityPage>.Fail(scope.ErrorCode!, scope.ErrorMessage!);

        // Tenant isolation: the card must belong to the caller's business.
        var owned = await _context.LoyaltyCards
            .AsNoTracking()
            .AnyAsync(c => c.Id == cardId && c.BusinessId == scope.Actor!.BusinessId);

        if (!owned)
            return ApiResponse<LoyaltyActivityPage>.Fail(
                "NOT_FOUND", "Loyalty card not found for this business.");

        return await QueryActivityAsync(
            scope.Actor!.BusinessId,
            new LoyaltyActivityQuery { Page = page, PageSize = pageSize },
            cardId);
    }

    private async Task<ApiResponse<LoyaltyActivityPage>> QueryActivityAsync(
        Guid businessId, LoyaltyActivityQuery query, Guid? cardId)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? 25 : Math.Min(query.PageSize, 100);

        var q = _context.StampTransactions
            .AsNoTracking()
            .Include(t => t.Program)
            .Include(t => t.Card)
            .ThenInclude(c => c.Customer)
            .Where(t => t.BusinessId == businessId);

        if (cardId.HasValue) q = q.Where(t => t.CardId == cardId.Value);
        if (query.ProgramId.HasValue) q = q.Where(t => t.ProgramId == query.ProgramId.Value);
        if (query.CustomerId.HasValue) q = q.Where(t => t.CustomerId == query.CustomerId.Value);
        if (!string.IsNullOrWhiteSpace(query.Source))
        {
            var source = query.Source.Trim();
            q = q.Where(t => t.Source == source);
        }
        if (query.AutomaticOnly.HasValue)
            q = q.Where(t => t.IsAutomatic == query.AutomaticOnly.Value);

        var total = await q.CountAsync();

        var rows = await q
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return ApiResponse<LoyaltyActivityPage>.Ok(new LoyaltyActivityPage
        {
            Items = rows.Select(t => MapTransaction(
                t, t.Program, t.Card?.Customer?.FullName, t.CreatedByRole)).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            TotalPages = (int)Math.Ceiling(total / (double)pageSize)
        });
    }

    // ── Projections ─────────────────────────────────────────

    internal static StampTransactionResponse MapTransaction(
        StampTransaction t, LoyaltyProgram? program, string? customerName = null, string? actorRole = null) => new()
    {
        Id = t.Id,
        BusinessId = t.BusinessId,
        ProgramId = t.ProgramId,
        ProgramName = program?.Name ?? string.Empty,
        CardId = t.CardId,
        CustomerId = t.CustomerId,
        CustomerName = customerName,
        Amount = t.Amount,
        Direction = t.Direction == StampTransactionDirection.Credit ? "credit" : "debit",
        SignedAmount = t.SignedAmount,
        Source = t.Source,
        SourceId = t.SourceId,
        EarningRuleId = t.EarningRuleId,
        IsAutomatic = t.IsAutomatic,
        Reason = t.Reason,
        CreatedByUserId = t.CreatedByUserId,
        CreatedByRole = t.CreatedByRole ?? actorRole,
        CreatedAt = t.CreatedAt
    };

    internal static RewardEntitlementResponse MapEntitlement(
        RewardEntitlement e, string? programName = null, string? customerName = null) => new()
    {
        Id = e.Id,
        BusinessId = e.BusinessId,
        ProgramId = e.ProgramId,
        ProgramName = programName,
        CardId = e.CardId,
        CustomerId = e.CustomerId,
        CustomerName = customerName,
        RewardId = e.RewardId,
        RewardName = e.RewardName,
        RequiredStamps = e.RequiredStamps,
        StampsToConsume = e.StampsToConsume,
        RewardValue = e.RewardValue,
        Status = e.Status switch
        {
            RewardEntitlementStatus.Redeemed => "redeemed",
            RewardEntitlementStatus.Expired => "expired",
            RewardEntitlementStatus.Cancelled => "cancelled",
            _ => "unlocked"
        },
        UnlockedAt = e.UnlockedAt,
        ExpiresAt = e.ExpiresAt,
        RedeemedAt = e.RedeemedAt,
        RedemptionId = e.RedemptionId,
        CanRedeem = e.Status == RewardEntitlementStatus.Unlocked &&
                   (e.ExpiresAt == null || e.ExpiresAt > DateTime.UtcNow)
    };
}