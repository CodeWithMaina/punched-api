using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Reward unlocking and the loyalty activity/audit projections.
/// </summary>
public partial class LoyaltyStampingService
{
    /// <summary>
    /// Creates entitlements for every Active reward whose threshold the card now
    /// meets, at most once per (card, reward, cycle), enforced by the unique index
    /// on UnlockKey. Never deletes or rewrites history — redemption consumes
    /// stamps through a new transaction instead.
    /// </summary>
    private async Task<List<RewardEntitlementResponse>> UnlockReachedRewardsAsync(
        LoyaltyCard card, LoyaltyProgram program, DateTime now)
    {
        var rewards = await _context.Rewards
            .Where(r => r.ProgramId == program.Id && r.Status == RewardStatus.Active)
            .ToListAsync();

        // Legacy program with no Reward rows: materialise the default reward from
        // the program's scalar columns so the entitlement has a valid Reward FK.
        if (rewards.Count == 0)
            rewards.Add(await EnsureDefaultRewardAsync(program));

        var reached = rewards
            .Where(r => r.RequiredStamps <= card.TotalStamps)
            .OrderBy(r => r.RequiredStamps)
            .ToList();

        if (reached.Count == 0) return new List<RewardEntitlementResponse>();

        // "Cycle" = completed redemptions on this card: exactly one unlock of each
        // reward per cycle, which is what makes concurrent events safe.
        var cycle = card.TotalRedemptions;
        var unlocked = new List<RewardEntitlementResponse>();

        foreach (var reward in reached)
        {
            var unlockKey = $"{card.Id}:{reward.Id}:{cycle}";
            if (await _context.RewardEntitlements.AnyAsync(e => e.UnlockKey == unlockKey))
                continue;

            var entitlement = new RewardEntitlement
            {
                Id = Guid.NewGuid(),
                BusinessId = card.BusinessId,
                ProgramId = program.Id,
                CardId = card.Id,
                CustomerId = card.CustomerId,
                RewardId = reward.Id,
                RewardName = reward.Name,
                RequiredStamps = reward.RequiredStamps,
                StampsToConsume = ResolveStampsToConsume(reward),
                RewardValue = reward.Value,
                Status = RewardEntitlementStatus.Unlocked,
                UnlockedAt = now,
                ExpiresAt = reward.ExpirationHours > 0 ? now.AddHours(reward.ExpirationHours) : null,
                UnlockKey = unlockKey,
                CreatedAt = now
            };

            await _unitOfWork.RewardEntitlements.AddAsync(entitlement);
            unlocked.Add(MapEntitlement(entitlement, program.Name, card.Customer?.FullName));
        }

        if (unlocked.Count > 0)
            _logger.LogInformation(
                "Rewards unlocked for card {CardId} (cycle {Cycle}): {Names}",
                card.Id, cycle, string.Join(", ", unlocked.Select(u => u.RewardName)));

        return unlocked;
    }

    /// <summary>Effective stamps consumed on redemption; defaults to the requirement.</summary>
    internal static int ResolveStampsToConsume(Reward reward) =>
        reward.StampsToConsume > 0 ? reward.StampsToConsume : reward.RequiredStamps;

    /// <summary>
    /// Builds (and persists) the default reward for a program that predates the
    /// Reward model. Backward compatibility only.
    /// </summary>
    private async Task<Reward> EnsureDefaultRewardAsync(LoyaltyProgram program)
    {
        var reward = new Reward
        {
            Id = Guid.NewGuid(),
            ProgramId = program.Id,
            BusinessId = program.BusinessId,
            Name = string.IsNullOrWhiteSpace(program.RewardDescription) ? "Loyalty reward" : program.RewardDescription,
            Description = program.RewardDescription,
            RequiredStamps = Math.Max(1, program.StampsRequired),
            Type = RewardType.Custom,
            Value = program.RewardValue,
            Status = RewardStatus.Active,
            ExpirationHours = program.RewardExpirationHours,
            StampsToConsume = Math.Max(1, program.StampsRequired),
            CreatedAt = DateTime.UtcNow
        };

        await _unitOfWork.Rewards.AddAsync(reward);
        _logger.LogInformation(
            "Materialised default reward for legacy program {ProgramId} from scalar columns.", program.Id);

        return reward;
    }
}