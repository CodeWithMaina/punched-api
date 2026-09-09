using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// A customer-initiated rescheduling proposal for an existing appointment.
/// The appointment is NOT modified until a business owner approves the request.
/// </summary>
public class RescheduleRequest : BaseEntity
{
    [Required]
    public Guid AppointmentId { get; set; }

    /// <summary>The customer who proposed the change.</summary>
    [Required]
    public Guid RequestedByUserId { get; set; }

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

    /// <summary>pending | approved | rejected</summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "pending";

    public DateTime? ResolvedAt { get; set; }

    public Guid? ResolvedByUserId { get; set; }
}
