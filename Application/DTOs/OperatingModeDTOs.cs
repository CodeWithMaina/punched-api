using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  BUSINESS OPERATING MODE — DTOs
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// The business's operating mode plus everything the UI needs to explain and
/// switch it without a second round trip.
/// </summary>
public class OperatingModeResponse
{
    /// <summary><c>solo</c> | <c>multi_staff</c>.</summary>
    [JsonPropertyName("operatingMode")] public string OperatingMode { get; set; } = "multi_staff";

    /// <summary>Human label, e.g. "Solo Business" — so the badge and settings agree.</summary>
    [JsonPropertyName("operatingModeLabel")] public string OperatingModeLabel { get; set; } = "Multi-Staff Business";

    /// <summary>
    /// The effective provider model after resolving the mode against module
    /// entitlements: <c>solo</c> | <c>owner_only</c> | <c>category_based</c>.
    /// </summary>
    [JsonPropertyName("effectiveProviderModel")] public string EffectiveProviderModel { get; set; } = "category_based";

    /// <summary>Whether the Staff module is currently entitled for this business.</summary>
    [JsonPropertyName("staffModuleEnabled")] public bool StaffModuleEnabled { get; set; }

    /// <summary>True when the customer must pick a provider during booking.</summary>
    [JsonPropertyName("requiresProviderSelection")] public bool RequiresProviderSelection { get; set; }

    /// <summary>Owner-facing explanation of what the current mode means.</summary>
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;

    [JsonPropertyName("categoryCount")] public int CategoryCount { get; set; }
    [JsonPropertyName("activeServiceCount")] public int ActiveServiceCount { get; set; }
    [JsonPropertyName("staffCount")] public int StaffCount { get; set; }

    /// <summary>Whether the owner is currently an eligible provider.</summary>
    [JsonPropertyName("ownerIsProvider")] public bool OwnerIsProvider { get; set; }

    /// <summary>
    /// The owner's user id, so a provider toggle can target them through the
    /// provider-config endpoint. Null only when the business has no owner row.
    /// </summary>
    [JsonPropertyName("ownerUserId")] public Guid? OwnerUserId { get; set; }

    /// <summary>The categories the owner is assigned to (empty in solo mode).</summary>
    [JsonPropertyName("ownerCategoryIds")] public List<Guid> OwnerCategoryIds { get; set; } = [];
}

/// <summary>Switches the business operating mode in one action.</summary>
public class UpdateOperatingModeRequest
{
    /// <summary><c>solo</c> or <c>multi_staff</c>.</summary>
    [JsonPropertyName("operatingMode")] public string OperatingMode { get; set; } = string.Empty;
}

/// <summary>One category a provider specialises in.</summary>
public class ProviderCategoryResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
    [JsonPropertyName("serviceCount")] public int ServiceCount { get; set; }
}

/// <summary>A provider (staff member or owner) and their category specialisations.</summary>
public class ProviderCategoriesResponse
{
    [JsonPropertyName("providerUserId")] public Guid ProviderUserId { get; set; }
    [JsonPropertyName("providerName")] public string ProviderName { get; set; } = string.Empty;
    [JsonPropertyName("isOwner")] public bool IsOwner { get; set; }

    /// <summary>
    /// Whether this provider is offering services (their membership
    /// <c>IsBookable</c>). Owners default to true; a provider turned off here is
    /// never offered or booked even if categories remain assigned.
    /// </summary>
    [JsonPropertyName("isProvider")] public bool IsProvider { get; set; } = true;

    [JsonPropertyName("effectiveProviderModel")] public string EffectiveProviderModel { get; set; } = "category_based";
    [JsonPropertyName("categories")] public List<ProviderCategoryResponse> Categories { get; set; } = [];
}

/// <summary>
/// Replacement set of categories a provider specialises in. The array is the
/// whole set — assigning removes any category not listed.
/// </summary>
public class SetProviderCategoriesRequest
{
    /// <summary>
    /// The replacement category set, or <c>null</c> to leave the current set
    /// untouched (used when only the "providing services" switch is changing).
    /// </summary>
    [JsonPropertyName("categoryIds")] public List<Guid>? CategoryIds { get; set; }

    /// <summary>
    /// Optional "is this person providing services" switch. Omitted leaves the
    /// current value unchanged; false takes the provider out of the booking pool
    /// (the owner can toggle this in any business type).
    /// </summary>
    [JsonPropertyName("isProvider")] public bool? IsProvider { get; set; }
}
