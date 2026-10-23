using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Resolves the authenticated identity's business scope from the canonical
/// <c>business_memberships</c> table, with a short-TTL cache so hot request
/// paths do not pay a database round trip per request purely for tenant
/// resolution.
///
/// The scope is derived from the caller's ACTIVE membership — never from
/// <c>Business.OwnerId</c>, <c>User.Role</c>, or the dropped
/// <c>users."StaffBusinessId"</c> column. Any flow that changes a membership
/// invalidates the identity's key immediately.
/// </summary>
public interface IBusinessScopeResolver
{
    /// <summary>
    /// Any active membership, strongest role first (Owner &gt; Staff &gt; Customer).
    /// This is the caller's *acting* business when no tenant host is supplied.
    /// </summary>
    Task<Guid?> GetMemberBusinessIdAsync(Guid userId);

    /// <summary>An active OWNER membership, or null. Never infers ownership.</summary>
    Task<Guid?> GetOwnedBusinessIdAsync(Guid ownerId);

    void InvalidateOwner(Guid ownerId);
}

/// <remarks>
/// Registered as a singleton, so the (scoped) DbContext is resolved per lookup
/// through IServiceScopeFactory instead of being captured in the singleton.
/// </remarks>
public sealed class BusinessScopeResolver : IBusinessScopeResolver
{
    private const string MemberKeyPrefix = "bizscope:member:";
    private const string OwnerKeyPrefix = "bizscope:owner:";
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;

    public BusinessScopeResolver(IServiceScopeFactory scopeFactory, IMemoryCache cache)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
    }

    public async Task<Guid?> GetMemberBusinessIdAsync(Guid userId)
    {
        return await _cache.GetOrCreateAsync(MemberKeyPrefix + userId, async entry =>
        {
            Guid? businessId;
            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Strongest role wins so an owner who also shops at their own
                // business is scoped to the owner view. One indexed read on
                // (user_id, status); the unique (user_id, business_id) key keeps
                // it to one row per business.
                var memberships = await context.BusinessMemberships
                    .AsNoTracking()
                    .Where(m => m.UserId == userId &&
                                m.Status == BusinessMembershipStatus.Active)
                    .OrderByDescending(m => m.Role == BusinessMembershipRole.Owner)
                    .ThenByDescending(m => m.Role == BusinessMembershipRole.Staff)
                    .Select(m => (Guid?)m.BusinessId)
                    .ToListAsync();

                businessId = memberships.FirstOrDefault();
            }

            // A missing mapping is cached very briefly so a business created
            // moments ago is picked up quickly without hammering the DB.
            entry.AbsoluteExpirationRelativeToNow =
                businessId.HasValue ? PositiveTtl : NegativeTtl;

            return businessId;
        });
    }

    public async Task<Guid?> GetOwnedBusinessIdAsync(Guid ownerId)
    {
        return await _cache.GetOrCreateAsync(OwnerKeyPrefix + ownerId, async entry =>
        {
            Guid? businessId;
            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                businessId = await context.BusinessMemberships
                    .AsNoTracking()
                    .Where(m => m.UserId == ownerId &&
                                m.Role == BusinessMembershipRole.Owner &&
                                m.Status == BusinessMembershipStatus.Active)
                    .Select(m => (Guid?)m.BusinessId)
                    .FirstOrDefaultAsync();
            }

            entry.AbsoluteExpirationRelativeToNow =
                businessId.HasValue ? PositiveTtl : NegativeTtl;

            return businessId;
        });
    }

    public void InvalidateOwner(Guid ownerId)
    {
        _cache.Remove(OwnerKeyPrefix + ownerId);
        _cache.Remove(MemberKeyPrefix + ownerId);
    }
}

