using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Modules;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Business role and permission administration.
///
/// <para>This is the MANAGEMENT half of RBAC; enforcement lives in
/// <see cref="IBusinessPermissionService"/>. Every rule the security model
/// depends on is re-checked here, because a controller filter alone is not a
/// sufficient guard for authority-granting operations.</para>
///
/// <para><b>Escalation is impossible by construction:</b> a caller may only
/// grant permission codes it already holds, and may only assign a role whose
/// grants it already holds. The owner role is immutable and unassignable to a
/// non-owner membership. Any role or permission write invalidates the affected
/// business permission cache, so the change is live on the next request.</para>
/// </summary>
public partial class BusinessRoleService : IBusinessRoleService
{
    private const int MaxNameLength = 80;
    private const int MaxDescriptionLength = 300;
    private const int MaxKeyLength = 40;
    private const int MaxCustomRolesPerBusiness = 50;

    private readonly ApplicationDbContext _context;
    private readonly IBusinessScopeResolver _scopeResolver;
    private readonly IBusinessPermissionService _businessPermissions;
    private readonly ILogger<BusinessRoleService> _logger;

    /// <summary>Active tenant (null on the platform root / in unit tests) - scoping only, never a grant.</summary>
    private readonly ITenantContext? _tenant;

    public BusinessRoleService(
        ApplicationDbContext context,
        IBusinessScopeResolver scopeResolver,
        IBusinessPermissionService businessPermissions,
        ILogger<BusinessRoleService> logger,
        ITenantContext? tenant = null)
    {
        _context = context;
        _scopeResolver = scopeResolver;
        _businessPermissions = businessPermissions;
        _logger = logger;
        _tenant = tenant;
    }

    private Guid? TenantBusinessId => _tenant?.IsActive == true ? _tenant.BusinessId : null;

    private static bool IsRole(string value, string expected) =>
        string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private async Task<Guid?> ResolveCallerBusinessIdAsync(Guid callerUserId, string role)
    {
        if (TenantBusinessId is Guid tenantId) return tenantId;
        if (callerUserId == Guid.Empty) return null;

        if (IsRole(role, "Business"))
        {
            var owned = await _scopeResolver.GetOwnedBusinessIdAsync(callerUserId);
            if (owned.HasValue) return owned;
        }

        return await _scopeResolver.GetMemberBusinessIdAsync(callerUserId);
    }

    /// <summary>The caller effective permissions in the business (all codes for an owner).</summary>
    private Task<IReadOnlySet<string>> CallerPermissionsAsync(Guid callerUserId, Guid businessId) =>
        _businessPermissions.GetEffectivePermissionsAsync(callerUserId, businessId);

    private async Task<bool> HasPermissionAsync(Guid userId, Guid businessId, string permissionCode) =>
        await _businessPermissions.HasPermissionAsync(userId, businessId, permissionCode);

    /// <summary>Derives a stable, url-safe role key from a display name.</summary>
    private static string DeriveKey(string name)
    {
        var buffer = new char[Math.Min(name.Length, MaxKeyLength)];
        var length = 0;
        var lastWasSeparator = true;

        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (length >= MaxKeyLength) break;

            if (char.IsLetterOrDigit(c))
            {
                buffer[length++] = c;
                lastWasSeparator = false;
                continue;
            }

            if (!lastWasSeparator && length < MaxKeyLength)
            {
                buffer[length++] = '_';
                lastWasSeparator = true;
            }
        }

        // Trim a trailing separator introduced by the loop.
        while (length > 0 && buffer[length - 1] == '_') length--;

        return length == 0 ? "role" : new string(buffer, 0, length);
    }
}

