using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  LOYALTY MODULE — EARNING RULES
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// A single earning rule as returned by the API. <see cref="IsAvailable"/>
/// reflects the subscription entitlement the rule's source depends on
/// (referral rules require the business to hold the Referrals module).
/// </summary>
public class EarningRuleResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("programId")] public Guid ProgramId { get; set; }
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }

    /// <summary>"appointment" | "service" | "referral".</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;

    [JsonPropertyName("stampAmount")] public int StampAmount { get; set; }

    /// <summary>"manual" | "automatic".</summary>
    [JsonPropertyName("stampingMode")] public string StampingMode { get; set; } = "manual";

    /// <summary>"draft" | "active" | "inactive" | "archived".</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "draft";

    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("qualifyingServiceId")] public Guid? QualifyingServiceId { get; set; }
    [JsonPropertyName("activatedAt")] public DateTime? ActivatedAt { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }

    /// <summary>
    /// False when the rule's source module is not in the business's subscription.
    /// Such rules cannot be created or activated, and are never evaluated.
    /// </summary>
    [JsonPropertyName("isAvailable")] public bool IsAvailable { get; set; } = true;

    /// <summary>Why the rule is unavailable (e.g. the referral module is off). Null when available.</summary>
    [JsonPropertyName("unavailableReason")] public string? UnavailableReason { get; set; }
}

/// <summary>Request body for creating or updating an earning rule.</summary>
public class UpsertEarningRuleRequest
{
    /// <summary>"appointment" | "service" | "referral".</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;

    /// <summary>Stamps awarded each time the rule fires (1-100).</summary>
    [JsonPropertyName("stampAmount")] public int StampAmount { get; set; } = 1;

    /// <summary>"manual" (default) or "automatic".</summary>
    [JsonPropertyName("stampingMode")] public string? StampingMode { get; set; }

    /// <summary>"draft" | "active" | "inactive" | "archived".</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }

    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("qualifyingServiceId")] public Guid? QualifyingServiceId { get; set; }
}

/// <summary>
/// Response for an activated automatic rule: tells the caller explicitly that
/// historical activity is not stamped, per the no-retroactive-stamping rule.
/// </summary>
public class EarningRuleActivationResponse
{
    [JsonPropertyName("rule")] public EarningRuleResponse Rule { get; set; } = new();

    /// <summary>Always false — automatic stamping never back-fills existing activity.</summary>
    [JsonPropertyName("retroactiveStamping")] public bool RetroactiveStamping { get; set; }

    /// <summary>Human-readable notice for the activation confirmation UI.</summary>
    [JsonPropertyName("notice")] public string Notice { get; set; } = string.Empty;
}