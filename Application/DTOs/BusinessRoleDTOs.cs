using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ===============================================================
//  RBAC - PERMISSION CATALOGUE
// ===============================================================

/// <summary>One system permission, as exposed to the role editor.</summary>
public class PermissionResponse
{
    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>Owning module key, used to group the catalogue in the UI.</summary>
    [JsonPropertyName("moduleKey")] public string ModuleKey { get; set; } = string.Empty;

    [JsonPropertyName("moduleName")] public string ModuleName { get; set; } = string.Empty;
}

/// <summary>The whole permission catalogue grouped by module.</summary>
public class PermissionCatalogueResponse
{
    [JsonPropertyName("permissions")] public List<PermissionResponse> Permissions { get; set; } = new();

    [JsonPropertyName("byModule")] public List<PermissionModuleGroupResponse> ByModule { get; set; } = new();
}

/// <summary>Permissions belonging to one module.</summary>
public class PermissionModuleGroupResponse
{
    [JsonPropertyName("moduleKey")] public string ModuleKey { get; set; } = string.Empty;
    [JsonPropertyName("moduleName")] public string ModuleName { get; set; } = string.Empty;
    [JsonPropertyName("permissions")] public List<PermissionResponse> Permissions { get; set; } = new();
}

// ===============================================================
//  RBAC - BUSINESS ROLES
// ===============================================================

/// <summary>A business-scoped role and its granted permissions.</summary>
public class BusinessRoleResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>
    /// True only for the single owner role, which implicitly holds EVERY
    /// permission and cannot be edited, deactivated or deleted.
    /// </summary>
    [JsonPropertyName("isOwnerRole")] public bool IsOwnerRole { get; set; }

    /// <summary>True for platform-seeded templates (editable, but not the owner role).</summary>
    [JsonPropertyName("isSystem")] public bool IsSystem { get; set; }

    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
    [JsonPropertyName("sortOrder")] public int SortOrder { get; set; }

    /// <summary>Explicit permission grants. Empty for the owner role (which holds all).</summary>
    [JsonPropertyName("permissions")] public List<string> Permissions { get; set; } = new();

    /// <summary>How many memberships currently hold this role.</summary>
    [JsonPropertyName("memberCount")] public int MemberCount { get; set; }

    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }
}

/// <summary>Creates a custom role.</summary>
public class CreateBusinessRoleRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>Optional stable key; derived from the name when omitted.</summary>
    [JsonPropertyName("key")] public string? Key { get; set; }

    /// <summary>Initial grants. Every code must exist in the catalogue and be held by the caller.</summary>
    [JsonPropertyName("permissions")] public string[]? Permissions { get; set; }

    [JsonPropertyName("sortOrder")] public int? SortOrder { get; set; }
}

/// <summary>Partially updates a role.</summary>
public class UpdateBusinessRoleRequest
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("isActive")] public bool? IsActive { get; set; }
    [JsonPropertyName("sortOrder")] public int? SortOrder { get; set; }
}

/// <summary>Replaces a role permission set wholesale (the editor save action).</summary>
public class SetRolePermissionsRequest
{
    [JsonPropertyName("permissions")] public string[] Permissions { get; set; } = [];
}

/// <summary>One permission grant or removal.</summary>
public class RolePermissionChangeRequest
{
    [JsonPropertyName("permissionCode")] public string PermissionCode { get; set; } = string.Empty;
}

/// <summary>Assigns or removes one role for one membership.</summary>
public class AssignMembershipRoleRequest
{
    [JsonPropertyName("roleId")] public Guid RoleId { get; set; }
}

/// <summary>A role a membership holds.</summary>
public class MembershipRoleResponse
{
    [JsonPropertyName("roleId")] public Guid RoleId { get; set; }
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("isOwnerRole")] public bool IsOwnerRole { get; set; }
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
    [JsonPropertyName("assignedAt")] public DateTime AssignedAt { get; set; }
    [JsonPropertyName("assignedByUserId")] public Guid? AssignedByUserId { get; set; }
}

/// <summary>A membership roles plus the effective permission union.</summary>
public class MembershipRolesResponse
{
    [JsonPropertyName("membershipId")] public Guid MembershipId { get; set; }
    [JsonPropertyName("userId")] public Guid UserId { get; set; }
    [JsonPropertyName("fullName")] public string? FullName { get; set; }

    /// <summary>The coarse persona (Owner | Staff | Customer) - never a grant.</summary>
    [JsonPropertyName("membershipRole")] public string MembershipRole { get; set; } = string.Empty;

    [JsonPropertyName("roles")] public List<MembershipRoleResponse> Roles { get; set; } = new();

    /// <summary>The union of the assigned roles permissions (all codes for an owner).</summary>
    [JsonPropertyName("effectivePermissions")] public List<string> EffectivePermissions { get; set; } = new();
}
