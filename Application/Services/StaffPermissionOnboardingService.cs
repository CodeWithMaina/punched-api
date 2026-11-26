using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Owner-facing onboarding for the staff capabilities introduced after some businesses
/// were already provisioned.
///
/// <para><b>Nothing is granted automatically.</b> The platform does not rewrite an
/// existing business's roles behind the owner's back; it reports what staff still
/// cannot do, offers a least-privilege recommendation per platform role, and applies
/// exactly what the owner confirms. Every grant is audited.</para>
/// </summary>
public interface IStaffPermissionOnboardingService
{
    Task<ApiResponse<StaffPermissionOnboardingResponse>> GetAsync(
        Guid callerUserId, CancellationToken cancellationToken = default);

    Task<ApiResponse<StaffPermissionOnboardingResponse>> ApplyAsync(
        Guid callerUserId, ApplyStaffPermissionOnboardingRequest request, CancellationToken cancellationToken = default);
}

public sealed class StaffPermissionOnboardingService : IStaffPermissionOnboardingService
{
    /// <summary>
    /// Codes this shortcut may grant. Deliberately a closed list: permission onboarding
    /// must never become a way to hand out an arbitrary capability.
    /// </summary>
    private static readonly HashSet<string> GrantableCodes = new(StringComparer.Ordinal)
    {
        "appointments.create",
        "appointments.manage",
        "payments.verify",
        "payments.confirm_cash",
        "finances.view",
        "finances.analytics.view"
    };

    /// <summary>Least-privilege recommendation for each platform role template.</summary>
    private static readonly Dictionary<string, string[]> Recommended = new(StringComparer.Ordinal)
    {
        [PermissionCatalog.DefaultStaffRoleKey] = ["appointments.create"],
        ["receptionist"] = ["appointments.create", "payments.verify"]
    };

    private static readonly (string Code, string Name, string Description)[] CapabilityCatalog =
    [
        ("appointments.create", "Create appointments",
            "Book a customer in, pinned to the member themselves unless they may assign others."),
        ("appointments.manage", "Manage assigned appointments",
            "Reassign an appointment to another member and administer the booking."),
        ("payments.verify", "Verify payments",
            "Approve a submitted manual M-PESA claim after reviewing its evidence."),
        ("payments.confirm_cash", "Confirm cash",
            "Confirm that physical cash was received for a payment."),
        ("finances.view", "View financial reports",
            "Read the business's money screens, including staff financials."),
        ("finances.analytics.view", "View financial analytics",
            "Read analytical views over the business's ledger.")
    ];

    private readonly ApplicationDbContext _context;
    private readonly IBusinessScopeResolver _scopeResolver;
    private readonly IBusinessPermissionService _permissions;
    private readonly IStaffAuditService? _audit;
    private readonly ILogger<StaffPermissionOnboardingService> _logger;

