using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// One durable scheduled unit of work (Phase 2): reminder ladders, follow-ups
/// and — from Phase 3 — automation rule actions. The scheduler worker claims due
/// rows with <c>FOR UPDATE SKIP LOCKED</c>; a lease (<see cref="LockedAt"/>) plus
/// stale reclaim keep N API instances safe.
/// <see cref="DeduKey"/> is globally unique: rescheduling REWRITES the due time
/// of the existing row instead of inserting a second job for the same slot.
/// </summary>
public class ScheduledJob : BaseEntity
{
    /// <summary>Handler selector, e.g. <c>appointment.reminder</c>, <c>appointment.follow_up</c>.</summary>
    [Required]
    [MaxLength(60)]
    public string JobType { get; set; } = string.Empty;

    /// <summary>Natural dedupe key, e.g. <c>appt:{appointmentId}:r-1440m</c>.</summary>
    [Required]
    [MaxLength(200)]
    public string DedupeKey { get; set; } = string.Empty;

    /// <summary>Tenant scope; null for platform-sourced jobs.</summary>
    public Guid? BusinessId { get; set; }

    /// <summary>Aggregate this job belongs to (e.g. <c>appointment</c>), for bulk cancellation.</summary>
    [MaxLength(40)]
    public string? AggregateType { get; set; }

    /// <summary>Aggregate id (e.g. the appointment id) for bulk cancellation.</summary>
    public Guid? AggregateId { get; set; }

    /// <summary>JSON payload handed to the job handler (appointmentId, kind, ...).</summary>
    [Required]
    public string PayloadJson { get; set; } = "{}";

    /// <summary>Due time UTC; retries push this forward.</summary>
    public DateTime ScheduledForUtc { get; set; }

    /// <summary><c>pending</c> | <c>processing</c> | <c>completed</c> | <c>cancelled</c> | <c>failed</c>.</summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "pending";

    /// <summary>Delivery attempts made so far (bounded retry).</summary>
    public int Attempts { get; set; }

    /// <summary>Lease timestamp while processing; stale rows are reclaimed.</summary>
    public DateTime? LockedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }

    [MaxLength(500)]
    public string? Error { get; set; }

    /// <summary>Last state transition timestamp (lease reclaims included).</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}