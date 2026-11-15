using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// <b>The business's financial ledger.</b> One row per money movement the business
/// records — income (from Punched or from outside it), expenses, refunds and
/// adjustments — so the cashbook, the overview and the analytics all read ONE source of
/// truth instead of adding up several.
///
/// <para><b>Relationship with <see cref="Payment"/>.</b> A payment is an obligation
/// being <i>collected</i> for an appointment; a financial transaction is money that has
/// been <i>recognised</i> in the business's books. They are joined by
/// <see cref="SourcePaymentId"/>, which is unique: a settled payment produces exactly
/// one income row however many times the lifecycle event fires, so a payment can never
/// be counted twice. Pending and rejected payments produce no row — they appear only as
/// pending totals derived from the payments table.</para>
///
/// <para><b>Nothing is silently rewritten or deleted.</b> An expense is edited only
/// through the audited expense path, and anything that must be undone is voided
/// (excluded from totals, still visible) rather than removed.</para>
/// </summary>
public class FinancialTransaction : BaseEntity
{
    /// <summary>FK to the owning business (tenant scope; mandatory for isolation).</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>Income, expense, refund or adjustment.</summary>
    [Required]
    public FinancialTransactionType Type { get; set; } = FinancialTransactionType.Income;

    /// <summary>Where the business transaction came from (appointment, walk-in, product, manual, other).</summary>
    [Required]
    public TransactionSource Source { get; set; } = TransactionSource.ManualIncome;

    /// <summary>How the money moved (Till, Paybill, Send Money, cash, bank, card, other).</summary>
    [Required]
    public FinancialPaymentMethod PaymentMethod { get; set; } = FinancialPaymentMethod.Cash;

    /// <summary>Whether the money is confirmed. Only Verified counts as revenue.</summary>
    [Required]
    public FinancialVerificationStatus VerificationStatus { get; set; } = FinancialVerificationStatus.Pending;

    /// <summary>
    /// Statement reconciliation state — deliberately independent of verification:
    /// confirming a pasted M-Pesa message does not reconcile it against the real statement.
    /// </summary>
    [Required]
    public ReconciliationStatus ReconciliationStatus { get; set; } = ReconciliationStatus.Unreconciled;

    /// <summary>Amount in major units (KES), 2dp. Always positive; <see cref="Type"/> carries the direction.</summary>
    [Required]
    [Range(0.01, 100_000_000)]
    public decimal Amount { get; set; }

    /// <summary>ISO-4217 currency. MVP is KES only.</summary>
    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "KES";

    /// <summary>Punched-issued ledger reference (<c>FIN-…</c>). Immutable.</summary>
    [Required]
    [MaxLength(40)]
    public string Reference { get; set; } = string.Empty;

    /// <summary>External evidence reference: an M-Pesa receipt code, bank reference or card slip number.</summary>
    [MaxLength(100)]
    public string? ExternalReference { get; set; }

    /// <summary>Expense category key (see <see cref="ExpenseCategories"/>). Null for income and refunds.</summary>
    [MaxLength(40)]
    public string? Category { get; set; }

    /// <summary>What the transaction was for.</summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>The customer this money relates to, when known. Null for anonymous walk-ins.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>Customer name captured at the time, so history survives a rename.</summary>
    [MaxLength(200)]
    public string? CustomerName { get; set; }

    /// <summary>The appointment this income fulfils, when it came from Punched.</summary>
    public Guid? AppointmentId { get; set; }

    /// <summary>
    /// The payment this row recognises. Unique — the idempotency key that guarantees a
    /// settled payment is recognised exactly once. Null for standalone income.
    /// </summary>
    public Guid? SourcePaymentId { get; set; }

    /// <summary>For a refund: the income row being refunded. Never null on a refund.</summary>
    public Guid? RefundOfTransactionId { get; set; }

    /// <summary>For a refund: why the money went back.</summary>
    [MaxLength(500)]
    public string? RefundReason { get; set; }

    /// <summary>For a refund: the refund's own reference (M-Pesa code, bank reference).</summary>
    [MaxLength(100)]
    public string? RefundReference { get; set; }

    /// <summary>
    /// The business date the money moved. Unlike <see cref="BaseEntity.CreatedAt"/> this is
    /// what the owner filters and reports on.
    /// </summary>
    [Required]
    public DateTime TransactionDate { get; set; }

    /// <summary>UTC time the row was recorded in Punched.</summary>
    [Required]
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC time the money was confirmed (null while pending).</summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>Who confirmed the money (owner/staff). Null while pending.</summary>
    public Guid? VerifiedByUserId { get; set; }

    /// <summary>Why a pending row was refused. Null unless rejected.</summary>
    [MaxLength(500)]
    public string? RejectionReason { get; set; }

    /// <summary>Who recorded the row (owner/staff). Null for system-generated rows.</summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>UTC time the row was last updated (expense edit, verification, reconciliation).</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ── Voiding (financial records are never silently deleted) ──

    /// <summary>True when voided: excluded from every total, still visible in history.</summary>
    public bool IsVoided { get; set; }

    /// <summary>UTC time the row was voided.</summary>
    public DateTime? VoidedAt { get; set; }

    /// <summary>Who voided it.</summary>
    public Guid? VoidedByUserId { get; set; }

    /// <summary>Why it was voided.</summary>
    [MaxLength(500)]
    public string? VoidReason { get; set; }

    // ── Reconciliation ──────────────────────────────────────

    /// <summary>UTC time the row was reconciled against a statement line.</summary>
    public DateTime? ReconciledAt { get; set; }

    /// <summary>Who reconciled it (null when matched automatically at import).</summary>
    public Guid? ReconciledByUserId { get; set; }

    /// <summary>The imported statement line this row was matched to, when matched.</summary>
    public Guid? StatementLineId { get; set; }

    /// <summary>Note recorded with the reconciliation decision (or the detected discrepancy).</summary>
    [MaxLength(500)]
    public string? ReconciliationNote { get; set; }

    // ── Navigation ──────────────────────────────────────────
    public virtual Business Business { get; set; } = null!;
}
