namespace PunchedApi.Domain.Entities;

/// <summary>
/// The durable obligation to represent one settled payment as exactly one verified income row in
/// the business ledger (the "cashbook").
///
/// <para><b>Why this table exists.</b> A payment can settle in four places — an owner verifying a
/// pasted M-Pesa message, staff confirming cash, an STK callback, a C2B confirmation — and the
/// ledger write that must follow is a <i>separate</i> aggregate. Writing it "afterwards, best
/// effort" leaves a settled payment with no income if the process dies, the database blips, or the
/// insert fails; the money is real and the books would never say so. This row is the durable
/// promise that the ledger work still needs doing, committed in the <b>same transaction</b> as the
/// payment's settled status.</para>
///
/// <para><b>Uniqueness is the idempotency guarantee.</b> <c>payment_id</c> is unique, so a replayed
/// approval, two concurrent approvals, a retried worker or a reconciliation pass can never create a
/// second obligation. The ledger row itself is likewise unique per payment
/// (<c>financial_transactions.source_payment_id</c>), so the effect — not just the intent — is
/// exactly-once.</para>
///
/// <para><b>Rows are never deleted.</b> A completed obligation is the audit trail of how a payment
/// became income; a failed one is an operational item. Both stay, with attempts, timing and the
/// last failure recorded.</para>
/// </summary>
public class PaymentLedgerObligation : BaseEntity
{
    /// <summary>The settled payment this obligation recognises. Unique across the table.</summary>
    public Guid PaymentId { get; set; }

    /// <summary>Tenant scope, copied from the payment so operational views stay tenant-safe.</summary>
    public Guid BusinessId { get; set; }

    /// <summary><c>pending</c> | <c>processing</c> | <c>completed</c> | <c>failed</c>.</summary>
    public string Status { get; set; } = PaymentLedgerObligationStatus.Pending;

    /// <summary>Total processing attempts (reset only by an explicit operator retry).</summary>
    public int Attempts { get; set; }

    /// <summary>When the obligation was last claimed/attempted (UTC).</summary>
    public DateTime? LastAttemptAt { get; set; }

    /// <summary>Earliest time the worker may claim this row again (backoff).</summary>
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;

    /// <summary>Lease taken by the worker while processing; stale leases are reclaimed.</summary>
    public DateTime? LockedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>The income row this obligation produced, once it exists.</summary>
    public Guid? FinancialTransactionId { get; set; }

    /// <summary>
    /// Machine-readable failure class — <c>transient</c> (infrastructure, retryable),
    /// <c>permanent</c> (invalid financial data / payment state: needs investigation),
    /// <c>conflict</c> (a unique-index race resolved in someone else's favour).
    /// </summary>
    public string? FailureKind { get; set; }

    /// <summary>Short machine code for the last failure (never a stack trace).</summary>
    public string? LastErrorCode { get; set; }

    /// <summary>Operator-readable last failure, bounded. Never contains customer data.</summary>
    public string? LastError { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual Payment Payment { get; set; } = null!;
    public virtual Business Business { get; set; } = null!;
}

/// <summary>The obligation lifecycle names, in one place.</summary>
public static class PaymentLedgerObligationStatus
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>Why an obligation stopped being processable automatically.</summary>
public static class PaymentLedgerFailureKind
{
    /// <summary>Infrastructure/transient: retried with backoff.</summary>
    public const string Transient = "transient";

    /// <summary>Invalid data or payment state: never retried automatically.</summary>
    public const string Permanent = "permanent";

    /// <summary>A unique-index race that another writer won (the row exists).</summary>
    public const string Conflict = "conflict";
}
