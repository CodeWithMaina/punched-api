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

    /// <summary>
    /// Optional job title / position within the business (Senior Stylist,
    /// Receptionist, ...). Purely descriptive: a position grants no permission
    /// and no service eligibility. History is preserved in
    /// <see cref="StaffPositionHistory"/>.
    /// </summary>
    public Guid? PositionId { get; set; }

    /// <summary>
    /// Short staff-authored biography shown on the storefront profile. Belongs to
    /// the MEMBERSHIP, not the identity: the same person describes themselves
    /// differently at each business they work for.
    /// </summary>
    [MaxLength(300)]
    public string? Bio { get; set; }

    /// <summary>
    /// What the member is known for at this business — free text, comma-separated
    /// ("Balayage, Curly cuts, Bridal hair"). Business-scoped like
    /// <see cref="Bio"/>, and exposed publicly only when <see cref="IsPublic"/>
    /// is true. Null means "not stated", which the storefront renders as nothing
    /// rather than inventing specialities.
    /// </summary>
    [MaxLength(300)]
    public string? Specialities { get; set; }

    /// <summary>
    /// Whole years of professional experience, as the business states it. Null
    /// means "not stated" — the storefront shows no number rather than a
    /// fabricated one.
    /// </summary>
    [Range(0, 80)]
    public int? YearsOfExperience { get; set; }

    /// <summary>
    /// Whether this member is listed publicly on the business storefront. Only
    /// meaningful for <see cref="BusinessMembershipRole.Staff"/>; it narrows what
    /// customers see, and never grants or removes access.
    /// </summary>
    public bool IsPublic { get; set; } = true;

    /// <summary>
    /// Whether customers may pick this member as the provider when booking.
    /// Enforced server-side by the booking and availability paths, so a member
    /// switched off here stops being offered without losing any history.
    /// </summary>
    public bool IsBookable { get; set; } = true;

    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual User User { get; set; } = null!;
    public virtual Business Business { get; set; } = null!;
    public virtual StaffPosition? Position { get; set; }

    /// <summary>Tenant claim value for this role (Owner | Staff | Customer).</summary>
    public string RoleName => Role switch
    {
        BusinessMembershipRole.Owner => "Owner",
        BusinessMembershipRole.Staff => "Staff",
        _ => "Customer"
    };
}
