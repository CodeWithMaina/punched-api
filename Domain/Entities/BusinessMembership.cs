using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// The role a person holds inside one business.
/// A person can hold different roles in different businesses, but only one role
/// per business (enforced by the unique <c>(user_id, business_id)</c> index).
/// </summary>
public enum BusinessMembershipRole
{
    /// <summary>Owns the business; full tenant administration.</summary>
    Owner = 1,

    /// <summary>Works at the business; operational surfaces only.</summary>
    Staff = 2,

    /// <summary>Enrolled customer of the business.</summary>
    Customer = 3
}

/// <summary>
/// Lifecycle of a <see cref="BusinessMembership"/>. Only <see cref="Active"/>
/// grants tenant access — everything else keeps the row (and its history) but
/// denies authorization.
/// </summary>
public enum BusinessMembershipStatus
{
    Active = 0,
    Invited = 1,
    Suspended = 2,
    Left = 3
}

/// <summary>
/// <b>The single authoritative user ↔ business relationship.</b>
///
/// Every tenant authorization decision resolves through this table:
/// ownership, staff membership and customer membership are the same row shape
/// with a different <see cref="Role"/>. There is no second source of truth for
/// "may this identity act in this business?":
/// <list type="bullet">
/// <item><see cref="Business.OwnerId"/> is a denormalized pointer written only
/// by <c>BusinessMembershipService</c> in the same transaction as the Owner row;
/// it is never used to authorize.</item>
/// <item><see cref="CustomerBusinessEnrollment"/> is the loyalty-domain detail
/// of a <see cref="BusinessMembershipRole.Customer"/> membership (stamps,
/// source attribution, history) and is written by the same service.</item>
/// </list>
///
/// Database invariants (see <c>BusinessMembershipConfiguration</c>):
/// unique <c>(user_id, business_id)</c>, exactly one active Owner per business,
/// role/status restricted to the enum values, FK to the tenant.
/// </summary>
public class BusinessMembership : BaseEntity
{
    [Required]
    public Guid UserId { get; set; }

    [Required]
    public Guid BusinessId { get; set; }

    [Required]
    public BusinessMembershipRole Role { get; set; }

    [Required]
    public BusinessMembershipStatus Status { get; set; } = BusinessMembershipStatus.Active;

    /// <summary>The member of staff/owner who caused the row to exist (invitations, manual adds).</summary>
    public Guid? InvitedByUserId { get; set; }

    /// <summary>When the membership became active.</summary>
    [Required]
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the membership stopped granting access (left/suspended/revoked).</summary>
    public DateTime? EndedAt { get; set; }

    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual User User { get; set; } = null!;
    public virtual Business Business { get; set; } = null!;

    /// <summary>Tenant claim value for this role (Owner | Staff | Customer).</summary>
    public string RoleName => Role switch
    {
        BusinessMembershipRole.Owner => "Owner",
        BusinessMembershipRole.Staff => "Staff",
        _ => "Customer"
    };
}