/// <summary>Permission catalogue and role reads.</summary>
public partial class BusinessRoleService
{
    /// <inheritdoc />
    public async Task<ApiResponse<PermissionCatalogueResponse>> GetPermissionCatalogueAsync(
        Guid callerUserId, string role)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<PermissionCatalogueResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "roles.view"))
            return ApiResponse<PermissionCatalogueResponse>.Fail("FORBIDDEN", "You are not allowed to view roles and permissions.");

        // The catalogue is CODE-owned (PermissionCatalog derives it from
        // ModuleCatalog), so the API can never advertise a permission that has no
        // backend operation behind it.
        var permissions = PermissionCatalog.All.Select(p => new PermissionResponse
        {
            Code = p.Code,
            Name = p.Name,
            Description = p.Description,
            ModuleKey = p.ModuleKey,
            ModuleName = ModuleCatalog.Find(p.ModuleKey)?.Name ?? p.ModuleKey
        }).ToList();

        return ApiResponse<PermissionCatalogueResponse>.Ok(new PermissionCatalogueResponse
        {
            Permissions = permissions,
            ByModule = permissions
                .GroupBy(p => new { p.ModuleKey, p.ModuleName })
                .Select(g => new PermissionModuleGroupResponse
                {
                    ModuleKey = g.Key.ModuleKey,
                    ModuleName = g.Key.ModuleName,
                    Permissions = g.OrderBy(p => p.Code).ToList()
                })
                .OrderBy(g => g.ModuleName)
                .ToList()
        });
    }

    /// <inheritdoc />
    public async Task<ApiResponse<List<BusinessRoleResponse>>> GetRolesAsync(
        Guid callerUserId, string role, bool includeInactive = true)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<List<BusinessRoleResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "roles.view"))
            return ApiResponse<List<BusinessRoleResponse>>.Fail("FORBIDDEN", "You are not allowed to view roles.");

        // Reading roles is also the point at which an un-upgraded business gets
        // its default templates, so the editor always shows a usable baseline.
        await _businessPermissions.EnsureProvisionedAsync(businessId.Value);

        var query = _context.BusinessRoles.AsNoTracking().Where(r => r.BusinessId == businessId.Value);
        if (!includeInactive) query = query.Where(r => r.IsActive);

        var roles = await query.OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToListAsync();
        return ApiResponse<List<BusinessRoleResponse>>.Ok(await MapManyAsync(roles, businessId.Value));
    }

    /// <inheritdoc />
    public async Task<ApiResponse<BusinessRoleResponse>> GetRoleAsync(
        Guid callerUserId, string role, Guid roleId)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<BusinessRoleResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "roles.view"))
            return ApiResponse<BusinessRoleResponse>.Fail("FORBIDDEN", "You are not allowed to view roles.");

        var businessRole = await _context.BusinessRoles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == roleId && r.BusinessId == businessId.Value);
        if (businessRole == null)
            return ApiResponse<BusinessRoleResponse>.Fail("NOT_FOUND", "Role not found.");

        var mapped = await MapManyAsync(new List<BusinessRole> { businessRole }, businessId.Value);
        return ApiResponse<BusinessRoleResponse>.Ok(mapped[0]);
    }
}

