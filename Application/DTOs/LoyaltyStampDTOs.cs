using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  LOYALTY MODULE — MANUAL STAMPING, LEDGER & ACTIVITY
// ═══════════════════════════════════════════════════════════════

/// <summary>Request body for a manual stamp award (positive or negative).</summary>
public class ManualStampRequest
{
    /// <summary>FK to the customer's loyalty card.</summary>
    [JsonPropertyName("cardId")] public Guid CardId { get; set; }

    /// <summary>
    /// Stamps to add. Use a negative value for a correction (e.g. -2).
    /// Must be non-zero, within -100..100.
    /// </summary>
    [JsonPropertyName("amount")] public int Amount { get; set; }

    /// <summary>Why the stamps were awarded/corrected. Required, max 500 chars.</summary>
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
}

/// <summary>A single entry in a card's stamp ledger.</summary>
public class StampTransactionResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("programId")] public Guid ProgramId { get; set; }
    [JsonPropertyName("programName")] public string ProgramName { get; set; } = string.Empty;
    [JsonPropertyName("cardId")] public Guid CardId { get; set; }
    [JsonPropertyName("customerId")] public Guid CustomerId { get; set; }
    [JsonPropertyName("customerName")] public string? CustomerName { get; set; }

    /// <summary>Always positive; the sign is carried by <see cref="Direction"/>.</summary>
    [JsonPropertyName("amount")] public int Amount { get; set; }

    /// <summary>"credit" | "debit".</summary>
    [JsonPropertyName("direction")] public string Direction { get; set; } = "credit";

    /// <summary>Signed balance effect (+credit / −debit), for display.</summary>
    [JsonPropertyName("signedAmount")] public int SignedAmount { get; set; }

    /// <summary>"APPOINTMENT" | "SERVICE" | "REFERRAL" | "MANUAL" | "ENROLLMENT" | "REDEMPTION" | "ADJUSTMENT".</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;

    [JsonPropertyName("sourceId")] public Guid? SourceId { get; set; }
    [JsonPropertyName("earningRuleId")] public Guid? EarningRuleId { get; set; }
    [JsonPropertyName("isAutomatic")] public bool IsAutomatic { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("createdByUserId")] public Guid? CreatedByUserId { get; set; }
    [JsonPropertyName("createdByName")] public string? CreatedByName { get; set; }
    [JsonPropertyName("createdByRole")] public string? CreatedByRole { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
}

/// <summary>Result of a manual stamp award, including the card's new state.</summary>
public class ManualStampResponse
{
    [JsonPropertyName("cardId")] public Guid CardId { get; set; }
    [JsonPropertyName("customerId")] public Guid CustomerId { get; set; }
    [JsonPropertyName("amount")] public int Amount { get; set; }
    [JsonPropertyName("totalStamps")] public int TotalStamps { get; set; }
    [JsonPropertyName("stampsRequired")] public int StampsRequired { get; set; }
    [JsonPropertyName("transaction")] public StampTransactionResponse Transaction { get; set; } = new();

    /// <summary>Set when this award unlocked a reward.</summary>
    [JsonPropertyName("unlockedReward")] public RewardEntitlementResponse? UnlockedReward { get; set; }
}

/// <summary>Query for the business-facing loyalty activity/audit log.</summary>
public class LoyaltyActivityQuery
{
    [JsonPropertyName("page")] public int Page { get; set; } = 1;
    [JsonPropertyName("pageSize")] public int PageSize { get; set; } = 25;

    /// <summary>Optional program filter. Null = all programs owned by the business.</summary>
    [JsonPropertyName("programId")] public Guid? ProgramId { get; set; }

    /// <summary>Optional customer filter.</summary>
    [JsonPropertyName("customerId")] public Guid? CustomerId { get; set; }

    /// <summary>Optional source filter (APPOINTMENT, MANUAL, …). Case-sensitive.</summary>
    [JsonPropertyName("source")] public string? Source { get; set; }

    /// <summary>Optional filter: only automatic awards (true) or only manual actions (false).</summary>
    [JsonPropertyName("automaticOnly")] public bool? AutomaticOnly { get; set; }
}

/// <summary>A page of loyalty activity rows.</summary>
public class LoyaltyActivityPage
{
    [JsonPropertyName("items")] public List<StampTransactionResponse> Items { get; set; } = new();
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("pageSize")] public int PageSize { get; set; }
    [JsonPropertyName("totalCount")] public int TotalCount { get; set; }
    [JsonPropertyName("totalPages")] public int TotalPages { get; set; }
}