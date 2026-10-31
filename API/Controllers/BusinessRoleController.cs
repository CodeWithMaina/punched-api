using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Filters;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Business role and permission administration. Base route: /v1/businesses/me/roles
///
/// <para>Enforcement of these permissions lives in
/// <c>BusinessPermissionService</c>; this controller is the MANAGEMENT surface.
/// It is gated on <c>roles.view</c> / <c>roles.manage</c> and every rule the
/// security model depends on is re-checked in the service (owner-role
/// immutability, cross-business prevention, escalation prevention).</para>
///
/// <para>The permission catalogue endpoint is the only place a client should
/// read valid permission codes from — the catalogue is code-owned and derived
/// from the module catalog, so it can never advertise an unbacked permission.</para>
/// </summary>
[ApiController]
[Route("v1/businesses/me/roles")]
[Produces("application/json")]
[Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
[RequireModule("settings")]
[EnableRateLimiting("general")]
public partial class BusinessRoleController : ControllerBase
{
    private readonly IBusinessRoleService _roles;

    public BusinessRoleController(IBusinessRoleService roles)
    {
        _roles = roles;
    }

    /// <summary>The system permission catalogue, grouped by module.</summary>
    [HttpGet("permissions")]
    [RequirePermission("roles.view")]
    [ProducesResponseType(typeof(ApiResponse<PermissionCatalogueResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPermissionCatalogue()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _roles.GetPermissionCatalogueAsync(userId.Value, GetTenantRole());
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>All roles of the caller business, each with grants and holder count.</summary>
    [HttpGet]
    [RequirePermission("roles.view")]
    [ProducesResponseType(typeof(ApiResponse<List<BusinessRoleResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRoles([FromQuery] bool includeInactive = true)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _roles.GetRolesAsync(userId.Value, GetTenantRole(), includeInactive);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>A single role of the caller business.</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission("roles.view")]
    [ProducesResponseType(typeof(ApiResponse<BusinessRoleResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRole(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _roles.GetRoleAsync(userId.Value, GetTenantRole(), id);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Creates a custom role. Unknown permission codes are rejected
    /// (UNKNOWN_PERMISSION) and codes the caller does not hold are rejected
    /// (PERMISSION_ESCALATION).
    /// </summary>
    [HttpPost]
    [RequirePermission("roles.manage")]
    [ProducesResponseType(typeof(ApiResponse<BusinessRoleResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateRole([FromBody] CreateBusinessRoleRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _roles.CreateRoleAsync(userId.Value, GetTenantRole(), request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Partially updates a role. The owner role is immutable.</summary>
    [HttpPatch("{id:guid}")]
    [RequirePermission("roles.manage")]
    [ProducesResponseType(typeof(ApiResponse<BusinessRoleResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateRole(Guid id, [FromBody] UpdateBusinessRoleRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _roles.UpdateRoleAsync(userId.Value, GetTenantRole(), id, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Deletes a custom role nobody holds. Default template roles and the owner
    /// role cannot be deleted; deactivate a template instead.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission("roles.manage")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteRole(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _roles.DeleteRoleAsync(userId.Value, GetTenantRole(), id);
        return result.Success ? Ok(result) : MapFailure(result);
    }
}

/// <summary>Role permission grants.</summary>
public partial class BusinessRoleController
{
    /// <summary>Replaces a role permission set wholesale (the editor save action).</summary>
    [HttpPut("{id:guid}/permissions")]
    [RequirePermission("roles.manage")]
    [ProducesResponseType(typeof(ApiResponse<BusinessRoleResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetRolePermissions(Guid id, [FromBody] SetRolePermissionsRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _roles.SetRolePermissionsAsync(userId.Value, GetTenantRole(), id, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Grants one permission to a role. Idempotent.</summary>
    [HttpPost("{id:guid}/permissions")]
    [RequirePermission("roles.manage")]
    [ProducesResponseType(typeof(ApiResponse<BusinessRoleResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddRolePermission(Guid id, [FromBody] RolePermissionChangeRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _roles.AddRolePermissionAsync(userId.Value, GetTenantRole(), id, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Removes one permission from a role. Idempotent.</summary>
    [HttpDelete("{id:guid}/permissions/{permissionCode}")]
    [RequirePermission("roles.manage")]
    [ProducesResponseType(typeof(ApiResponse<BusinessRoleResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveRolePermission(Guid id, string permissionCode)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _roles.RemoveRolePermissionAsync(userId.Value, GetTenantRole(), id, permissionCode);
        return result.Success ? Ok(result) : MapFailure(result);
    }
}

/// <summary>Membership role assignment and shared helpers.</summary>
public partial class BusinessRoleController
{
    /// <summary>The roles one member of this business holds, plus the effective union.</summary>
    [HttpGet("~/v1/businesses/me/staff/{staffUserId:guid}/roles")]
    [RequirePermission("roles.view")]
    [ProducesResponseType(typeof(ApiResponse<MembershipRolesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMembershipRoles(Guid staffUserId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _roles.GetMembershipRolesAsync(userId.Value, GetTenantRole(), staffUserId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Assigns one role to one member. The role must belong to this business,
    /// the owner role can only go to the owner membership, and the caller must
    /// already hold every permission the role grants.
    /// </summary>
    [HttpPost("~/v1/businesses/me/staff/{staffUserId:guid}/roles")]
    [RequirePermission("roles.manage")]
    [ProducesResponseType(typeof(ApiResponse<MembershipRolesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignRole(Guid staffUserId, [FromBody] AssignMembershipRoleRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _roles.AssignRoleAsync(userId.Value, GetTenantRole(), staffUserId, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Removes one role from one member. The owner role is never removable.</summary>
    [HttpDelete("~/v1/businesses/me/staff/{staffUserId:guid}/roles/{roleId:guid}")]
    [RequirePermission("roles.manage")]
    [ProducesResponseType(typeof(ApiResponse<MembershipRolesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveRole(Guid staffUserId, Guid roleId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _roles.RemoveRoleAsync(userId.Value, GetTenantRole(), staffUserId, roleId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private string GetTenantRole()
    {
        var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
                   ?? User.FindFirst("role")?.Value;

        return string.Equals(role?.Trim(), "Business", StringComparison.OrdinalIgnoreCase)
            ? "Business"
            : "Staff";
    }

    /// <summary>
    /// Maps an ApiResponse failure onto the platform HTTP status convention.
    /// Escalation and immutability refusals are 403 (authorization), conflicts
    /// are 409, unknown permissions are 400.
    /// </summary>
    private IActionResult MapFailure<T>(ApiResponse<T> result)
        => result.Error?.Code switch
        {
            "NOT_FOUND" or "STAFF_NOT_FOUND" or "ROLE_NOT_FOUND" => NotFound(result),
            "FORBIDDEN" or "PERMISSION_ESCALATION" or "OWNER_ROLE_IMMUTABLE"
                or "MODULE_DISABLED" => StatusCode(StatusCodes.Status403Forbidden, result),
            "ROLE_NAME_EXISTS" or "ROLE_KEY_EXISTS" or "ROLE_IN_USE"
                or "ROLE_SYSTEM_PROTECTED" or "ROLE_LIMIT_REACHED" => Conflict(result),
            _ => BadRequest(result)
        };
}