/// <summary>Custom role creation.</summary>
public partial class BusinessRoleService
{
    /// <inheritdoc />
    public async Task<ApiResponse<BusinessRoleResponse>> CreateRoleAsync(
        Guid callerUserId, string role, CreateBusinessRoleRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<BusinessRoleResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "roles.manage"))
            return ApiResponse<BusinessRoleResponse>.Fail("FORBIDDEN", "You are not allowed to manage roles.");

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            return ApiResponse<BusinessRoleResponse>.Fail("VALIDATION_ERROR", "A role name is required.");
        if (name.Length > MaxNameLength)
            return ApiResponse<BusinessRoleResponse>.Fail("VALIDATION_ERROR", $"A role name may be at most {MaxNameLength} characters.");

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description != null && description.Length > MaxDescriptionLength)
            return ApiResponse<BusinessRoleResponse>.Fail("VALIDATION_ERROR", $"A description may be at most {MaxDescriptionLength} characters.");

        var requested = (request.Permissions ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Unknown codes fail closed: the catalogue is the only source of truth.
        var unknown = requested.Where(code => !PermissionCatalog.Exists(code)).ToList();
        if (unknown.Count > 0)
            return ApiResponse<BusinessRoleResponse>.Fail(
                "UNKNOWN_PERMISSION", $"Unknown permission code(s): {string.Join(", ", unknown)}.");

        // Privilege escalation guard: a caller may only hand out authority it
        // already holds. The owner holds every code, so this is a no-op for them.
        var callerPermissions = await CallerPermissionsAsync(callerUserId, businessId.Value);
        var notHeld = requested.Where(code => !callerPermissions.Contains(code)).ToList();
        if (notHeld.Count > 0)
            return ApiResponse<BusinessRoleResponse>.Fail(
                "PERMISSION_ESCALATION",
                $"You cannot grant permission(s) you do not hold: {string.Join(", ", notHeld)}.");

        var key = string.IsNullOrWhiteSpace(request.Key) ? DeriveKey(name) : DeriveKey(request.Key!);

        var existingRoles = await _context.BusinessRoles
            .Where(r => r.BusinessId == businessId.Value)
            .Select(r => new { r.Key, r.Name, r.IsSystem, r.SortOrder })
            .ToListAsync();

        if (existingRoles.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            return ApiResponse<BusinessRoleResponse>.Fail(
                "ROLE_NAME_EXISTS", "A role with this name already exists.");

        if (existingRoles.Any(r => string.Equals(r.Key, key, StringComparison.Ordinal)))
            return ApiResponse<BusinessRoleResponse>.Fail(
                "ROLE_KEY_EXISTS", "A role with this key already exists. Provide a different key or name.");

        if (existingRoles.Count(r => !r.IsSystem) >= MaxCustomRolesPerBusiness)
            return ApiResponse<BusinessRoleResponse>.Fail(
                "ROLE_LIMIT_REACHED", $"A business may have at most {MaxCustomRolesPerBusiness} custom roles.");

        var now = DateTime.UtcNow;
        var businessRole = new BusinessRole
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId.Value,
            Key = key,
            Name = name,
            Description = description,
            // A custom role is never the owner role: that flag is reserved for
            // the single seeded owner template.
            IsOwnerRole = false,
            IsSystem = false,
            IsActive = true,
            SortOrder = request.SortOrder ?? (existingRoles.Count == 0 ? 0 : existingRoles.Max(r => r.SortOrder) + 1),
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.BusinessRoles.Add(businessRole);

        foreach (var code in requested)
        {
            _context.BusinessRolePermissions.Add(new BusinessRolePermission
            {
                Id = Guid.NewGuid(),
                BusinessRoleId = businessRole.Id,
                PermissionCode = code,
                CreatedAt = now
            });
        }

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            foreach (var entry in _context.ChangeTracker.Entries()
                         .Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;

            return ApiResponse<BusinessRoleResponse>.Fail(
                "ROLE_NAME_EXISTS", "A role with this name or key already exists.");
        }

        // A new role must be usable immediately: drop the memoized permission set
        // for this business so the next check sees the new authority.
        _businessPermissions.Invalidate(businessId.Value);

        _logger.LogInformation(
            "Custom role {RoleId} ({RoleKey}) created for business {BusinessId} by {UserId} with {Count} permission(s).",
            businessRole.Id, businessRole.Key, businessId.Value, callerUserId, requested.Count);

        var mapped = await MapManyAsync(new List<BusinessRole> { businessRole }, businessId.Value);
        return ApiResponse<BusinessRoleResponse>.Ok(mapped[0]);
    }
}

