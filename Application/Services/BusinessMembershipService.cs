using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

/// <summary>
/// <b>The only writer of <see cref="BusinessMembership"/> rows.</b>
///
/// Every path that creates a tenant relationship — business provisioning,
/// staff invitation acceptance (new and existing identity), customer enrollment
/// and seeding — goes through this service, so the invariants can never drift:
/// <list type="bullet">
/// <item>one row per <c>(user, business)</c> (unique index backs this up);</item>
/// <item>role precedence <b>Owner &gt; Staff &gt; Customer</b>: a membership is
/// only ever upgraded, never silently downgraded;</item>
/// <item><c>Business.OwnerId</c> is a pointer maintained here, in the same
/// <c>SaveChanges</c> as the Owner row — it is never an authorization input;</item>
/// <item>mutations join the caller's unit of work: this service never calls
/// <c>SaveChanges</c> itself, so provisioning stays atomic.</item>
/// </list>
/// </summary>
public interface IBusinessMembershipService
{
    /// <summary>The membership row for (user, business), tracked, or null.</summary>
    Task<BusinessMembership?> FindAsync(Guid userId, Guid businessId);

    /// <summary>Creates or upgrades the membership. Does NOT save.</summary>
    Task<BusinessMembership> EnsureAsync(
        Guid userId,
        Guid businessId,
        BusinessMembershipRole role,
        Guid? invitedByUserId = null);

    /// <summary>Creates/upgrades the Owner membership and keeps <c>Business.OwnerId</c> in sync.</summary>
    Task<BusinessMembership> EnsureOwnerAsync(Guid userId, Business business, Guid? invitedByUserId = null);

    /// <summary>Revokes (or suspends) an active membership. Does NOT save.</summary>
    Task<BusinessMembership?> EndAsync(
        Guid userId,
        Guid businessId,
        BusinessMembershipStatus status = BusinessMembershipStatus.Left);

    /// <summary>True when the identity has an ACTIVE membership of the given role anywhere.</summary>
    Task<bool> HasRoleAnywhereAsync(Guid userId, BusinessMembershipRole role);

    /// <summary>True when the identity has any ACTIVE membership in the business.</summary>
    Task<bool> IsActiveMemberAsync(Guid userId, Guid businessId);

    /// <summary>All active memberships of the identity (role per business).</summary>
    Task<List<BusinessMembership>> ListActiveAsync(Guid userId);

    /// <summary>Active member user ids of a business for the given role.</summary>
    Task<List<Guid>> ListActiveUserIdsAsync(Guid businessId, BusinessMembershipRole role);

    /// <summary>
    /// The first ACTIVE business of the identity for the given role, or null.
    /// Replaces the legacy <c>User.StaffBusinessId</c> pointer: the membership
    /// row is the only place a staff relationship is recorded.
    /// </summary>
    Task<Guid?> GetActiveBusinessIdAsync(Guid userId, BusinessMembershipRole role);

    /// <summary>True when the identity is ACTIVE staff of the business.</summary>
    Task<bool> IsStaffOfAsync(Guid userId, Guid businessId);
}

/// <remarks>
/// Scoped: shares the request's <see cref="IUnitOfWork"/>, so the membership
/// write commits with the caller's business/user inserts and no second
/// transaction is ever opened.
/// </remarks>
public sealed class BusinessMembershipService : IBusinessMembershipService
{
    private readonly IUnitOfWork _unitOfWork;

    // Rows this scoped service has already ensured in the caller's open unit of
    // work. EF's query path cannot see a row that is Added but not yet saved, so
    // without this a second grant for the same pair inside one request would
    // insert a duplicate and fail the whole commit on the unique index.
    private readonly Dictionary<(Guid UserId, Guid BusinessId), BusinessMembership> _ensured = new();

    public BusinessMembershipService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public Task<BusinessMembership?> FindAsync(Guid userId, Guid businessId) =>
        _unitOfWork.BusinessMemberships.FirstOrDefaultAsync(m =>
            m.UserId == userId && m.BusinessId == businessId);

    public async Task<BusinessMembership> EnsureAsync(
        Guid userId,
        Guid businessId,
        BusinessMembershipRole role,
        Guid? invitedByUserId = null)
    {
        if (userId == Guid.Empty) throw new ArgumentException("userId is required", nameof(userId));
        if (businessId == Guid.Empty) throw new ArgumentException("businessId is required", nameof(businessId));

        var key = (userId, businessId);
        var existing = _ensured.TryGetValue(key, out var pending)
            ? pending
            : await FindAsync(userId, businessId);
        var now = DateTime.UtcNow;

        if (existing == null)
        {
            var membership = new BusinessMembership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                BusinessId = businessId,
                Role = role,
                Status = BusinessMembershipStatus.Active,
                InvitedByUserId = invitedByUserId,
                JoinedAt = now,
                UpdatedAt = now,
                CreatedAt = now
            };
            await _unitOfWork.BusinessMemberships.AddAsync(membership);
            _ensured[key] = membership;
            return membership;
        }

        var changed = false;

