using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Authorization;

/// <summary>
/// <b>The tenant-scoped permission authority.</b>
///
/// <para>Answers "may this identity perform this action in this business?" from
/// the RBAC tables (<c>business_roles</c>, <c>business_role_permissions</c>,
/// <c>business_membership_role_assignments</c>) — never from a token claim, a
/// request field, or a static role matrix.</para>
///
/// <para><b>Provisioning.</b> Every business is guaranteed to have the
/// platform's default role templates and a role assignment for each of its
/// memberships. Provisioning is idempotent, race-safe (unique keys) and runs
/// lazily on first use, so businesses created before RBAC existed are upgraded
/// in place without a data migration and without losing legitimate access.</para>
///
/// <para><b>Freshness.</b> Effective permissions are memoized for at most
/// <see cref="CacheTtl"/> and the entry is invalidated immediately on any role,
/// permission or assignment write in this process. A permission change
/// therefore takes effect on the next request — no token refresh, no
/// re-login.</para>
/// </summary>
public interface IBusinessPermissionService
{
    /// <summary>Ensures the business has default roles and role assignments.</summary>
    Task EnsureProvisionedAsync(Guid businessId, CancellationToken cancellationToken = default);

    /// <summary>All permission codes the identity currently holds in the business.</summary>
    Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        Guid userId, Guid businessId, CancellationToken cancellationToken = default);

    /// <summary>Does the identity hold the permission in the business?</summary>
    Task<bool> HasPermissionAsync(
        Guid userId, Guid businessId, string permissionCode, CancellationToken cancellationToken = default);

    /// <summary>Resolves the business the identity acts for (tenant first), or null.</summary>
    Task<Guid?> ResolveCallerBusinessIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Drops memoized permissions for the business (call after any RBAC write).</summary>
    void Invalidate(Guid businessId);
}