/// <summary>Role updates, permission grants and deletion.</summary>
public partial class BusinessRoleService
{
    /// <inheritdoc />
    public async Task<ApiResponse<BusinessRoleResponse>> UpdateRoleAsync(
        Guid callerUserId, string role, Guid roleId, UpdateBusinessRoleRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<BusinessRoleResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "roles.manage"))
            return ApiResponse<BusinessRoleResponse>.Fail("FORBIDDEN", "You are not allowed to manage roles.");

        var businessRole = await _context.BusinessRoles
            .FirstOrDefaultAsync(r => r.Id == roleId && r.BusinessId == businessId.Value);
        if (businessRole == null)
            return ApiResponse<BusinessRoleResponse>.Fail("NOT_FOUND", "Role not found.");

        // The owner role is immutable: the business owner always keeps full
        // authority over their own business.
        if (businessRole.IsOwnerRole)
            return ApiResponse<BusinessRoleResponse>.Fail(
                "OWNER_ROLE_IMMUTABLE", "The owner role cannot be changed. It always holds every permission.");

        if (request.Name != null)
        {
            var name = request.Name.Trim();
            if (name.Length == 0)
                return ApiResponse<BusinessRoleResponse>.Fail("VALIDATION_ERROR", "A role name cannot be blank.");
            if (name.Length > MaxNameLength)
                return ApiResponse<BusinessRoleResponse>.Fail("VALIDATION_ERROR", $"A role name may be at most {MaxNameLength} characters.");

            if (!string.Equals(name, businessRole.Name, StringComparison.Ordinal))
            {
                var clash = await _context.BusinessRoles.AsNoTracking().AnyAsync(r =>
                    r.BusinessId == businessId.Value && r.Id != businessRole.Id && r.Name.ToLower() == name.ToLower());
                if (clash)
                    return ApiResponse<BusinessRoleResponse>.Fail(
                        "ROLE_NAME_EXISTS", "A role with this name already exists.");

                businessRole.Name = name;
            }
        }

        if (request.Description != null)
        {
            var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            if (description != null && description.Length > MaxDescriptionLength)
                return ApiResponse<BusinessRoleResponse>.Fail("VALIDATION_ERROR", $"A description may be at most {MaxDescriptionLength} characters.");
            businessRole.Description = description;
        }

        // A deactivated role stops granting its permissions immediately (the
        // resolver only joins ACTIVE roles) without losing its grants or
        // assignments, so reactivating restores the exact prior authority.
        if (request.IsActive.HasValue) businessRole.IsActive = request.IsActive.Value;
        if (request.SortOrder.HasValue) businessRole.SortOrder = request.SortOrder.Value;

        businessRole.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return ApiResponse<BusinessRoleResponse>.Fail(
                "ROLE_NAME_EXISTS", "A role with this name already exists.");
        }

        _businessPermissions.Invalidate(businessId.Value);

        _logger.LogInformation(
            "Role {RoleId} updated for business {BusinessId} by {UserId} (active: {IsActive}).",
            businessRole.Id, businessId.Value, callerUserId, businessRole.IsActive);

        var mapped = await MapManyAsync(new List<BusinessRole> { businessRole }, businessId.Value);
        return ApiResponse<BusinessRoleResponse>.Ok(mapped[0]);
    }
}

/// <summary>Role permission grant/revoke and role deletion.</summary>
public partial class BusinessRoleService
{
    /// <inheritdoc />
    public async Task<ApiResponse<BusinessRoleResponse>> SetRolePermissionsAsync(
        Guid callerUserId, string role, Guid roleId, SetRolePermissionsRequest request)
    {
        var requested = (request.Permissions ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return await ReplacePermissionsAsync(callerUserId, role, roleId, requested);
    }

    /// <inheritdoc />
    public async Task<ApiResponse<BusinessRoleResponse>> AddRolePermissionAsync(
        Guid callerUserId, string role, Guid roleId, RolePermissionChangeRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<BusinessRoleResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "roles.manage"))
            return ApiResponse<BusinessRoleResponse>.Fail("FORBIDDEN", "You are not allowed to manage roles.");

        var code = (request.PermissionCode ?? string.Empty).Trim();
        if (!PermissionCatalog.Exists(code))
            return ApiResponse<BusinessRoleResponse>.Fail("UNKNOWN_PERMISSION", $"Unknown permission code '{code}'.");

        var businessRole = await _context.BusinessRoles
            .FirstOrDefaultAsync(r => r.Id == roleId && r.BusinessId == businessId.Value);
        if (businessRole == null)
            return ApiResponse<BusinessRoleResponse>.Fail("NOT_FOUND", "Role not found.");
        if (businessRole.IsOwnerRole)
            return ApiResponse<BusinessRoleResponse>.Fail(
                "OWNER_ROLE_IMMUTABLE", "The owner role always holds every permission; it cannot be edited.");

        var callerPermissions = await CallerPermissionsAsync(callerUserId, businessId.Value);
        if (!callerPermissions.Contains(code))
            return ApiResponse<BusinessRoleResponse>.Fail(
                "PERMISSION_ESCALATION", $"You cannot grant a permission you do not hold ({code}).");

        var alreadyGranted = await _context.BusinessRolePermissions
            .AnyAsync(p => p.BusinessRoleId == roleId && p.PermissionCode == code);

