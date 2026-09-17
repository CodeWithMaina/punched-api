using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Reward creation and update.
/// </summary>
public partial class LoyaltyRewardService
{
    /// <inheritdoc />
    public async Task<ApiResponse<RewardResponse>> UpsertRewardAsync(
        Guid actorUserId, Guid programId, UpsertRewardRequest request)
    {
        var scope = await _scopeResolver.ResolveAsync(actorUserId, "rewards.manage");
        if (!scope.Success)
            return ApiResponse<RewardResponse>.Fail(scope.ErrorCode!, scope.ErrorMessage!);

        var businessId = scope.Actor!.BusinessId;
        if (!await _entitlementService.IsModuleEnabledAsync(businessId, LoyaltyModuleKey))
            return ApiResponse<RewardResponse>.Fail(
                "MODULE_DISABLED", "The 'loyalty' module is not enabled for this business.");

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length < 2)
            return ApiResponse<RewardResponse>.Fail(
                "INVALID_NAME", "A reward name of at least 2 characters is required.");

        if (request.RequiredStamps < 1 || request.RequiredStamps > 100)
            return ApiResponse<RewardResponse>.Fail(
                "INVALID_REQUIRED_STAMPS", "Required stamps must be between 1 and 100.");

        if (!TryParseRewardType(request.Type, out var rewardType))
            return ApiResponse<RewardResponse>.Fail(
                "INVALID_TYPE",
                "Type must be one of: freeService, percentageDiscount, fixedDiscount, voucher, custom.");

        if (!TryParseRewardStatus(request.Status, out var status))
            return ApiResponse<RewardResponse>.Fail(
                "INVALID_STATUS", "Status must be one of: draft, active, inactive, archived.");

        if (rewardType == RewardType.PercentageDiscount && (request.Percentage is null or < 1 or > 100))
            return ApiResponse<RewardResponse>.Fail(
                "INVALID_PERCENTAGE", "A percentage discount requires a percentage between 1 and 100.");

        var program = await ResolveProgramAsync(businessId, programId);
        if (program == null)
            return ApiResponse<RewardResponse>.Fail("NOT_FOUND", "Loyalty program not found.");

        // A reward a customer can never reach with the card's target is a
        // configuration error, not a valid reward.
        if (request.RequiredStamps > program.StampsRequired)
            return ApiResponse<RewardResponse>.Fail(
                "REWARD_UNREACHABLE",
                $"Required stamps ({request.RequiredStamps}) exceeds this program's stamp card target ({program.StampsRequired}).");

        Reward? reward = null;

        if (request.Id.HasValue)
        {
            reward = await _context.Rewards
                .FirstOrDefaultAsync(r => r.Id == request.Id.Value
                    && r.ProgramId == programId
                    && r.BusinessId == businessId);

            if (reward == null)
                return ApiResponse<RewardResponse>.Fail("NOT_FOUND", "Reward not found.");
        }
        else
        {
            reward = new Reward
            {
                Id = Guid.NewGuid(),
                ProgramId = programId,
                BusinessId = businessId,
                CreatedAt = DateTime.UtcNow
            };
            await _unitOfWork.Rewards.AddAsync(reward);
        }

        reward.Name = request.Name.Trim();
        reward.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        reward.RequiredStamps = request.RequiredStamps;
        reward.Type = rewardType;
        reward.Value = request.Value < 0 ? 0 : request.Value;
        reward.Percentage = rewardType == RewardType.PercentageDiscount ? request.Percentage : null;
        reward.Status = status;
        reward.ExpirationHours = Math.Clamp(request.ExpirationHours, 0, 8760);
        reward.StampsToConsume = Math.Clamp(request.StampsToConsume ?? request.RequiredStamps, 0, 100);
        reward.ServiceCatalogItemId = rewardType == RewardType.FreeService ? request.ServiceCatalogItemId : null;

        _unitOfWork.Rewards.Update(reward);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation(
            "Reward upserted: program={ProgramId} reward={RewardId} required={Required} status={Status}",
            programId, reward.Id, reward.RequiredStamps, reward.Status);

        return ApiResponse<RewardResponse>.Ok(MapReward(reward));
    }
}