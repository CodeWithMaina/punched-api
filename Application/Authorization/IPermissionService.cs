using System.Security.Claims;

namespace PunchedApi.Application.Authorization;

/// <summary>
/// Fine-grained permission checks. Complements the role-based
/// <c>[Authorize]</c> policies: entitlement decides whether a module is
/// available, membership decides the coarse persona, and permissions decide
/// what may be done inside the business.
///
/// <para><b>Authority.</b> For business operations the authoritative check is
/// <see cref="HasBusinessPermissionAsync"/> — it resolves the caller's ACTIVE
/// membership of the named business and unions the permissions of the roles
/// assigned to it. <see cref="HasPermission"/> remains for platform/persona
/// checks (and for the legacy staff-creation guard) and reads the static
/// <see cref="PermissionMatrix"/>, which is derived from the same
/// <c>ModuleCatalog</c> the RBAC catalogue is built from.</para>
/// </summary>
public interface IPermissionService
{
    /// <summary>Does the given persona hold the given permission code (static matrix)?</summary>
    bool HasPermission(string role, string permissionCode);

    /// <summary>
    /// Does the authenticated principal hold the permission in the business it
    /// is acting for? Falls back to the persona matrix when no business context
    /// can be resolved.
    /// </summary>
    Task<bool> CanAsync(ClaimsPrincipal user, string permissionCode);

    /// <summary>
    /// <b>Authoritative tenant check:</b> does this identity hold the permission
    /// in this business, based on its active membership and assigned roles?
    /// </summary>
    Task<bool> HasBusinessPermissionAsync(Guid userId, Guid businessId, string permissionCode);
}

/// <inheritdoc />
public class PermissionService : IPermissionService
{
    private readonly IBusinessPermissionService? _businessPermissions;

    /// <summary>
    /// The optional dependency keeps the legacy parameterless construction used
    /// by persona-only call sites and tests working unchanged.
    /// </summary>
    public PermissionService(IBusinessPermissionService? businessPermissions = null)
    {
        _businessPermissions = businessPermissions;
    }

    // The role claim type matches JwtTokenService (ClaimTypes.Role).
    public bool HasPermission(string role, string permissionCode) =>
        PermissionMatrix.HasPermission(role, permissionCode);

    public async Task<bool> CanAsync(ClaimsPrincipal user, string permissionCode)
    {
        var role = user.FindFirstValue(ClaimTypes.Role)
                   ?? user.FindFirstValue("role");

        var rawUserId = user.FindFirst("userId")?.Value
                        ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (_businessPermissions != null &&
            Guid.TryParse(rawUserId, out var userId) &&
            userId != Guid.Empty)
        {
            var businessId = await _businessPermissions.ResolveCallerBusinessIdAsync(userId);
            if (businessId.HasValue)
                return await _businessPermissions.HasPermissionAsync(userId, businessId.Value, permissionCode);
        }

        return role != null && HasPermission(role, permissionCode);
    }

    public Task<bool> HasBusinessPermissionAsync(Guid userId, Guid businessId, string permissionCode)
    {
        if (_businessPermissions == null)
            return Task.FromResult(false);

        return _businessPermissions.HasPermissionAsync(userId, businessId, permissionCode);
    }
}
