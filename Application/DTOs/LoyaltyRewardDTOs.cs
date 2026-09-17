using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  LOYALTY MODULE — REWARDS & ENTITLEMENTS
// ═══════════════════════════════════════════════════════════════

/// <summary>A configured reward.</summary>
public class RewardResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("programId")] public Guid ProgramId { get; set; }
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("requiredStamps")] public int RequiredStamps { get; set; }

    /// <summary>"freeService" | "percentageDiscount" | "fixedDiscount" | "voucher" | "custom".</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "freeService";

    [JsonPropertyName("value")] public decimal Value { get; set; }
    [JsonPropertyName("percentage")] public int? Percentage { get; set; }

    /// <summary>"draft" | "active" | "inactive" | "archived".</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "draft";

    [JsonPropertyName("expirationHours")] public int ExpirationHours { get; set; }
    [JsonPropertyName("stampsToConsume")] public int StampsToConsume { get; set; }
    [JsonPropertyName("serviceCatalogItemId")] public Guid? ServiceCatalogItemId { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
}

/// <summary>Request body for creating or updating a reward.</summary>
public class UpsertRewardRequest
{
    /// <summary>Existing reward id when updating. Null creates a new reward.</summary>
    [JsonPropertyName("id")] public Guid? Id { get; set; }

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("requiredStamps")] public int RequiredStamps { get; set; } = 10;
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("value")] public decimal Value { get; set; }
    [JsonPropertyName("percentage")] public int? Percentage { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("expirationHours")] public int ExpirationHours { get; set; }

    /// <summary>Stamps consumed on redemption. Omit (null) to default to <see cref="RequiredStamps"/>.</summary>
    [JsonPropertyName("stampsToConsume")] public int? StampsToConsume { get; set; }

    [JsonPropertyName("serviceCatalogItemId")] public Guid? ServiceCatalogItemId { get; set; }
}

/// <summary>An unlocked (or redeemed) reward entitlement.</summary>
public class RewardEntitlementResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("programId")] public Guid ProgramId { get; set; }
    [JsonPropertyName("programName")] public string? ProgramName { get; set; }
    [JsonPropertyName("cardId")] public Guid CardId { get; set; }
    [JsonPropertyName("customerId")] public Guid CustomerId { get; set; }
    [JsonPropertyName("customerName")] public string? CustomerName { get; set; }
    [JsonPropertyName("rewardId")] public Guid RewardId { get; set; }
    [JsonPropertyName("rewardName")] public string RewardName { get; set; } = string.Empty;
    [JsonPropertyName("requiredStamps")] public int RequiredStamps { get; set; }
    [JsonPropertyName("stampsToConsume")] public int StampsToConsume { get; set; }
    [JsonPropertyName("rewardValue")] public decimal RewardValue { get; set; }

    /// <summary>"unlocked" | "redeemed" | "expired" | "cancelled".</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "unlocked";

    [JsonPropertyName("unlockedAt")] public DateTime UnlockedAt { get; set; }
    [JsonPropertyName("expiresAt")] public DateTime? ExpiresAt { get; set; }
    [JsonPropertyName("redeemedAt")] public DateTime? RedeemedAt { get; set; }
    [JsonPropertyName("redemptionId")] public Guid? RedemptionId { get; set; }

    /// <summary>True while the entitlement can still be redeemed.</summary>
    [JsonPropertyName("canRedeem")] public bool CanRedeem { get; set; }
}

/// <summary>Request body for redeeming an unlocked reward entitlement.</summary>
public class RedeemRewardRequest
{
    [JsonPropertyName("entitlementId")] public Guid EntitlementId { get; set; }
}

/// <summary>Result of a successful reward redemption.</summary>
public class RedeemRewardResponse
{
    [JsonPropertyName("entitlement")] public RewardEntitlementResponse Entitlement { get; set; } = new();
    [JsonPropertyName("redemptionId")] public Guid RedemptionId { get; set; }
    [JsonPropertyName("stampsConsumed")] public int StampsConsumed { get; set; }
    [JsonPropertyName("remainingStamps")] public int RemainingStamps { get; set; }

    /// <summary>Short code the customer shows at the counter to collect the reward.</summary>
    [JsonPropertyName("fulfilmentCode")] public string? FulfilmentCode { get; set; }
}