        if (!alreadyGranted)
        {
            _context.BusinessRolePermissions.Add(new BusinessRolePermission
            {
                Id = Guid.NewGuid(),
                BusinessRoleId = roleId,
                PermissionCode = code,
                CreatedAt = DateTime.UtcNow
            });
            businessRole.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            _businessPermissions.Invalidate(businessId.Value);

            _logger.LogInformation(
                "Permission {PermissionCode} granted to role {RoleId} in business {BusinessId} by {UserId}.",
                code, roleId, businessId.Value, callerUserId);
        }

        var mapped = await MapManyAsync(new List<BusinessRole> { businessRole }, businessId.Value);
        return ApiResponse<BusinessRoleResponse>.Ok(mapped[0]);
    }

    /// <inheritdoc />
    public async Task<ApiResponse<BusinessRoleResponse>> RemoveRolePermissionAsync(
        Guid callerUserId, string role, Guid roleId, string permissionCode)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<BusinessRoleResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "roles.manage"))
            return ApiResponse<BusinessRoleResponse>.Fail("FORBIDDEN", "You are not allowed to manage roles.");

        var businessRole = await _context.BusinessRoles
            .FirstOrDefaultAsync(r => r.Id == roleId && r.BusinessId == businessId.Value);
        if (businessRole == null)
            return ApiResponse<BusinessRoleResponse>.Fail("NOT_FOUND", "Role not found.");
        if (businessRole.IsOwnerRole)
            return ApiResponse<BusinessRoleResponse>.Fail(
                "OWNER_ROLE_IMMUTABLE", "The owner role always holds every permission; it cannot be edited.");

        var grant = await _context.BusinessRolePermissions
            .FirstOrDefaultAsync(p => p.BusinessRoleId == roleId && p.PermissionCode == permissionCode);

        if (grant != null)
        {
            _context.BusinessRolePermissions.Remove(grant);
            businessRole.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            _businessPermissions.Invalidate(businessId.Value);

            _logger.LogInformation(
                "Permission {PermissionCode} revoked from role {RoleId} in business {BusinessId} by {UserId}.",
                permissionCode, roleId, businessId.Value, callerUserId);
        }

        var mapped = await MapManyAsync(new List<BusinessRole> { businessRole }, businessId.Value);
        return ApiResponse<BusinessRoleResponse>.Ok(mapped[0]);
    }
}

