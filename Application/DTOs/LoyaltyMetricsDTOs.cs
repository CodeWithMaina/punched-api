using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  LOYALTY MODULE — METRICS & BUSINESS-FACING CARD VIEW
// ═══════════════════════════════════════════════════════════════

/// <summary>Aggregate loyalty metrics for a business (optionally one program).</summary>
public class LoyaltyMetricsResponse
{
    [JsonPropertyName("activePrograms")] public int ActivePrograms { get; set; }
    [JsonPropertyName("customersEnrolled")] public int CustomersEnrolled { get; set; }
    [JsonPropertyName("stampsAwarded")] public int StampsAwarded { get; set; }
    [JsonPropertyName("manualStamps")] public int ManualStamps { get; set; }
    [JsonPropertyName("automaticStamps")] public int AutomaticStamps { get; set; }
    [JsonPropertyName("rewardsUnlocked")] public int RewardsUnlocked { get; set; }
    [JsonPropertyName("rewardsRedeemed")] public int RewardsRedeemed { get; set; }

    /// <summary>Rewards redeemed ÷ rewards unlocked (0 when none unlocked).</summary>
    [JsonPropertyName("redemptionRate")] public decimal RedemptionRate { get; set; }
}

/// <summary>A customer's loyalty card summary for the business-facing view.</summary>
public class BusinessLoyaltyCardResponse
{
    [JsonPropertyName("cardId")] public Guid CardId { get; set; }
    [JsonPropertyName("customerId")] public Guid CustomerId { get; set; }
    [JsonPropertyName("customerName")] public string CustomerName { get; set; } = string.Empty;
    [JsonPropertyName("customerPhone")] public string? CustomerPhone { get; set; }
    [JsonPropertyName("programId")] public Guid ProgramId { get; set; }
    [JsonPropertyName("programName")] public string ProgramName { get; set; } = string.Empty;
    [JsonPropertyName("totalStamps")] public int TotalStamps { get; set; }
    [JsonPropertyName("lifetimeStamps")] public int LifetimeStamps { get; set; }
    [JsonPropertyName("stampsRequired")] public int StampsRequired { get; set; }
    [JsonPropertyName("totalRedemptions")] public int TotalRedemptions { get; set; }
    [JsonPropertyName("lastStampAt")] public DateTime? LastStampAt { get; set; }
    [JsonPropertyName("enrolledAt")] public DateTime EnrolledAt { get; set; }

    /// <summary>Unlocked rewards the customer can still redeem.</summary>
    [JsonPropertyName("unlockedRewards")] public List<RewardEntitlementResponse> UnlockedRewards { get; set; } = new();
}