using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Append-only staff lifecycle audit event.
///
/// <para>Records who did what to whom, in which business and when. The table is
/// written only by <c>IStaffAuditService</c> and exposed through a read-only owner
/// endpoint — there is deliberately no update or delete path in the application,
/// so the trail cannot be rewritten through an ordinary API.</para>
///
/// <para><see cref="StaffUserId"/> is null for an invitation event created before
/// the invitee has an account; the invitation id then lives in
/// <see cref="DetailsJson"/>.</para>
/// </summary>
public class StaffAuditEvent : BaseEntity
{
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>The staff member concerned, when a person already exists.</summary>
    public Guid? StaffUserId { get; set; }

    /// <summary>The authenticated actor who performed the action (null for system actions).</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>One of <see cref="StaffAuditEventTypes"/>.</summary>
    [Required]
    [MaxLength(40)]
    public string EventType { get; set; } = string.Empty;

    /// <summary>Human-readable one-line description for the owner's activity view.</summary>
    [Required]
    [MaxLength(300)]
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Structured change details as JSON (changed field names, previous/next status,
    /// category ids, granted permission codes). Never includes secrets or tokens.
    /// </summary>
    [MaxLength(4000)]
    public string? DetailsJson { get; set; }
}

/// <summary>Canonical event type keys. Stored as strings so history survives enum renames.</summary>
public static class StaffAuditEventTypes
{
    public const string InvitationCreated = "invitation_created";
    public const string InvitationResent = "invitation_resent";
    public const string InvitationRevoked = "invitation_revoked";
    public const string InvitationAccepted = "invitation_accepted";
    public const string StaffActivated = "staff_activated";
    public const string StaffDeactivated = "staff_deactivated";
    public const string StaffProfileUpdated = "staff_profile_updated";
    public const string ServiceAssignmentsChanged = "service_assignments_changed";
    public const string StaffPermissionsChanged = "staff_permissions_changed";
    public const string PermissionOnboardingApplied = "permission_onboarding_applied";

    /// <summary>
    /// Future appointments of one member moved to a colleague — either while the
    /// member leaves, or to relieve a diary that is being resolved by hand.
    /// </summary>
    public const string AppointmentsReassigned = "appointments_reassigned";
}