/// <summary>Whole-set permission replacement and role deletion.</summary>
public partial class BusinessRoleService
{
    /// <summary>
    /// Replaces a role permission set wholesale (the role editor save action).
    /// Unknown codes and codes the caller does not hold are rejected before any
    /// write, so a partially-applied escalation is impossible.
    /// </summary>
    private async Task<ApiResponse<BusinessRoleResponse>> ReplacePermissionsAsync(
        Guid callerUserId, string role, Guid roleId, List<string> requested)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<BusinessRoleResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "roles.manage"))
            return ApiResponse<BusinessRoleResponse>.Fail("FORBIDDEN", "You are not allowed to manage roles.");

        var businessRole = await _context.BusinessRoles
            .FirstOrDefaultAsync(r => r.Id == roleId && r.BusinessId == businessId.Value);
        if (businessRole == null)
            return ApiResponse<BusinessRoleResponse>.Fail("NOT_FOUND", "Role not found.");
        if (businessRole.IsOwnerRole)
            return ApiResponse<BusinessRoleResponse>.Fail(
                "OWNER_ROLE_IMMUTABLE", "The owner role always holds every permission; it cannot be edited.");

        var unknown = requested.Where(code => !PermissionCatalog.Exists(code)).ToList();
        if (unknown.Count > 0)
            return ApiResponse<BusinessRoleResponse>.Fail(
                "UNKNOWN_PERMISSION", $"Unknown permission code(s): {string.Join(", ", unknown)}.");

        var callerPermissions = await CallerPermissionsAsync(callerUserId, businessId.Value);
        var notHeld = requested.Where(code => !callerPermissions.Contains(code)).ToList();
        if (notHeld.Count > 0)
            return ApiResponse<BusinessRoleResponse>.Fail(
                "PERMISSION_ESCALATION",
                $"You cannot grant permission(s) you do not hold: {string.Join(", ", notHeld)}.");

        var existing = await _context.BusinessRolePermissions
            .Where(p => p.BusinessRoleId == roleId)
            .ToListAsync();

        var now = DateTime.UtcNow;
        var toAdd = requested.Where(code => existing.All(e => e.PermissionCode != code)).ToList();
        var toRemove = existing.Where(e => !requested.Contains(e.PermissionCode)).ToList();

        foreach (var grant in toRemove) _context.BusinessRolePermissions.Remove(grant);
        foreach (var code in toAdd)
        {
            _context.BusinessRolePermissions.Add(new BusinessRolePermission
            {
                Id = Guid.NewGuid(),
                BusinessRoleId = roleId,
                PermissionCode = code,
                CreatedAt = now
            });
        }

        if (toAdd.Count > 0 || toRemove.Count > 0)
        {
            businessRole.UpdatedAt = now;
            await _context.SaveChangesAsync();
            _businessPermissions.Invalidate(businessId.Value);

            _logger.LogInformation(
                "Role {RoleId} permissions replaced in business {BusinessId} by {UserId} (+{Added}/-{Removed}).",
                roleId, businessId.Value, callerUserId, toAdd.Count, toRemove.Count);
        }

        var mapped = await MapManyAsync(new List<BusinessRole> { businessRole }, businessId.Value);
        return ApiResponse<BusinessRoleResponse>.Ok(mapped[0]);
    }

    /// <inheritdoc />
    public async Task<ApiResponse<bool>> DeleteRoleAsync(Guid callerUserId, string role, Guid roleId)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "roles.manage"))
            return ApiResponse<bool>.Fail("FORBIDDEN", "You are not allowed to manage roles.");

        var businessRole = await _context.BusinessRoles
            .FirstOrDefaultAsync(r => r.Id == roleId && r.BusinessId == businessId.Value);
        if (businessRole == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "Role not found.");

        if (businessRole.IsOwnerRole)
            return ApiResponse<bool>.Fail(
                "OWNER_ROLE_IMMUTABLE", "The owner role cannot be deleted.");

        // Seeded templates are the platform's baseline; deactivating them is the
        // supported way to retire one without breaking existing assignments.
        if (businessRole.IsSystem)
            return ApiResponse<bool>.Fail(
                "ROLE_SYSTEM_PROTECTED", "A default role cannot be deleted. Deactivate it instead.");

        // A role somebody holds must be unassigned first: deleting it would leave
        // memberships pointing at a missing authority record.
        var holders = await _context.BusinessMembershipRoleAssignments.AsNoTracking()
            .CountAsync(a => a.BusinessRoleId == roleId);
        if (holders > 0)
            return ApiResponse<bool>.Fail(
                "ROLE_IN_USE", $"{holders} member(s) still hold this role. Unassign it first.");

        _context.BusinessRoles.Remove(businessRole);
        await _context.SaveChangesAsync();
        _businessPermissions.Invalidate(businessId.Value);

        _logger.LogInformation(
            "Custom role {RoleId} deleted in business {BusinessId} by {UserId}.",
            roleId, businessId.Value, callerUserId);

        return ApiResponse<bool>.Ok(true);
    }
}

