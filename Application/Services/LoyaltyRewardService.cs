using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Loyalty;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Reward configuration, entitlements, redemption and loyalty analytics.
/// Every query is tenant-scoped to the caller's server-resolved business.
/// </summary>
public partial class LoyaltyRewardService : ILoyaltyRewardService
{
    private const string LoyaltyModuleKey = "loyalty";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ApplicationDbContext _context;
    private readonly ILoyaltyScopeResolver _scopeResolver;
    private readonly IModuleEntitlementService _entitlementService;
    private readonly ILoyaltyStampingService _stampingService;
    private readonly ILogger<LoyaltyRewardService> _logger;

    public LoyaltyRewardService(
        IUnitOfWork unitOfWork,
        ApplicationDbContext context,
        ILoyaltyScopeResolver scopeResolver,
        IModuleEntitlementService entitlementService,
        ILoyaltyStampingService stampingService,
        ILogger<LoyaltyRewardService> logger)
    {
        _unitOfWork = unitOfWork;
        _context = context;
        _scopeResolver = scopeResolver;
        _entitlementService = entitlementService;
        _stampingService = stampingService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ApiResponse<List<RewardResponse>>> GetRewardsAsync(Guid actorUserId, Guid programId)
    {
        var scope = await _scopeResolver.ResolveAsync(actorUserId, "rewards.view");
        if (!scope.Success)
            return ApiResponse<List<RewardResponse>>.Fail(scope.ErrorCode!, scope.ErrorMessage!);

        var businessId = scope.Actor!.BusinessId;
        if (!await _entitlementService.IsModuleEnabledAsync(businessId, LoyaltyModuleKey))
            return ApiResponse<List<RewardResponse>>.Fail(
                "MODULE_DISABLED", "The 'loyalty' module is not enabled for this business.");

        if (await ResolveProgramAsync(businessId, programId) == null)
            return ApiResponse<List<RewardResponse>>.Fail("NOT_FOUND", "Loyalty program not found.");

        var rewards = await _context.Rewards
            .AsNoTracking()
            .Where(r => r.ProgramId == programId && r.BusinessId == businessId)
            .OrderBy(r => r.RequiredStamps)
            .ToListAsync();

        return ApiResponse<List<RewardResponse>>.Ok(rewards.Select(MapReward).ToList());
    }

    /// <inheritdoc />
    public async Task<ApiResponse<RewardResponse>> ArchiveRewardAsync(
        Guid actorUserId, Guid programId, Guid rewardId)
    {
        var (reward, error) = await LoadRewardForMutationAsync(actorUserId, programId, rewardId);
        if (error != null) return ApiResponse<RewardResponse>.Fail(error.Value.Code, error.Value.Message);

        // Archiving stops FUTURE unlocks. Existing entitlements are untouched so
        // customers never lose a reward they already earned.
        reward!.Status = RewardStatus.Archived;

        _unitOfWork.Rewards.Update(reward);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Reward archived: program={ProgramId} reward={RewardId}", programId, rewardId);
        return ApiResponse<RewardResponse>.Ok(MapReward(reward));
    }

    /// <inheritdoc />
    public async Task<ApiResponse<bool>> DeleteRewardAsync(Guid actorUserId, Guid programId, Guid rewardId)
    {
        var (reward, error) = await LoadRewardForMutationAsync(actorUserId, programId, rewardId);
        if (error != null) return ApiResponse<bool>.Fail(error.Value.Code, error.Value.Message);

        // A reward already unlocked by customers is part of the audit trail.
        var hasEntitlements = await _context.RewardEntitlements
            .AsNoTracking()
            .AnyAsync(e => e.RewardId == rewardId);

        if (hasEntitlements)
            return ApiResponse<bool>.Fail(
                "REWARD_IN_USE",
                "This reward has already been unlocked by customers, so it cannot be deleted. Archive it instead.");

        _unitOfWork.Rewards.Delete(reward!);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Reward deleted: program={ProgramId} reward={RewardId}", programId, rewardId);
        return ApiResponse<bool>.Ok(true);
    }

    /// <summary>Resolves actor scope, entitlement, and a tenant-scoped reward.</summary>
    private async Task<(Reward? Reward, (string Code, string Message)? Error)> LoadRewardForMutationAsync(
        Guid actorUserId, Guid programId, Guid rewardId)
    {
        var scope = await _scopeResolver.ResolveAsync(actorUserId, "rewards.manage");
        if (!scope.Success) return (null, (scope.ErrorCode!, scope.ErrorMessage!));

        var businessId = scope.Actor!.BusinessId;

        if (!await _entitlementService.IsModuleEnabledAsync(businessId, LoyaltyModuleKey))
            return (null, ("MODULE_DISABLED", "The 'loyalty' module is not enabled for this business."));

        var reward = await _context.Rewards
            .FirstOrDefaultAsync(r => r.Id == rewardId
                && r.ProgramId == programId
                && r.BusinessId == businessId);

        return reward == null ? (null, ("NOT_FOUND", "Reward not found.")) : (reward, null);
    }
}