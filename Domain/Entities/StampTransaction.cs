using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Immutable, auditable ledger entry for every stamp balance change.
/// This is the source of truth for loyalty activity — the mutable
/// <see cref="LoyaltyCard.TotalStamps"/> counter is a materialized convenience
/// that must always equal the sum of that card's transactions.
///
/// Transactions are never updated or deleted. Corrections are new transactions
/// with <see cref="StampTransactions.Adjustment"/> as the source.
/// </summary>
public class StampTransaction : BaseEntity
{
    /// <summary>FK to the owning business (tenant scope for every read).</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>FK to the program the stamps were earned under.</summary>
    [Required]
    public Guid ProgramId { get; set; }

    /// <summary>FK to the <see cref="LoyaltyCard"/> whose balance changed.</summary>
    [Required]
    public Guid CardId { get; set; }

    /// <summary>Denormalized FK to the customer, for customer-facing activity feeds.</summary>
    [Required]
    public Guid CustomerId { get; set; }

    /// <summary>
    /// Always positive; the sign is carried by <see cref="Direction"/>.
    /// Range 1-1000.
    /// </summary>
    [Required]
    [Range(1, 1000)]
    public int Amount { get; set; }

    /// <summary>Credit adds to the balance, Debit removes from it.</summary>
    [Required]
    public StampTransactionDirection Direction { get; set; } = StampTransactionDirection.Credit;

    /// <summary>
    /// What produced this transaction — one of <see cref="StampTransactions"/>.
    /// Max 30 characters.
    /// </summary>
    [Required]
    [MaxLength(30)]
    public string Source { get; set; } = StampTransactions.Manual;

    /// <summary>
    /// Identifier of the originating record (appointment id, referral id, …).
    /// Null for sources with no single originating record (e.g. free-form manual awards).
    /// </summary>
    public Guid? SourceId { get; set; }

    /// <summary>
    /// FK to the <see cref="LoyaltyEarningRule"/> that produced this transaction.
    /// Null for manual awards, enrollment stamps, adjustments and redemptions.
    /// </summary>
    public Guid? EarningRuleId { get; set; }

    /// <summary>True when the award was produced by automatic event processing.</summary>
    [Required]
    public bool IsAutomatic { get; set; }

    /// <summary>Human-readable reason. Required for manual awards and adjustments. Max 500 characters.</summary>
    [MaxLength(500)]
    public string? Reason { get; set; }

    /// <summary>FK to the business/staff user who performed the action. Null for system/automatic awards.</summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>Actor role at the time of the action (e.g. "Business", "Staff", "System"). Max 20 characters.</summary>
    [MaxLength(20)]
    public string? CreatedByRole { get; set; }

    /// <summary>
    /// Deterministic uniqueness token for automatic awards, shaped as
    /// <c>{programId}:{ruleId}:{source}:{sourceId}</c>. A unique index on this
    /// column makes retried domain events idempotent at the database level.
    /// Null for manual/adjustment transactions (which are intentionally repeatable).
    /// </summary>
    [MaxLength(200)]
    public string? IdempotencyKey { get; set; }

    /// <summary>Optional JSON blob for source-specific detail (service ids, note, etc.).</summary>
    public string? MetadataJson { get; set; }

    // ── Navigation ──────────────────────────────────────────
    public virtual LoyaltyProgram Program { get; set; } = null!;
    public virtual LoyaltyCard Card { get; set; } = null!;
    public virtual LoyaltyEarningRule? EarningRule { get; set; }

    /// <summary>Signed effect on the card balance (+credit / −debit).</summary>
    public int SignedAmount =>
        Direction == StampTransactionDirection.Credit ? Amount : -Amount;
}
