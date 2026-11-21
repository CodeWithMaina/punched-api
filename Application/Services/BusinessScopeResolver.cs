using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Caching;
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
///
/// <para>The cache is <see cref="ICacheStore"/>: an in-process memory cache when
/// Redis is disabled (development, tests) and Redis when
/// <c>Redis:Enabled=true</c> (the compose stack). With Redis the scope survives a
/// restart and is shared by the API and the worker, so a membership change made
/// through one process invalidates the same key the other reads. Either way the
/// TTLs below bound staleness — this is a lookup accelerator, never an authority:
/// every tenant-scoped service still re-checks ownership against the database.</para>
/// </remarks>
public sealed class BusinessScopeResolver : IBusinessScopeResolver
{
    private const string MemberKeyPrefix = "bizscope:member:";
    private const string OwnerKeyPrefix = "bizscope:owner:";
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICacheStore _cache;

    public BusinessScopeResolver(IServiceScopeFactory scopeFactory, ICacheStore cache)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
    }

    public async Task<Guid?> GetMemberBusinessIdAsync(Guid userId)
    {
        var key = MemberKeyPrefix + userId;
        var cached = await _cache.GetStringAsync(key);
        if (cached is not null)
        {
            return Decode(cached);
        }

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

        // A missing mapping is cached very briefly so a business created moments
        // ago is picked up quickly without hammering the DB.
        await _cache.SetStringAsync(key, Encode(businessId), businessId.HasValue ? PositiveTtl : NegativeTtl);
        return businessId;
    }

    public async Task<Guid?> GetOwnedBusinessIdAsync(Guid ownerId)
    {
        var key = OwnerKeyPrefix + ownerId;
        var cached = await _cache.GetStringAsync(key);
        if (cached is not null)
        {
            return Decode(cached);
        }

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

        await _cache.SetStringAsync(key, Encode(businessId), businessId.HasValue ? PositiveTtl : NegativeTtl);
        return businessId;
    }

    /// <summary>
    /// Drops both cached scopes for an identity. Removal is asynchronous by
    /// nature; <see cref="ICacheStore"/> implementations swallow transport errors,
    /// so the only possible outcome of a lost removal is that the entry lives
    /// until its (≤ 60 s) TTL expires.
    /// </summary>
    public void InvalidateOwner(Guid ownerId)
    {
        _ = _cache.RemoveAsync(OwnerKeyPrefix + ownerId);
        _ = _cache.RemoveAsync(MemberKeyPrefix + ownerId);
    }

    /// <summary>Empty string encodes "no active membership" (a cached negative).</summary>
    private static string Encode(Guid? businessId) => businessId?.ToString("D") ?? string.Empty;

    /// <summary>
    /// A value that cannot be parsed is treated as a cache miss rather than an
    /// error: a corrupted or foreign entry in a shared Redis instance must never
    /// turn a tenant resolution into a 500.
    /// </summary>
    private static Guid? Decode(string cached) =>
        Guid.TryParse(cached, out var parsed) ? parsed : null;
}