/// <summary>Membership role assignment.</summary>
public partial class BusinessRoleService
{
    /// <inheritdoc />
    public async Task<ApiResponse<MembershipRolesResponse>> GetMembershipRolesAsync(
        Guid callerUserId, string role, Guid staffUserId)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<MembershipRolesResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "roles.view"))
            return ApiResponse<MembershipRolesResponse>.Fail("FORBIDDEN", "You are not allowed to view roles.");

        var membership = await _context.BusinessMemberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.BusinessId == businessId.Value && m.UserId == staffUserId);
        if (membership == null)
            return ApiResponse<MembershipRolesResponse>.Fail("STAFF_NOT_FOUND", "Member not found in this business.");

        return ApiResponse<MembershipRolesResponse>.Ok(
            await BuildMembershipRolesAsync(membership, businessId.Value));
    }

    /// <inheritdoc />
    public async Task<ApiResponse<MembershipRolesResponse>> AssignRoleAsync(
        Guid callerUserId, string role, Guid staffUserId, AssignMembershipRoleRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<MembershipRolesResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "roles.manage"))
            return ApiResponse<MembershipRolesResponse>.Fail("FORBIDDEN", "You are not allowed to assign roles.");

        var membership = await _context.BusinessMemberships
            .FirstOrDefaultAsync(m => m.BusinessId == businessId.Value && m.UserId == staffUserId);
        if (membership == null)
            return ApiResponse<MembershipRolesResponse>.Fail("STAFF_NOT_FOUND", "Member not found in this business.");

        // The role must belong to THIS business, so a cross-tenant role can never
        // be attached (IDOR guard).
        var businessRole = await _context.BusinessRoles
            .FirstOrDefaultAsync(r => r.Id == request.RoleId && r.BusinessId == businessId.Value);
        if (businessRole == null)
            return ApiResponse<MembershipRolesResponse>.Fail("NOT_FOUND", "Role not found in this business.");

        // The owner role is reserved for the owner membership. Assigning it to a
        // staff member would hand over the whole business.
        if (businessRole.IsOwnerRole && membership.Role != BusinessMembershipRole.Owner)
            return ApiResponse<MembershipRolesResponse>.Fail(
                "FORBIDDEN", "The owner role cannot be assigned to a non-owner membership.");

        // Escalation guard: a caller may only assign a role whose grants it holds.
        var callerPermissions = await CallerPermissionsAsync(callerUserId, businessId.Value);
        var roleCodes = await _context.BusinessRolePermissions.AsNoTracking()
            .Where(p => p.BusinessRoleId == businessRole.Id)
            .Select(p => p.PermissionCode)
            .ToListAsync();

        var notHeld = roleCodes.Where(code => !callerPermissions.Contains(code)).ToList();
        if (notHeld.Count > 0)
            return ApiResponse<MembershipRolesResponse>.Fail(
                "PERMISSION_ESCALATION",
                $"You cannot assign a role that grants permission(s) you do not hold: {string.Join(", ", notHeld)}.");

        var alreadyAssigned = await _context.BusinessMembershipRoleAssignments
            .AnyAsync(a => a.BusinessMembershipId == membership.Id && a.BusinessRoleId == businessRole.Id);

        if (!alreadyAssigned)
        {
            _context.BusinessMembershipRoleAssignments.Add(new BusinessMembershipRoleAssignment
            {
                Id = Guid.NewGuid(),
                BusinessMembershipId = membership.Id,
                BusinessRoleId = businessRole.Id,
                AssignedByUserId = callerUserId,
                CreatedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();
            _businessPermissions.Invalidate(businessId.Value);

            _logger.LogInformation(
                "Role {RoleId} assigned to user {StaffUserId} in business {BusinessId} by {UserId}.",
                businessRole.Id, staffUserId, businessId.Value, callerUserId);
        }

        return ApiResponse<MembershipRolesResponse>.Ok(
            await BuildMembershipRolesAsync(membership, businessId.Value));
    }

    /// <inheritdoc />
    public async Task<ApiResponse<MembershipRolesResponse>> RemoveRoleAsync(
        Guid callerUserId, string role, Guid staffUserId, Guid roleId)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<MembershipRolesResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "roles.manage"))
            return ApiResponse<MembershipRolesResponse>.Fail("FORBIDDEN", "You are not allowed to assign roles.");

        var membership = await _context.BusinessMemberships
            .FirstOrDefaultAsync(m => m.BusinessId == businessId.Value && m.UserId == staffUserId);
        if (membership == null)
            return ApiResponse<MembershipRolesResponse>.Fail("STAFF_NOT_FOUND", "Member not found in this business.");

        var businessRole = await _context.BusinessRoles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == roleId && r.BusinessId == businessId.Value);
        if (businessRole == null)
            return ApiResponse<MembershipRolesResponse>.Fail("NOT_FOUND", "Role not found in this business.");

        // The owner role is never removable: an owner always keeps full authority.
        if (businessRole.IsOwnerRole)
            return ApiResponse<MembershipRolesResponse>.Fail(
                "OWNER_ROLE_IMMUTABLE", "The owner role cannot be removed from the business owner.");

        var assignment = await _context.BusinessMembershipRoleAssignments
            .FirstOrDefaultAsync(a => a.BusinessMembershipId == membership.Id && a.BusinessRoleId == roleId);

        if (assignment != null)
        {
            _context.BusinessMembershipRoleAssignments.Remove(assignment);
            await _context.SaveChangesAsync();
            _businessPermissions.Invalidate(businessId.Value);

            _logger.LogInformation(
                "Role {RoleId} removed from user {StaffUserId} in business {BusinessId} by {UserId}.",
                roleId, staffUserId, businessId.Value, callerUserId);
        }

        return ApiResponse<MembershipRolesResponse>.Ok(
            await BuildMembershipRolesAsync(membership, businessId.Value));
    }
}

