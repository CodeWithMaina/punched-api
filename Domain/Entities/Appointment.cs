using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

public class Appointment : BaseEntity
{
    [Required]
    public Guid BusinessId { get; set; }

    [Required]
    public Guid CustomerId { get; set; }

    public Guid? StaffUserId { get; set; }

    [Required]
    public DateTime ScheduledAt { get; set; }

    [Required]
    public DateTime EndAt { get; set; }

    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "booked";

    /// <summary>
    /// Customer opt-out for scheduled reminder jobs (Phase 2 toggle). Opting out
    /// cancels the appointment's pending reminder jobs; opting back in recreates
    /// the still-future ones.
    /// </summary>
    public bool ReminderOptOut { get; set; } = false;

    /// <summary>
    /// Optimistic-concurrency token for status transitions and scheduling
    /// writes. Incremented by every accepted state change, so two concurrent
    /// transition requests for the same appointment cannot both succeed (the
    /// loser gets a concurrency conflict instead of a duplicate side effect).
    /// </summary>
    public int Version { get; set; }

    public ICollection<AppointmentResource> Resources { get; set; } = new List<AppointmentResource>();
}
