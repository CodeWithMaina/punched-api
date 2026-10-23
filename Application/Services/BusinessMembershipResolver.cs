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
/// The single answer to "what is this user to this business?" — resolved from
/// the authoritative <see cref="BusinessMembership"/> table.
/// </summary>
/// <param name="Kind">Owner / Staff / Customer (None when unrelated).</param>
/// <param name="Status">Lifecycle of the relationship: Active, Left, Suspended, Invited, None.</param>
/// <param name="BizRole">Tenant claim value (Owner/Staff/Customer), null when not an active member.</param>
/// <param name="BusinessId">The business the membership was resolved against.</param>
public sealed record TenantMembership(
    BusinessMembershipKind Kind,
    string Status,
    string? BizRole,
    Guid BusinessId)
{
    /// <summary>True when the user currently belongs to the business.</summary>
    public bool IsActive => Kind != BusinessMembershipKind.None &&
                            string.Equals(Status, "Active", StringComparison.OrdinalIgnoreCase);

    /// <summary>No relationship.</summary>
    public static TenantMembership None(Guid businessId) =>
        new(BusinessMembershipKind.None, "None", null, businessId);
}

/// <summary>
/// Unified membership view — <b>one query, one source of truth</b>.
///
/// Before this resolver was rewritten, the same question was answered by three
/// different mechanisms (<c>Business.OwnerId</c>, <c>User.StaffBusinessId</c>,
/// <c>customer_business_enrollments</c>) with three round-trips and three
/// chances to disagree. It now reads the canonical
/// <c>business_memberships</c> row, so tenant claims, host/token consistency
/// and authorization can never drift apart.
/// </summary>
public interface IBusinessMembershipResolver
{
    Task<TenantMembership> ResolveAsync(Guid userId, Guid businessId);

    /// <summary>Active role per business for the identity (tenant switcher).</summary>
    Task<List<TenantMembership>> ResolveAllAsync(Guid userId);
}

/// <remarks>Scoped: shares the request's DbContext (read-only queries only).</remarks>
public sealed class BusinessMembershipResolver : IBusinessMembershipResolver
{
    private readonly ApplicationDbContext _context;

    public BusinessMembershipResolver(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<TenantMembership> ResolveAsync(Guid userId, Guid businessId)
    {
        if (userId == Guid.Empty || businessId == Guid.Empty)
            return TenantMembership.None(businessId);

        // Projected, no tracking: one indexed lookup on the unique
        // (user_id, business_id) key returns exactly what the caller needs.
        var row = await _context.BusinessMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.BusinessId == businessId)
            .Select(m => new { m.Role, m.Status })
            .FirstOrDefaultAsync();

        if (row == null)
            return TenantMembership.None(businessId);

        return new TenantMembership(
            MapRole(row.Role),
            row.Status.ToString(),
            // Only an ACTIVE membership grants tenant context; a Left/Suspended
            // row keeps the history but not the membership.
            row.Status == BusinessMembershipStatus.Active ? row.Role.ToString() : null,
            businessId);
    }

    public async Task<List<TenantMembership>> ResolveAllAsync(Guid userId)
    {
        if (userId == Guid.Empty)
            return new List<TenantMembership>();

        var rows = await _context.BusinessMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { m.Role, m.Status, m.BusinessId })
            .ToListAsync();

        return rows
            .Select(r => new TenantMembership(
                MapRole(r.Role),
                r.Status.ToString(),
                r.Status == BusinessMembershipStatus.Active ? r.Role.ToString() : null,
                r.BusinessId))
            .ToList();
    }

    private static BusinessMembershipKind MapRole(BusinessMembershipRole role) => role switch
    {
        BusinessMembershipRole.Owner => BusinessMembershipKind.Owner,
        BusinessMembershipRole.Staff => BusinessMembershipKind.Staff,
        _ => BusinessMembershipKind.Customer
    };
}
