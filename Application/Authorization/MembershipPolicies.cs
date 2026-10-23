using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Authorization;

/// <summary>
/// Tenant authorization policies — the single place where "may this identity
/// act on this business?" is answered.
///
/// <para>Each policy resolves the caller's <b>active membership</b> (from
/// <c>business_memberships</c>) against the active tenant, or — when the request
/// did not arrive on a business address — against the identity's own
/// memberships. The global persona claim (<c>Role</c>) is never an input:
/// a stashed "Business" role in a JWT cannot grant tenant access, and losing
/// staff status takes effect immediately without waiting for a token refresh.</para>
///
/// <para><see cref="PlatformAdmin"/> is the only persona-based policy: platform
/// administration is a separate axis from tenancy, not a stronger tenant role.</para>
/// </summary>
public static class MembershipPolicies
{
    /// <summary>Owner of the active business (or of an owned business on the root host).</summary>
    public const string Owner = "BusinessOwner";

    /// <summary>Active staff member of the active business.</summary>
    public const string Staff = "BusinessStaff";

    /// <summary>Owner or staff of the active business.</summary>
    public const string OwnerOrStaff = "BusinessOwnerOrStaff";

    /// <summary>Owner or staff, or a platform administrator.</summary>
    public const string OwnerOrStaffOrAdmin = "BusinessOwnerOrStaffOrAdmin";

    /// <summary>Any active member of the business (Owner, Staff or Customer).</summary>
    public const string Member = "BusinessMember";

    /// <summary>Platform administrator only (global operator, never a tenant).</summary>
    public const string PlatformAdmin = "PlatformAdmin";

    /// <summary>
    /// Any authenticated identity. Used by endpoints that serve both customers
    /// and business members (the tenant boundary is still enforced by
    /// <c>TenantConsistencyMiddleware</c> and by each service's own scoping).
    /// </summary>
    public const string Authenticated = "AuthenticatedIdentity";

    public static void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddPolicy(Owner, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new BusinessMembershipRequirement(BusinessMembershipRole.Owner)));

        options.AddPolicy(Staff, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new BusinessMembershipRequirement(BusinessMembershipRole.Staff)));

        options.AddPolicy(OwnerOrStaff, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new BusinessMembershipRequirement(
                BusinessMembershipRole.Owner, BusinessMembershipRole.Staff)));

        options.AddPolicy(OwnerOrStaffOrAdmin, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new BusinessMembershipRequirement(
                BusinessMembershipRole.Owner, BusinessMembershipRole.Staff)
            { AllowPlatformAdmin = true }));

        options.AddPolicy(Member, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new BusinessMembershipRequirement(
                BusinessMembershipRole.Owner,
                BusinessMembershipRole.Staff,
                BusinessMembershipRole.Customer)));

        options.AddPolicy(PlatformAdmin, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(ClaimTypes.Role, nameof(UserRole.Admin)));

        // Authenticated identity, no tenant claim required.
        options.AddPolicy(Authenticated, policy => policy.RequireAuthenticatedUser());
    }
}


/// <summary>Requires an ACTIVE membership with one of the listed roles.</summary>
public sealed class BusinessMembershipRequirement : IAuthorizationRequirement
{
    public BusinessMembershipRequirement(params BusinessMembershipRole[] roles)
    {
        if (roles.Length == 0)
            throw new ArgumentException("At least one role is required.", nameof(roles));

        Roles = roles;
    }

    public IReadOnlyList<BusinessMembershipRole> Roles { get; }

    /// <summary>Platform administrators bypass tenant membership checks.</summary>
    public bool AllowPlatformAdmin { get; init; }
}

/// <summary>Resolves the requirement against the canonical membership table.</summary>
public sealed class BusinessMembershipAuthorizationHandler
    : AuthorizationHandler<BusinessMembershipRequirement>
{
    private readonly IBusinessMembershipResolver _memberships;
    private readonly IBusinessMembershipService _service;
    private readonly ITenantContext? _tenant;

    public BusinessMembershipAuthorizationHandler(
        IBusinessMembershipResolver memberships,
        IBusinessMembershipService service,
        ITenantContext? tenant = null)
    {
        _memberships = memberships;
        _service = service;
        _tenant = tenant;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        BusinessMembershipRequirement requirement)
    {
        var principal = context.User;
        if (principal?.Identity?.IsAuthenticated != true)
            return;

        var role = principal.FindFirstValue(ClaimTypes.Role) ?? principal.FindFirstValue("role");
        if (requirement.AllowPlatformAdmin &&
            string.Equals(role, nameof(UserRole.Admin), StringComparison.OrdinalIgnoreCase))
        {
            context.Succeed(requirement);
            return;
        }

        var rawUserId = principal.FindFirst("userId")?.Value ??
                        principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(rawUserId, out var userId) || userId == Guid.Empty)
            return;

        // 1. The request arrived on a business address: the membership of THAT
        //    business is the only thing that counts (a host can only narrow).
        if (_tenant?.IsActive == true && _tenant.BusinessId is Guid tenantBusinessId)
        {
            var membership = await _memberships.ResolveAsync(userId, tenantBusinessId);
            if (membership.IsActive && requirement.Roles.Contains(ToEntityRole(membership.Kind)))
                context.Succeed(requirement);
            return;
        }

        // 2. Platform root: any active membership of the required kind. The
        //    endpoint/service still resolves the concrete business to act on.
        foreach (var candidate in requirement.Roles)
        {
            if (await _service.HasRoleAnywhereAsync(userId, candidate))
            {
                context.Succeed(requirement);
                return;
            }
        }
    }

    private static BusinessMembershipRole ToEntityRole(BusinessMembershipKind kind) => kind switch
    {
        BusinessMembershipKind.Owner => BusinessMembershipRole.Owner,
        BusinessMembershipKind.Staff => BusinessMembershipRole.Staff,
        _ => BusinessMembershipRole.Customer
    };
}
