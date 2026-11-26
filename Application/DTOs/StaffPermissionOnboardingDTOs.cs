using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

/// <summary>Which staff role currently holds a capability, for the owner's onboarding view.</summary>
public class StaffCapabilityResponse
{
    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    /// <summary>True when at least one active non-owner role in this business grants it.</summary>
    [JsonPropertyName("grantedToStaffRoles")] public bool GrantedToStaffRoles { get; set; }
    /// <summary>Names of the roles that grant it (owner role excluded: it always has everything).</summary>
    [JsonPropertyName("grantedRoles")] public List<string> GrantedRoles { get; set; } = [];
}

/// <summary>One staff role and the new capabilities it has not been granted yet.</summary>
public class StaffRoleOnboardingResponse
{
    [JsonPropertyName("roleId")] public Guid RoleId { get; set; }
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("isSystem")] public bool IsSystem { get; set; }
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
    [JsonPropertyName("memberCount")] public int MemberCount { get; set; }
    [JsonPropertyName("missingRecommendedPermissions")] public List<string> MissingRecommendedPermissions { get; set; } = [];
}

/// <summary>One explicit grant the owner can apply during onboarding.</summary>
public class StaffPermissionRecommendationResponse
{
    [JsonPropertyName("roleId")] public Guid RoleId { get; set; }
    [JsonPropertyName("roleName")] public string RoleName { get; set; } = string.Empty;
    [JsonPropertyName("permissionCode")] public string PermissionCode { get; set; } = string.Empty;
    [JsonPropertyName("permissionName")] public string PermissionName { get; set; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// The owner-facing permission onboarding state. Nothing here is granted
/// automatically: the owner reviews and applies, so an existing business never
/// receives new staff authority silently.
/// </summary>
public class StaffPermissionOnboardingResponse
{
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("hasGaps")] public bool HasGaps { get; set; }
    [JsonPropertyName("capabilities")] public List<StaffCapabilityResponse> Capabilities { get; set; } = [];
    [JsonPropertyName("roles")] public List<StaffRoleOnboardingResponse> Roles { get; set; } = [];
    [JsonPropertyName("recommendations")] public List<StaffPermissionRecommendationResponse> Recommendations { get; set; } = [];
}

public class StaffPermissionGrantRequest
{
    [JsonPropertyName("roleId")] public Guid RoleId { get; set; }
    [JsonPropertyName("permissionCodes")] public List<string> PermissionCodes { get; set; } = [];
}

public class ApplyStaffPermissionOnboardingRequest
{
    [JsonPropertyName("grants")] public List<StaffPermissionGrantRequest> Grants { get; set; } = [];
}
