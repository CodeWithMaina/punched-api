using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// One durable domain event (Phase 2): written in the SAME transaction as the
/// state change that caused it (intent-level durability, mirroring the
/// notification outbox) and relayed to in-process handlers by the relay worker.
/// <see cref="HandledByJson"/> is the per-handler checkpoint — a JSON array of
/// handler names that already ran — so replay after a crash never re-runs a
/// completed handler (handlers stay idempotent, but the checkpoint stops them
/// from being asked twice).
/// </summary>
public class DomainEvent : BaseEntity
{
    /// <summary>Dotted event type, e.g. <c>appointment.completed</c>.</summary>
    [Required]
    [MaxLength(100)]
    public string EventType { get; set; } = string.Empty;

    /// <summary>Aggregate kind (e.g. <c>appointment</c>).</summary>
    [MaxLength(40)]
    public string? AggregateType { get; set; }

    /// <summary>Aggregate id (e.g. the appointment id) — the correlation anchor.</summary>
    public Guid? AggregateId { get; set; }

    /// <summary>Tenant scope; null for platform events.</summary>
    public Guid? BusinessId { get; set; }

    /// <summary>JSON payload describing the state change.</summary>
    [Required]
    public string PayloadJson { get; set; } = "{}";

    /// <summary>UTC timestamp when the qualifying state change happened.</summary>
    public DateTime OccurredAtUtc { get; set; }

    /// <summary><c>pending</c> | <c>processing</c> | <c>completed</c> | <c>failed</c>.</summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "pending";

    /// <summary>Relay attempts so far (bounded retry).</summary>
    public int Attempts { get; set; }

    /// <summary>JSON array of handler names that already ran (checkpoint).</summary>
    [Required]
    public string HandledByJson { get; set; } = "[]";

    /// <summary>Lease timestamp while processing; stale rows are reclaimed.</summary>
    public DateTime? LockedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    [MaxLength(500)]
    public string? Error { get; set; }

    /// <summary>Last state transition timestamp (lease reclaims included).</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}