        // Upgrade only: Owner > Staff > Customer. A person who owns a business
        // and also buys from it keeps the stronger role; a customer invited as
        // staff becomes Staff (the invitation explicitly grants it).
        if (RoleRank(role) > RoleRank(existing.Role))
        {
            existing.Role = role;
            changed = true;
        }

        if (existing.Status != BusinessMembershipStatus.Active)
        {
            existing.Status = BusinessMembershipStatus.Active;
            existing.EndedAt = null;
            existing.JoinedAt = now;
            changed = true;
        }

        if (invitedByUserId.HasValue && existing.InvitedByUserId == null)
        {
            existing.InvitedByUserId = invitedByUserId;
            changed = true;
        }

        if (changed)
        {
            existing.UpdatedAt = now;
            // No Update(): the row is tracked (either loaded by FindAsync or added
            // above). Calling Update() on a not-yet-saved row would flip it from
            // Added to Modified and turn the INSERT into an UPDATE of nothing.
        }

        _ensured[key] = existing;
        return existing;
    }

    public async Task<BusinessMembership> EnsureOwnerAsync(
        Guid userId,
        Business business,
        Guid? invitedByUserId = null)
    {
        var membership = await EnsureAsync(
            userId, business.Id, BusinessMembershipRole.Owner, invitedByUserId);

        // Denormalized pointer for legacy joins/FKs; written here and only here.
        // The instance is already tracked (provisioning adds it, seeding loads
        // it), so assigning the field is enough — calling Update() on a
        // not-yet-inserted business would flip it to Modified and turn the
        // INSERT into an UPDATE of a row that does not exist.
        if (business.OwnerId != userId)
            business.OwnerId = userId;

        return membership;
    }

    public async Task<BusinessMembership?> EndAsync(
        Guid userId,
        Guid businessId,
        BusinessMembershipStatus status = BusinessMembershipStatus.Left)
    {
        if (status == BusinessMembershipStatus.Active)
            throw new ArgumentException("Use EnsureAsync to activate a membership.", nameof(status));

        var membership = await FindAsync(userId, businessId);
        if (membership == null)
            return null;

        // The next EnsureAsync must observe the revoked state, not a cached row.
        _ensured.Remove((userId, businessId));

        if (membership.Status != status)
        {
            membership.Status = status;
            membership.EndedAt = DateTime.UtcNow;
            membership.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.BusinessMemberships.Update(membership);
        }

        // Owner revocation must not leave a dangling owner pointer.
        if (membership.Role == BusinessMembershipRole.Owner && membership.Status != BusinessMembershipStatus.Active)
        {
            var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.Id == businessId);
            if (business != null && business.OwnerId == userId)
            {
                // Null (not Guid.Empty): owner_id is a nullable FK, and a
                // sentinel uuid would violate the foreign key in PostgreSQL.
                business.OwnerId = null;
                _unitOfWork.Businesses.Update(business);
            }
        }

        return membership;
    }

    public Task<bool> HasRoleAnywhereAsync(Guid userId, BusinessMembershipRole role) =>
        _unitOfWork.BusinessMemberships.AnyAsync(m =>
            m.UserId == userId &&
            m.Role == role &&
            m.Status == BusinessMembershipStatus.Active);

    public Task<bool> IsActiveMemberAsync(Guid userId, Guid businessId) =>
        _unitOfWork.BusinessMemberships.AnyAsync(m =>
            m.UserId == userId &&
            m.BusinessId == businessId &&
            m.Status == BusinessMembershipStatus.Active);

    public async Task<List<BusinessMembership>> ListActiveAsync(Guid userId)
    {
        var memberships = await _unitOfWork.BusinessMemberships.FindNoTrackingAsync(m =>
            m.UserId == userId && m.Status == BusinessMembershipStatus.Active);

        return memberships
            .OrderBy(m => m.Role)
            .ThenByDescending(m => m.JoinedAt)
            .ToList();
    }

    public async Task<List<Guid>> ListActiveUserIdsAsync(Guid businessId, BusinessMembershipRole role)
    {
        var memberships = await _unitOfWork.BusinessMemberships.SelectNoTrackingAsync(
            m => m.BusinessId == businessId &&
                 m.Role == role &&
                 m.Status == BusinessMembershipStatus.Active,
            m => m.UserId);

        return memberships.Distinct().ToList();
    }

    public async Task<Guid?> GetActiveBusinessIdAsync(Guid userId, BusinessMembershipRole role)
    {
        var businesses = await _unitOfWork.BusinessMemberships.SelectNoTrackingAsync(
            m => m.UserId == userId &&
                 m.Role == role &&
                 m.Status == BusinessMembershipStatus.Active,
            m => (Guid?)m.BusinessId);

        return businesses.FirstOrDefault();
    }

    public Task<bool> IsStaffOfAsync(Guid userId, Guid businessId) =>
        _unitOfWork.BusinessMemberships.AnyAsync(m =>
            m.UserId == userId &&
            m.BusinessId == businessId &&
            m.Role == BusinessMembershipRole.Staff &&
            m.Status == BusinessMembershipStatus.Active);

    private static int RoleRank(BusinessMembershipRole role) => role switch
    {
        BusinessMembershipRole.Owner => 3,
        BusinessMembershipRole.Staff => 2,
        _ => 1
    };
}
