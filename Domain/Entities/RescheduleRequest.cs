using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Lifecycle of a <see cref="RescheduleRequest"/>.
///
/// <para><b>A reschedule request is NOT an appointment status.</b> The
/// appointment keeps its normal lifecycle status (<c>booked</c> /
/// <c>confirmed</c> / ...) throughout; the request is a separate workflow that
/// either mutates the appointment's schedule (approved) or leaves it untouched
/// (rejected / withdrawn / expired).</para>
///
/// <para>Terminal states are <see cref="Approved"/>, <see cref="Rejected"/>,
/// <see cref="Withdrawn"/> and <see cref="Expired"/>. A pending request may
/// move to any of them, never back to <see cref="Pending"/>.</para>
/// </summary>
public enum RescheduleRequestStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Withdrawn = 3,
    Expired = 4
}

/// <summary>Which party raised the proposal.</summary>
public enum RescheduleRequestedByParty
{
    Customer = 0,
    Staff = 1,
    Owner = 2
}

/// <summary>
/// A rescheduling proposal for an existing appointment. The appointment is NOT
/// modified until the request is approved.
///
/// <para>Both the <b>original</b> and the <b>proposed</b> schedules are stored,
/// so the request is a self-contained record of the change even if the
/// appointment is later rescheduled again. Requests are never deleted or
/// overwritten: the full history of every proposal and decision is preserved.</para>
/// </summary>
public class RescheduleRequest : BaseEntity
{
    [Required]
    public Guid AppointmentId { get; set; }

    /// <summary>The tenant the request belongs to (denormalized for scoped queries).</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>The user who proposed the change (customer, staff member or owner).</summary>
    [Required]
    public Guid RequestedByUserId { get; set; }

    /// <summary>Which party the requester is acting as.</summary>
    [Required]
    public RescheduleRequestedByParty RequestedByParty { get; set; } = RescheduleRequestedByParty.Customer;

    /// <summary>Optional explanation supplied by the requester.</summary>
    [MaxLength(300)]
    public string? RequestReason { get; set; }

    // ── Original schedule (snapshot at request time) ─────────────
    public DateTime OriginalScheduledAt { get; set; }
    public DateTime OriginalEndAt { get; set; }
    public Guid? OriginalStaffUserId { get; set; }

    // ── Proposed schedule ────────────────────────────────────────
    public DateTime ProposedScheduledAt { get; set; }

    public DateTime ProposedEndAt { get; set; }

    public Guid? ProposedStaffUserId { get; set; }

    /// <summary>
    /// JSON snapshot of the proposed service configuration
    /// (List&lt;AppointmentServiceSnapshot&gt; serialized) so the proposal is
    /// immutable even if the catalog changes before approval.
    /// </summary>
    [Required]
    public string ProposedServicesJson { get; set; } = string.Empty;

    /// <summary>
    /// Proposal state. Stored as text so existing rows and the database itself
    /// can be read/checked without an enum mapping.
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "pending";

    /// <summary>
    /// When an undecided request stops being actionable. Approval after this
    /// instant is refused and the expiry worker moves it to <c>expired</c>.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public Guid? ResolvedByUserId { get; set; }

    /// <summary>Decision notes captured at approval/rejection (audit).</summary>
    [MaxLength(500)]
    public string? DecisionNotes { get; set; }

    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Optimistic-concurrency token: two concurrent decisions cannot both apply.</summary>
    public int Version { get; set; }
}

