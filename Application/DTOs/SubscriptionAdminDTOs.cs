using System.Text.Json.Serialization;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  ADMIN SUBSCRIPTION / TIER DTOs
//  The DB object stays SubscriptionPlan; "tier" is the API/UI boundary.
// ═══════════════════════════════════════════════════════════════

/// <summary>A module in the admin catalog (runtime manifest + DB metadata).</summary>
public class AdminCatalogModule
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; } = string.Empty;
    [JsonPropertyName("isCore")] public bool IsCore { get; set; }
    [JsonPropertyName("visibility")] public string Visibility { get; set; } = "Standard";
    [JsonPropertyName("dependencies")] public List<string> Dependencies { get; set; } = new();
}

/// <summary>Row in the admin tier list.</summary>
public class AdminTierSummary
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("price")] public decimal Price { get; set; }
    [JsonPropertyName("billingInterval")] public string BillingInterval { get; set; } = "monthly";
    [JsonPropertyName("lifecycle")] public string Lifecycle { get; set; } = "Draft";
    [JsonPropertyName("isDefault")] public bool IsDefault { get; set; }
    [JsonPropertyName("displayOrder")] public int? DisplayOrder { get; set; }
    [JsonPropertyName("moduleCount")] public int ModuleCount { get; set; }
    [JsonPropertyName("businessCount")] public int BusinessCount { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime? UpdatedAt { get; set; }
}

/// <summary>Single module membership on a tier (module-management view).</summary>
public class TierModuleView
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; } = string.Empty;
    [JsonPropertyName("isCore")] public bool IsCore { get; set; }
    [JsonPropertyName("visibility")] public string Visibility { get; set; } = "Standard";
    [JsonPropertyName("included")] public bool Included { get; set; }
    [JsonPropertyName("dependencies")] public List<string> Dependencies { get; set; } = new();
}

/// <summary>Full tier detail (tier detail page).</summary>
public class AdminTierDetail
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("price")] public decimal Price { get; set; }
    [JsonPropertyName("billingInterval")] public string BillingInterval { get; set; } = "monthly";
    [JsonPropertyName("lifecycle")] public string Lifecycle { get; set; } = "Draft";
    [JsonPropertyName("isDefault")] public bool IsDefault { get; set; }
    [JsonPropertyName("displayOrder")] public int? DisplayOrder { get; set; }
    [JsonPropertyName("publishedAt")] public DateTime? PublishedAt { get; set; }
    [JsonPropertyName("deactivatedAt")] public DateTime? DeactivatedAt { get; set; }
    [JsonPropertyName("archivedAt")] public DateTime? ArchivedAt { get; set; }
    [JsonPropertyName("lastPublishedByUserId")] public Guid? LastPublishedByUserId { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("moduleCount")] public int ModuleCount { get; set; }
    [JsonPropertyName("businessCount")] public int BusinessCount { get; set; }
    [JsonPropertyName("modules")] public List<TierModuleView> Modules { get; set; } = new();
}

/// <summary>Body of POST /v1/admin/subscription-tiers.</summary>
public class AdminTierCreateRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("price")] public decimal Price { get; set; }
    [JsonPropertyName("billingInterval")] public string BillingInterval { get; set; } = "monthly";
    [JsonPropertyName("displayOrder")] public int? DisplayOrder { get; set; }
    [JsonPropertyName("isDefault")] public bool IsDefault { get; set; }
    [JsonPropertyName("moduleKeys")] public List<string> ModuleKeys { get; set; } = new();
}

/// <summary>Body of PUT /v1/admin/subscription-tiers/{id}.</summary>
public class AdminTierUpdateRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("price")] public decimal Price { get; set; }
    [JsonPropertyName("billingInterval")] public string BillingInterval { get; set; } = "monthly";
    [JsonPropertyName("displayOrder")] public int? DisplayOrder { get; set; }
    [JsonPropertyName("isDefault")] public bool IsDefault { get; set; }
}

/// <summary>Body of PUT /v1/admin/subscription-tiers/{id}/modules — the complete module set.</summary>
public class AdminTierModulesRequest
{
    [JsonPropertyName("moduleKeys")] public List<string> ModuleKeys { get; set; } = new();
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

/// <summary>Response of GET/PUT a tier's module set.</summary>
public class AdminTierModulesResponse
{
    [JsonPropertyName("tierId")] public Guid TierId { get; set; }
    [JsonPropertyName("moduleKeys")] public List<string> ModuleKeys { get; set; } = new();
    [JsonPropertyName("modules")] public List<TierModuleView> Modules { get; set; } = new();
}

/// <summary>A business subscribed to a tier (tier → businesses, paginated).</summary>
public class AdminTierBusinessItem
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "active";
    [JsonPropertyName("startsAt")] public DateTime? StartsAt { get; set; }
    [JsonPropertyName("endsAt")] public DateTime? EndsAt { get; set; }
    [JsonPropertyName("moduleCount")] public int ModuleCount { get; set; }
}

/// <summary>Effective-access source for a module on a business.</summary>
public class BusinessEffectiveModule
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; } = string.Empty;
    [JsonPropertyName("isCore")] public bool IsCore { get; set; }
    [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; }
    [JsonPropertyName("hasAccess")] public bool HasAccess { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "PLAN";
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("dependencies")] public List<string> Dependencies { get; set; } = new();
}

/// <summary>Aggregate business → subscription view (business subscription page).</summary>
public class BusinessSubscriptionDetail
{
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("businessName")] public string BusinessName { get; set; } = string.Empty;
    [JsonPropertyName("currentTier")] public AdminTierSummary? CurrentTier { get; set; }
    [JsonPropertyName("subscriptionStatus")] public string? SubscriptionStatus { get; set; }
    [JsonPropertyName("startsAt")] public DateTime? StartsAt { get; set; }
    [JsonPropertyName("endsAt")] public DateTime? EndsAt { get; set; }
    [JsonPropertyName("effectiveModules")] public List<BusinessEffectiveModule> EffectiveModules { get; set; } = new();
    [JsonPropertyName("auditHistory")] public List<SubscriptionAuditLogDto> AuditHistory { get; set; } = new();
}

/// <summary>A single subscription audit record (admin audit feed / history).</summary>
public class SubscriptionAuditLogDto
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    [JsonPropertyName("actorUserId")] public Guid? ActorUserId { get; set; }
    [JsonPropertyName("actorName")] public string? ActorName { get; set; }
    [JsonPropertyName("targetBusinessId")] public Guid? TargetBusinessId { get; set; }
    [JsonPropertyName("targetPlanId")] public Guid? TargetPlanId { get; set; }
    [JsonPropertyName("payload")] public object? Payload { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
}

/// <summary>Response of the admin module catalog endpoint (GET /v1/admin/modules).</summary>
public class AdminModulesCatalogResponse
{
    [JsonPropertyName("modules")] public List<AdminCatalogModule> Modules { get; set; } = new();
}

/// <summary>KPI summary for the tier list header.</summary>
public class AdminSubscriptionSummary
{
    [JsonPropertyName("activeTiers")] public int ActiveTiers { get; set; }
    [JsonPropertyName("draftTiers")] public int DraftTiers { get; set; }
    [JsonPropertyName("totalSubscribers")] public int TotalSubscribers { get; set; }
    [JsonPropertyName("totalTiers")] public int TotalTiers { get; set; }
}