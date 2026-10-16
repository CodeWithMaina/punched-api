using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// One execution of an automation rule (Phase 3). Records rule version snapshot,
/// source event/job identity, produced notification intents/jobs, and error info.
/// Enables business run-history views and admin failure consoles.
/// </summary>
[Table("automation_rule_runs")]
public class AutomationRuleRun : BaseEntity
{
    /// <summary>Rule that was evaluated.</summary>
    [Required]
    public Guid RuleId { get; set; }

    /// <summary>Snapshot of the rule version at execution time.</summary>
    public int RuleVersion { get; set; }

    /// <summary>Source event type (e.g. <c>appointment.created</c>).</summary>
    [Required]
    [MaxLength(100)]
    public string SourceEventType { get; set; } = string.Empty;

    /// <summary>Source aggregate id (e.g. appointment id) — correlation anchor.</summary>
    public Guid? SourceAggregateId { get; set; }

    /// <summary>Source scheduled-job id if triggered by the scheduler.</summary>
    public Guid? SourceJobId { get; set; }

    /// <summary><c>pending</c> | <c>completed</c> | <c>failed</c>.</summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "pending";

    /// <summary>Notification intent ids created by this run (JSON array of strings).</summary>
    [Required]
    public string CreatedNotificationIntentIdsJson { get; set; } = "[]";

    /// <summary>Scheduled job ids created by this run (JSON array of strings).</summary>
    [Required]
    public string CreatedJobIdsJson { get; set; } = "[]";

    /// <summary>Error message if the run failed.</summary>
    [MaxLength(500)]
    public string? ErrorMessage { get; set; }

    /// <summary>Structured error details (JSON) for diagnosis.</summary>
    public string? ErrorDetailsJson { get; set; }

    /// <summary>Deterministic idempotency key — prevents duplicate execution.</summary>
    [Required]
    [MaxLength(200)]
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Tenant scope — denormalized for efficient tenant-scoped queries.</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>Execution start timestamp (UTC).</summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Execution completion timestamp (UTC, null if still running/failed).</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Foreign key to automation_rules.</summary>
    [ForeignKey(nameof(RuleId))]
    public virtual AutomationRule? Rule { get; set; }

    /// <summary>Foreign key to businesses.</summary>
    [ForeignKey(nameof(BusinessId))]
    public virtual Business? Business { get; set; }
}