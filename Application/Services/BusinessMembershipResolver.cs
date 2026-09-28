using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>How a user is related to a business.</summary>
public enum BusinessMembershipKind
{
    None = 0,
    Owner = 1,
    Staff = 2,
    Customer = 3
}

/// <summary>
/// The single answer to "what is this user to this business?" — the
/// abstraction tenant-aware authentication, tenant claims and entry routing
/// consume instead of re-implementing the three legacy relationship checks.
/// </summary>
/// <param name="Kind">Owner / Staff / Customer (None when unrelated).</param>
/// <param name="Status">Lifecycle of the relationship: Active, Left, …</param>
/// <param name="BizRole">Tenant claim value (Owner/Staff/Customer), null when not a member.</param>
/// <param name="BusinessId">The business the membership was resolved against.</param>
public sealed record BusinessMembership(
    BusinessMembershipKind Kind,
    string Status,
    string? BizRole,
    Guid BusinessId)
{
    /// <summary>True when the user currently belongs to the business.</summary>
    public bool IsActive => Kind != BusinessMembershipKind.None &&
                            string.Equals(Status, "Active", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Unified membership view over the three existing relationship mechanisms —
/// no schema change and no new source of truth:
/// <list type="bullet">
/// <item><b>Owner</b> — <c>Business.OwnerId</c></item>
/// <item><b>Staff</b> — <c>User.StaffBusinessId</c></item>
/// <item><b>Customer</b> — active <c>CustomerBusinessEnrollment</c></item>
/// </list>
/// Every caller (tenant-aware login, refresh, host↔token consistency) gets the
/// same answer, so the rules can never drift apart.
/// </summary>
public interface IBusinessMembershipResolver
{
    Task<BusinessMembership> ResolveAsync(Guid userId, Guid businessId);
}

/// <remarks>Scoped: shares the request's DbContext (read-only queries only).</remarks>
public sealed class BusinessMembershipResolver : IBusinessMembershipResolver
{
    private readonly ApplicationDbContext _context;

    public BusinessMembershipResolver(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<BusinessMembership> ResolveAsync(Guid userId, Guid businessId)
    {
        if (userId == Guid.Empty || businessId == Guid.Empty)
            return new BusinessMembership(BusinessMembershipKind.None, "None", null, businessId);

        // Owner — Business.OwnerId is the authoritative owner relationship.
        var isOwner = await _context.Businesses
            .AsNoTracking()
            .AnyAsync(b => b.Id == businessId && b.OwnerId == userId);

        if (isOwner)
            return new BusinessMembership(BusinessMembershipKind.Owner, "Active", "Owner", businessId);

        // Staff — the user row carries the single business they work at.
        var isStaff = await _context.Users
            .AsNoTracking()
            .AnyAsync(u => u.Id == userId && u.StaffBusinessId == businessId);

        if (isStaff)
            return new BusinessMembership(BusinessMembershipKind.Staff, "Active", "Staff", businessId);

        // Customer — an explicit enrollment row (one per customer/business).
        var enrollment = await _context.CustomerBusinessEnrollments
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.CustomerId == userId && e.BusinessId == businessId);

        if (enrollment == null)
            return new BusinessMembership(BusinessMembershipKind.None, "None", null, businessId);

        return new BusinessMembership(
            BusinessMembershipKind.Customer,
            enrollment.Status.ToString(),
            // Only an ACTIVE enrollment grants tenant context; a Left row keeps
            // the history but not the membership.
            enrollment.Status == CustomerBusinessEnrollmentStatus.Active ? "Customer" : null,
            businessId);
    }

    /// <summary>Convenience for callers that already resolved a business id.</summary>
    public static string? RoleOrNull(BusinessMembership membership) => membership.BizRole;
}