    public StaffPermissionOnboardingService(
        ApplicationDbContext context,
        IBusinessScopeResolver scopeResolver,
        IBusinessPermissionService permissions,
        ILogger<StaffPermissionOnboardingService> logger,
        IStaffAuditService? audit = null)
    {
        _context = context;
        _scopeResolver = scopeResolver;
        _permissions = permissions;
        _logger = logger;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<ApiResponse<StaffPermissionOnboardingResponse>> GetAsync(
        Guid callerUserId, CancellationToken cancellationToken = default)
    {
        var businessId = await _scopeResolver.GetOwnedBusinessIdAsync(callerUserId);
        if (businessId == null)
            return ApiResponse<StaffPermissionOnboardingResponse>.Fail(
                "NOT_FOUND", "No business found for this account.");

        await _permissions.EnsureProvisionedAsync(businessId.Value, cancellationToken);
        var state = await BuildStateAsync(businessId.Value, cancellationToken);
        return ApiResponse<StaffPermissionOnboardingResponse>.Ok(state);
    }

    /// <inheritdoc />
    public async Task<ApiResponse<StaffPermissionOnboardingResponse>> ApplyAsync(
        Guid callerUserId, ApplyStaffPermissionOnboardingRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Grants is not { Count: > 0 })
            return ApiResponse<StaffPermissionOnboardingResponse>.Fail(
                "VALIDATION_ERROR", "Choose at least one permission to grant.");

        var businessId = await _scopeResolver.GetOwnedBusinessIdAsync(callerUserId);
        if (businessId == null)
            return ApiResponse<StaffPermissionOnboardingResponse>.Fail(
                "NOT_FOUND", "No business found for this account.");

        await _permissions.EnsureProvisionedAsync(businessId.Value, cancellationToken);

        var roleIds = request.Grants.Select(g => g.RoleId).Distinct().ToList();
        var roles = await _context.BusinessRoles
            .Where(r => r.BusinessId == businessId.Value && roleIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, cancellationToken);

        if (roles.Count != roleIds.Count)
            return ApiResponse<StaffPermissionOnboardingResponse>.Fail(
                "NOT_FOUND", "One or more roles do not belong to your business.");

        var existing = (await _context.BusinessRolePermissions
                .Where(p => roleIds.Contains(p.BusinessRoleId))
                .Select(p => new { p.BusinessRoleId, p.PermissionCode })
                .ToListAsync(cancellationToken))
            .Select(p => (p.BusinessRoleId, p.PermissionCode))
            .ToHashSet();

        var applied = new List<object>();
        var now = DateTime.UtcNow;

        foreach (var grant in request.Grants)
        {
            var role = roles[grant.RoleId];

            // The owner role already holds every code; granting it is meaningless.
            if (role.IsOwnerRole)
                return ApiResponse<StaffPermissionOnboardingResponse>.Fail(
                    "VALIDATION_ERROR", "The owner role already holds every permission.");

            if (!role.IsActive)
                return ApiResponse<StaffPermissionOnboardingResponse>.Fail(
                    "VALIDATION_ERROR", $"The role “{role.Name}” is inactive. Activate it first.");

            var codes = (grant.PermissionCodes ?? [])
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var unknown = codes.Where(code => !GrantableCodes.Contains(code)).ToList();
            if (unknown.Count > 0)
                return ApiResponse<StaffPermissionOnboardingResponse>.Fail(
                    "UNKNOWN_PERMISSION",
                    $"These permissions cannot be granted here: {string.Join(", ", unknown)}.");

            var added = new List<string>();
            foreach (var code in codes)
            {
                if (existing.Contains((role.Id, code))) continue;

                _context.BusinessRolePermissions.Add(new BusinessRolePermission
                {
                    Id = Guid.NewGuid(),
                    BusinessRoleId = role.Id,
                    PermissionCode = code,
                    CreatedAt = now
                });
                existing.Add((role.Id, code));
                added.Add(code);
            }

            if (added.Count == 0) continue;

            role.UpdatedAt = now;
            applied.Add(new { roleId = role.Id, roleKey = role.Key, permissions = added });
        }

        if (applied.Count == 0)
            return ApiResponse<StaffPermissionOnboardingResponse>.Ok(
                await BuildStateAsync(businessId.Value, cancellationToken));

        _audit?.Record(
            businessId.Value,
            staffUserId: null,
            actorUserId: callerUserId,
            StaffAuditEventTypes.PermissionOnboardingApplied,
            $"Staff permissions updated for {applied.Count} role(s).",
            new { grants = applied });

        await _context.SaveChangesAsync(cancellationToken);

        // Immediate effect: the permission cache must not keep serving the old set.
        _permissions.Invalidate(businessId.Value);
        _logger.LogInformation(
            "Staff permission onboarding applied by {Actor} in business {BusinessId}: {Roles} role(s).",
            callerUserId, businessId.Value, applied.Count);

        return ApiResponse<StaffPermissionOnboardingResponse>.Ok(
            await BuildStateAsync(businessId.Value, cancellationToken));
    }

