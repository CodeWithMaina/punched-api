using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Business role and permission administration.
///
/// <para>Enforcement lives in <c>IBusinessPermissionService</c>; this service is
/// the <b>management</b> surface. It deliberately re-checks every rule the
/// security model depends on, so a caller cannot reach it by knowing an id:</para>
/// <list type="bullet">
/// <item>only a holder of <c>roles.manage</c> may create, edit or delete a role;</item>
/// <item>the owner role can never be edited, deactivated or deleted;</item>
/// <item>a role must belong to the caller's business to be read or assigned;</item>
/// <item>a caller may only GRANT permissions they themselves hold (no escalation);</item>
/// <item>the owner role can never be assigned to a non-owner membership.</item>
/// </list>
/// </summary>
public interface IBusinessRoleService
{
    /// <summary>The system permission catalogue, grouped by module.</summary>
    Task<ApiResponse<PermissionCatalogueResponse>> GetPermissionCatalogueAsync(Guid callerUserId, string role);

    /// <summary>All roles of the caller's business, each with its grants and holder count.</summary>
    Task<ApiResponse<List<BusinessRoleResponse>>> GetRolesAsync(Guid callerUserId, string role, bool includeInactive = true);

    /// <summary>A single role of the caller's business.</summary>
    Task<ApiResponse<BusinessRoleResponse>> GetRoleAsync(Guid callerUserId, string role, Guid roleId);

    /// <summary>Creates a custom role. Unknown or unheld permission codes are rejected.</summary>
    Task<ApiResponse<BusinessRoleResponse>> CreateRoleAsync(Guid callerUserId, string role, CreateBusinessRoleRequest request);

    /// <summary>Partially updates a role. The owner role is immutable.</summary>
    Task<ApiResponse<BusinessRoleResponse>> UpdateRoleAsync(Guid callerUserId, string role, Guid roleId, UpdateBusinessRoleRequest request);

    /// <summary>Replaces a role permission set wholesale.</summary>
    Task<ApiResponse<BusinessRoleResponse>> SetRolePermissionsAsync(Guid callerUserId, string role, Guid roleId, SetRolePermissionsRequest request);

    /// <summary>Grants one permission to a role.</summary>
    Task<ApiResponse<BusinessRoleResponse>> AddRolePermissionAsync(Guid callerUserId, string role, Guid roleId, RolePermissionChangeRequest request);

    /// <summary>Removes one permission from a role.</summary>
    Task<ApiResponse<BusinessRoleResponse>> RemoveRolePermissionAsync(Guid callerUserId, string role, Guid roleId, string permissionCode);

    /// <summary>
    /// Deletes a custom role that no membership holds. Seeded template roles and
    /// the owner role cannot be deleted.
    /// </summary>
    Task<ApiResponse<bool>> DeleteRoleAsync(Guid callerUserId, string role, Guid roleId);

    /// <summary>The roles one member of the caller's business holds, plus the effective union.</summary>
    Task<ApiResponse<MembershipRolesResponse>> GetMembershipRolesAsync(Guid callerUserId, string role, Guid staffUserId);

    /// <summary>Assigns one role to one member. Idempotent.</summary>
    Task<ApiResponse<MembershipRolesResponse>> AssignRoleAsync(Guid callerUserId, string role, Guid staffUserId, AssignMembershipRoleRequest request);

    /// <summary>Removes one role from one member. The owner role can never be removed.</summary>
    Task<ApiResponse<MembershipRolesResponse>> RemoveRoleAsync(Guid callerUserId, string role, Guid staffUserId, Guid roleId);
}
