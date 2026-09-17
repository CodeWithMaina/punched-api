using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Enum parsing and reward/entitlement projections for the reward service.
/// </summary>
public partial class LoyaltyRewardService
{
    internal static bool TryParseRewardType(string? value, out RewardType type)
    {
        if (string.IsNullOrWhiteSpace(value)) { type = RewardType.FreeService; return true; }

        switch (value.Trim().ToLowerInvariant())
        {
            case "freeservice": type = RewardType.FreeService; return true;
            case "percentagediscount": type = RewardType.PercentageDiscount; return true;
            case "fixeddiscount": type = RewardType.FixedDiscount; return true;
            case "voucher": type = RewardType.Voucher; return true;
            case "custom": type = RewardType.Custom; return true;
            default: type = RewardType.FreeService; return false;
        }
    }

    /// <summary>Null/absent falls back to Draft — a reward is never live implicitly.</summary>
    internal static bool TryParseRewardStatus(string? value, out RewardStatus status)
    {
        if (string.IsNullOrWhiteSpace(value)) { status = RewardStatus.Draft; return true; }

        switch (value.Trim().ToLowerInvariant())
        {
            case "draft": status = RewardStatus.Draft; return true;
            case "active": status = RewardStatus.Active; return true;
            case "inactive": status = RewardStatus.Inactive; return true;
            case "archived": status = RewardStatus.Archived; return true;
            default: status = RewardStatus.Draft; return false;
        }
    }

    internal static RewardResponse MapReward(Reward r) => new()
    {
        Id = r.Id,
        ProgramId = r.ProgramId,
        BusinessId = r.BusinessId,
        Name = r.Name,
        Description = r.Description,
        RequiredStamps = r.RequiredStamps,
        Type = r.Type switch
        {
            RewardType.PercentageDiscount => "percentageDiscount",
            RewardType.FixedDiscount => "fixedDiscount",
            RewardType.Voucher => "voucher",
            RewardType.Custom => "custom",
            _ => "freeService"
        },
        Value = r.Value,
        Percentage = r.Percentage,
        Status = r.Status switch
        {
            RewardStatus.Active => "active",
            RewardStatus.Inactive => "inactive",
            RewardStatus.Archived => "archived",
            _ => "draft"
        },
        ExpirationHours = r.ExpirationHours,
        StampsToConsume = LoyaltyStampingService.ResolveStampsToConsume(r),
        ServiceCatalogItemId = r.ServiceCatalogItemId,
        CreatedAt = r.CreatedAt
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