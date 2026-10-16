using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

public class NotificationLog : BaseEntity
{
    [Required]
    public Guid UserId { get; set; }

    public Guid? BusinessId { get; set; }

    [Required]
    [MaxLength(20)]
    public string Channel { get; set; } = "email";

    [Required]
    [MaxLength(100)]
    public string TemplateType { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "sent";

    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeliveredAt { get; set; }
    public DateTime? OpenedAt { get; set; }

    /// <summary>JSON payload handed to an external channel by the delivery worker.</summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>Number of failed delivery attempts made so far.</summary>
    public int Attempts { get; set; }

    /// <summary>Timestamped channel attempt outcomes; null means legacy attempts are not fully observable.</summary>
    public string? DeliveryAttemptsJson { get; set; }

    /// <summary>Earliest UTC time at which a pending row may be claimed.</summary>
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Earliest UTC time at which this row may be delivered. A row is eligible for
    /// claiming when <c>DeliverAfterUtc &lt;= now()</c> (or null == invoice now).
    /// Absent from legacy payloads; safe to backfill as NULL.
    /// </summary>
    public DateTime? DeliverAfterUtc { get; set; }

    /// <summary>Trigger rule id pinned by the producer (Rule 3.x identity, immutable per intent).</summary>
    [MaxLength(64)]
    public string? RuleId { get; set; }

    /// <summary>Job id that sourced the intent (scheduler or automation rule run).</summary>
    [MaxLength(64)]
    public string? SourceJobId { get; set; }

    /// <summary>Terminal dead-letter timestamp, distinct from failed.</summary>
    public DateTime? DeadLetteredAt { get; set; }

    /// <summary>Codified reason for dead-lettering (classification).</summary>
    [MaxLength(200)]
    public string? DeadLetterReason { get; set; }

    /// <summary>Template key pinned at intent time (e.g. 'platform|default|appointment.reminder').</summary>
    [MaxLength(64)]
    public string? TemplateKey { get; set; }

    /// <summary>Caller key or deterministic natural key used to collapse duplicate sends.</summary>
    [MaxLength(200)]
    public string? IdempotencyKey { get; set; }

    /// <summary>Last ledger state transition timestamp.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(500)]
    public string? Error { get; set; }
}