/// <inheritdoc />
public sealed class BusinessPermissionService : IBusinessPermissionService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProvisionedTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Per-business RBAC generation. Bumping it makes every memoized permission
    /// set for that business unreachable at once, which is what makes a role
    /// change take effect predictably instead of "whenever the TTL expires".
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, long> Generations = new();

    private static readonly IReadOnlySet<string> NoPermissions = new HashSet<string>(StringComparer.Ordinal);

    private readonly ApplicationDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly ITenantContext? _tenant;
    private readonly IBusinessScopeResolver _scopeResolver;

    public BusinessPermissionService(
        ApplicationDbContext context,
        IMemoryCache cache,
        IBusinessScopeResolver scopeResolver,
        ITenantContext? tenant = null)
    {
        _context = context;
        _cache = cache;
        _scopeResolver = scopeResolver;
        _tenant = tenant;
    }

    /// <inheritdoc />
    public void Invalidate(Guid businessId)
    {
        if (businessId == Guid.Empty) return;
        Generations.AddOrUpdate(businessId, 1, (_, current) => current + 1);
        _cache.Remove(ProvisionedKey(businessId));
    }

    /// <inheritdoc />
    public async Task<Guid?> ResolveCallerBusinessIdAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        // The active tenant is the server-side answer when the request arrived
        // on a business address; it can only narrow, never grant.
        if (_tenant?.IsActive == true && _tenant.BusinessId is Guid tenantBusinessId)
            return tenantBusinessId;

        if (userId == Guid.Empty) return null;
        return await _scopeResolver.GetMemberBusinessIdAsync(userId);
    }

    /// <inheritdoc />
    public async Task<bool> HasPermissionAsync(
        Guid userId, Guid businessId, string permissionCode, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || businessId == Guid.Empty || string.IsNullOrWhiteSpace(permissionCode))
            return false;

        if (!PermissionCatalog.Exists(permissionCode))
            return false;

        var effective = await GetEffectivePermissionsAsync(userId, businessId, cancellationToken);
        return effective.Contains(permissionCode);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        Guid userId, Guid businessId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || businessId == Guid.Empty)
            return NoPermissions;

        await EnsureProvisionedAsync(businessId, cancellationToken);

        // The membership row is read FIRST and its identity + version form part
        // of the cache key. A membership change (suspension, re-activation,
        // role change) always bumps UpdatedAt, so the memoized answer can never
        // outlive the membership it describes — one indexed lookup, and the
        // expensive role/permission resolution is still cached.
        var membership = await _context.BusinessMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId &&
                        m.BusinessId == businessId &&
                        m.Status == BusinessMembershipStatus.Active)
            .Select(m => new { m.Id, m.Role, m.UpdatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        // No active membership of this business: no permissions at all. This is
        // the cross-tenant boundary — a user from another business resolves here.
        if (membership == null) return NoPermissions;

        // The business owner always holds full authority over their own
        // business, even if the owner-role assignment is somehow missing.
        if (membership.Role == BusinessMembershipRole.Owner)
            return PermissionCatalog.AllCodes;

        var key = PermissionKey(businessId, membership.Id, membership.UpdatedAt, GenerationFor(businessId));
        if (_cache.TryGetValue(key, out IReadOnlySet<string>? cached) && cached != null)
            return cached;

        var resolved = await LoadEffectiveAsync(membership.Id, businessId, cancellationToken);
        _cache.Set(key, resolved, CacheTtl);
        return resolved;
    }

    /// <summary>
    /// Resolves the union of the permissions granted by the roles assigned to a
    /// membership. Only ACTIVE roles count.
    /// </summary>
    private async Task<IReadOnlySet<string>> LoadEffectiveAsync(
        Guid membershipId, Guid businessId, CancellationToken cancellationToken)
    {
        var roles = await _context.BusinessMembershipRoleAssignments
            .AsNoTracking()
            .Where(a => a.BusinessMembershipId == membershipId)
            .Join(
                _context.BusinessRoles.AsNoTracking().Where(r => r.IsActive),
                assignment => assignment.BusinessRoleId,
                role => role.Id,
                (assignment, role) => new { role.Id, role.IsOwnerRole })
            .ToListAsync(cancellationToken);

        if (roles.Count == 0)
        {
            // Defensive fallback ONLY for a business that has not been upgraded
            // to RBAC at all (zero roles). If roles exist but this member holds
            // none, the honest answer is "no permissions" — falling back to the
            // persona matrix there would silently re-grant authority an owner
            // had deliberately withheld.
            var hasRoles = await _context.BusinessRoles.AsNoTracking()
                .AnyAsync(r => r.BusinessId == businessId, cancellationToken);

            return hasRoles ? NoPermissions : LegacyPersonaFallback(membershipId, businessId, cancellationToken);
        }

        if (roles.Any(r => r.IsOwnerRole))
            return PermissionCatalog.AllCodes;

        var ids = roles.Select(r => r.Id).ToList();
        var codes = await _context.BusinessRolePermissions
            .AsNoTracking()
            .Where(p => ids.Contains(p.BusinessRoleId))
            .Select(p => p.PermissionCode)
            .Distinct()
            .ToListAsync(cancellationToken);

        return codes.ToHashSet(StringComparer.Ordinal);
    }

    private IReadOnlySet<string> LegacyPersonaFallback(
        Guid membershipId, Guid businessId, CancellationToken cancellationToken)
    {
        // Resolved synchronously from the already-loaded role of the membership:
        // the caller has the membership row in hand, so re-reading it is
        // unnecessary. The persona matrix grants a strict subset of what that
        // persona had before RBAC existed, so this is not an escalation path.
        var role = _context.BusinessMemberships
            .AsNoTracking()
            .Where(m => m.Id == membershipId)
            .Select(m => (BusinessMembershipRole?)m.Role)
            .FirstOrDefault();

        var persona = role switch
        {
            BusinessMembershipRole.Staff => "Staff",
            BusinessMembershipRole.Customer => "Customer",
            _ => null
        };

        return persona == null ? NoPermissions : PermissionMatrix.PermissionsForRole(persona);
    }

    private static long GenerationFor(Guid businessId) => Generations.GetOrAdd(businessId, 0);

    private static string PermissionKey(Guid businessId, Guid membershipId, DateTime membershipUpdatedAt, long generation) =>
        $"rbac:perms:{businessId}:{membershipId}:{membershipUpdatedAt.Ticks}:{generation}";

    private static string ProvisionedKey(Guid businessId) => $"rbac:provisioned:{businessId}";

    /// <inheritdoc />
    public async Task EnsureProvisionedAsync(Guid businessId, CancellationToken cancellationToken = default)
    {
        if (businessId == Guid.Empty) return;

        var key = ProvisionedKey(businessId);
        if (_cache.TryGetValue(key, out bool done) && done) return;

        await ProvisionAsync(businessId, cancellationToken);
        _cache.Set(key, true, ProvisionedTtl);
    }

    private async Task ProvisionAsync(Guid businessId, CancellationToken cancellationToken)
    {
        var roles = await _context.BusinessRoles
            .Where(r => r.BusinessId == businessId)
            .ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, cancellationToken);

        var missing = PermissionCatalog.DefaultRoles
            .Where(template => !roles.ContainsKey(template.Key))
            .ToList();

        var now = DateTime.UtcNow;

        foreach (var template in missing)
        {
            var role = new BusinessRole
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                Key = template.Key,
                Name = template.Name,
                Description = template.Description,
                IsOwnerRole = template.IsOwnerRole,
                IsSystem = true,
                IsActive = true,
                SortOrder = template.SortOrder,
                UpdatedAt = now,
                CreatedAt = now
            };

            _context.BusinessRoles.Add(role);
            roles[template.Key] = role;

            if (!template.IsOwnerRole)
            {
                foreach (var code in template.Permissions.Distinct(StringComparer.Ordinal))
                {
                    _context.BusinessRolePermissions.Add(new BusinessRolePermission
                    {
                        Id = Guid.NewGuid(),
                        BusinessRoleId = role.Id,
                        PermissionCode = code,
                        CreatedAt = now
                    });
                }
            }
        }

        if (missing.Count > 0 && !await TrySaveAsync(cancellationToken))
        {
            // Another request provisioned the same business concurrently. The
            // unique keys guarantee exactly one winner; the loser reloads and
            // continues with the winner's rows.
            roles = await _context.BusinessRoles
                .Where(r => r.BusinessId == businessId)
                .ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, cancellationToken);
        }

        await EnsureAssignmentsAsync(businessId, roles, cancellationToken);
    }

    private async Task EnsureAssignmentsAsync(
        Guid businessId,
        IReadOnlyDictionary<string, BusinessRole> roles,
        CancellationToken cancellationToken)
    {
        var memberships = await _context.BusinessMemberships
            .Where(m => m.BusinessId == businessId &&
                        m.Status == BusinessMembershipStatus.Active &&
                        (m.Role == BusinessMembershipRole.Owner || m.Role == BusinessMembershipRole.Staff))
            .Select(m => new { m.Id, m.Role })
            .ToListAsync(cancellationToken);

        if (memberships.Count == 0) return;

        var membershipIds = memberships.Select(m => m.Id).ToList();
        var assigned = (await _context.BusinessMembershipRoleAssignments
                .Where(a => membershipIds.Contains(a.BusinessMembershipId))
                .Select(a => new { a.BusinessMembershipId, a.BusinessRoleId })
                .ToListAsync(cancellationToken))
            .Select(a => (a.BusinessMembershipId, a.BusinessRoleId))
            .ToHashSet();

        var now = DateTime.UtcNow;
        var added = false;

        foreach (var membership in memberships)
        {
            var roleKey = membership.Role == BusinessMembershipRole.Owner
                ? PermissionCatalog.OwnerRoleKey
                : PermissionCatalog.DefaultStaffRoleKey;

            if (!roles.TryGetValue(roleKey, out var role)) continue;
            if (assigned.Contains((membership.Id, role.Id))) continue;

            _context.BusinessMembershipRoleAssignments.Add(new BusinessMembershipRoleAssignment
            {
                Id = Guid.NewGuid(),
                BusinessMembershipId = membership.Id,
                BusinessRoleId = role.Id,
                CreatedAt = now
            });
            added = true;
        }

        if (added) await TrySaveAsync(cancellationToken);
    }

    /// <summary>
    /// Saves, returning false when the write lost a race (unique-key violation)
    /// and detaching the failed inserts so the caller can reload and continue.
    /// </summary>
    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            foreach (var entry in _context.ChangeTracker.Entries()
                         .Where(e => e.State == EntityState.Added)
                         .ToList())
            {
                entry.State = EntityState.Detached;
            }

            return false;
        }
    }
}