    /// <summary>Reads the current role/capability state in three batched queries.</summary>
    private async Task<StaffPermissionOnboardingResponse> BuildStateAsync(
        Guid businessId, CancellationToken cancellationToken)
    {
        var roles = await _context.BusinessRoles.AsNoTracking()
            .Where(r => r.BusinessId == businessId)
            .Select(r => new
            {
                r.Id,
                r.Key,
                r.Name,
                r.IsOwnerRole,
                r.IsSystem,
                r.IsActive,
                r.SortOrder
            })
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);

        var roleIds = roles.Select(r => r.Id).ToList();

        var grants = await _context.BusinessRolePermissions.AsNoTracking()
            .Where(p => roleIds.Contains(p.BusinessRoleId))
            .Select(p => new { p.BusinessRoleId, p.PermissionCode })
            .ToListAsync(cancellationToken);

        var counts = (await _context.BusinessMembershipRoleAssignments.AsNoTracking()
                .Where(a => roleIds.Contains(a.BusinessRoleId))
                .GroupBy(a => a.BusinessRoleId)
                .Select(g => new { RoleId = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.RoleId, x => x.Count);

        var codesByRole = grants
            .GroupBy(g => g.BusinessRoleId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PermissionCode).ToHashSet(StringComparer.Ordinal));

        // The owner role is excluded from "staff capability" reporting: it holds every
        // code by construction, so including it would hide every real gap.
        var staffRoles = roles.Where(r => !r.IsOwnerRole).ToList();

        var capabilities = CapabilityCatalog.Select(capability =>
        {
            var granting = staffRoles
                .Where(r => r.IsActive
                    && codesByRole.TryGetValue(r.Id, out var codes)
                    && codes.Contains(capability.Code))
                .Select(r => r.Name)
                .ToList();

            return new StaffCapabilityResponse
            {
                Code = capability.Code,
                Name = capability.Name,
                Description = capability.Description,
                GrantedToStaffRoles = granting.Count > 0,
                GrantedRoles = granting
            };
        }).ToList();

        var recommendations = staffRoles
            .Where(r => r.IsActive && Recommended.ContainsKey(r.Key))
            .SelectMany(role => Recommended[role.Key]
                .Where(code => !codesByRole.TryGetValue(role.Id, out var codes) || !codes.Contains(code))
                .Select(code => new StaffPermissionRecommendationResponse
                {
                    RoleId = role.Id,
                    RoleName = role.Name,
                    PermissionCode = code,
                    PermissionName = PermissionCatalog.Find(code)?.Name ?? code,
                    Reason = CapabilityCatalog.FirstOrDefault(c => c.Code == code).Description
                        ?? "Recommended for this role."
                }))
            .ToList();

        var roleRows = staffRoles.Select(role => new StaffRoleOnboardingResponse
        {
            RoleId = role.Id,
            Key = role.Key,
            Name = role.Name,
            IsSystem = role.IsSystem,
            IsActive = role.IsActive,
            MemberCount = counts.GetValueOrDefault(role.Id),
            MissingRecommendedPermissions = role.IsActive && Recommended.TryGetValue(role.Key, out var recommended)
                ? recommended
                    .Where(code => !codesByRole.TryGetValue(role.Id, out var codes) || !codes.Contains(code))
                    .ToList()
                : []
        }).ToList();

        var coreCapabilitiesCovered = capabilities
            .Where(c => c.Code is "appointments.create" or "payments.verify")
            .All(c => c.GrantedToStaffRoles);

        return new StaffPermissionOnboardingResponse
        {
            BusinessId = businessId,
            HasGaps = recommendations.Count > 0 || !coreCapabilitiesCovered,
            Capabilities = capabilities,
            Roles = roleRows,
            Recommendations = recommendations
        };
    }
}