/// <summary>Mapping helpers.</summary>
public partial class BusinessRoleService
{
    private async Task<List<BusinessRoleResponse>> MapManyAsync(
        List<BusinessRole> roles, Guid businessId)
    {
        if (roles.Count == 0) return new List<BusinessRoleResponse>();

        var ids = roles.Select(r => r.Id).ToList();

        // Two batched queries instead of two per role (no N+1).
        var grants = await _context.BusinessRolePermissions.AsNoTracking()
            .Where(p => ids.Contains(p.BusinessRoleId))
            .Select(p => new { p.BusinessRoleId, p.PermissionCode })
            .ToListAsync();

        var counts = (await _context.BusinessMembershipRoleAssignments.AsNoTracking()
                .Where(a => ids.Contains(a.BusinessRoleId))
                .GroupBy(a => a.BusinessRoleId)
                .Select(g => new { RoleId = g.Key, Count = g.Count() })
                .ToListAsync())
            .ToDictionary(x => x.RoleId, x => x.Count);

        return roles.Select(r => new BusinessRoleResponse
        {
            Id = r.Id,
            BusinessId = r.BusinessId,
            Key = r.Key,
            Name = r.Name,
            Description = r.Description,
            IsOwnerRole = r.IsOwnerRole,
            IsSystem = r.IsSystem,
            IsActive = r.IsActive,
            SortOrder = r.SortOrder,
            Permissions = grants
                .Where(g => g.BusinessRoleId == r.Id)
                .Select(g => g.PermissionCode)
                .OrderBy(code => code, StringComparer.Ordinal)
                .ToList(),
            MemberCount = counts.TryGetValue(r.Id, out var value) ? value : 0,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt
        }).ToList();
    }

    private async Task<MembershipRolesResponse> BuildMembershipRolesAsync(
        BusinessMembership membership, Guid businessId)
    {
        var assignments = await _context.BusinessMembershipRoleAssignments.AsNoTracking()
            .Where(a => a.BusinessMembershipId == membership.Id)
            .Join(_context.BusinessRoles.AsNoTracking(), a => a.BusinessRoleId, r => r.Id,
                (a, r) => new
                {
                    a.Id,
                    a.BusinessRoleId,
                    a.CreatedAt,
                    a.AssignedByUserId,
                    r.Key,
                    r.Name,
                    r.IsOwnerRole,
                    r.IsActive
                })
            .OrderBy(x => x.Name)
            .ToListAsync();

        var fullName = await _context.Users.AsNoTracking()
            .Where(u => u.Id == membership.UserId).Select(u => u.FullName).FirstOrDefaultAsync();

        var effective = await _businessPermissions
            .GetEffectivePermissionsAsync(membership.UserId, businessId);

        return new MembershipRolesResponse
        {
            MembershipId = membership.Id,
            UserId = membership.UserId,
            FullName = fullName,
            MembershipRole = membership.Role.ToString(),
            Roles = assignments.Select(a => new MembershipRoleResponse
            {
                RoleId = a.BusinessRoleId,
                Key = a.Key,
                Name = a.Name,
                IsOwnerRole = a.IsOwnerRole,
                IsActive = a.IsActive,
                AssignedAt = a.CreatedAt,
                AssignedByUserId = a.AssignedByUserId
            }).ToList(),
            EffectivePermissions = effective.OrderBy(code => code, StringComparer.Ordinal).ToList()
        };
    }
}
