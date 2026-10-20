using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Durable analytics backfill execution record. One row per (run group, business).
/// A null <see cref="BusinessId"/> row is the parent coordinator for an
/// all-businesses run; per-business child rows carry the actual checkpoints.
/// Checkpoint (<see cref="LastCompletedDate"/>) only advances after the
/// aggregate writes for that date are committed, so resume never skips
/// uncommitted work and reruns stay idempotent (replaceable aggregates).
/// </summary>
public class AnalyticsBackfillRun : BaseEntity
{
    /// <summary>Stable run group shared by all business rows of one invocation.</summary>
    [Required]
    public Guid RunGroupId { get; set; }

    /// <summary>Null on the parent coordinator row; set on per-business rows.</summary>
    public Guid? BusinessId { get; set; }

    [Required]
    public DateOnly FromDate { get; set; }

    [Required]
    public DateOnly ToDate { get; set; }

    /// <summary>Pending | Running | Completed | Failed | Cancelled.</summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "Pending";

    /// <summary>Last date whose aggregates are durably committed. Null = none yet.</summary>
    public DateOnly? LastCompletedDate { get; set; }

    public int TotalDays { get; set; }
    public int CompletedDays { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? LastProgressAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    [MaxLength(2000)]
    public string? Error { get; set; }
}
