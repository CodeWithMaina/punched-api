using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Append-only audit trail for the financial domain: who changed what, when, and what
/// the values were before and after. Rows are never updated or deleted, so a later
/// correction cannot erase the fact that the earlier value existed — which is exactly
/// what "historical financial changes are auditable" requires.
/// </summary>
public class FinancialAuditLog : BaseEntity
{
    /// <summary>Tenant scope; mandatory for isolation.</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>The kind of record acted on: <c>transaction</c> | <c>expense</c> | <c>refund</c> | <c>reconciliation</c> | <c>income</c>.</summary>
    [Required]
    [MaxLength(40)]
    public string EntityType { get; set; } = string.Empty;

    /// <summary>The record's id (the ledger row, statement import or statement line).</summary>
    [Required]
    public Guid EntityId { get; set; }

    /// <summary>The action taken: <c>created</c> | <c>updated</c> | <c>verified</c> | <c>rejected</c> | <c>voided</c> | <c>refunded</c> | <c>reconciled</c> | <c>imported</c>.</summary>
    [Required]
    [MaxLength(40)]
    public string Action { get; set; } = string.Empty;

    /// <summary>Who acted. Null for system-generated entries (e.g. an automatic match).</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Actor's display name captured at the time.</summary>
    [MaxLength(100)]
    public string? ActorName { get; set; }

    /// <summary>Human-readable summary of the change.</summary>
    [MaxLength(500)]
    public string? Note { get; set; }

    /// <summary>Before/after values as JSON, so a diff is reconstructable without replaying the service.</summary>
    public string? ChangesJson { get; set; }

    /// <summary>Immutable ledger reference at the time of the action (kept for denormalised reporting).</summary>
    [MaxLength(40)]
    public string? Reference { get; set; }
}
