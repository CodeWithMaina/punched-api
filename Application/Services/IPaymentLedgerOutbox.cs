using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// The durable bridge between "the payment settled" and "the books say so".
///
/// <para>Every settlement path calls <see cref="EnsureObligationAsync"/> <b>inside the same
/// transaction</b> as the payment's settled status, so the obligation and the settlement commit
/// together. The processor then turns each obligation into exactly one income row; the worker
/// retries anything the request could not finish.</para>
/// </summary>
public interface IPaymentLedgerOutbox
{
    /// <summary>
    /// Records that a settled payment still needs its ledger row. Idempotent and non-throwing under
    /// a duplicate: the unique <c>payment_id</c> constraint decides, not an existence check.
    /// Returns true when this call created the obligation.
    /// </summary>
    Task<bool> EnsureObligationAsync(Payment payment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims due work for one batch, marking it <c>processing</c> under a lease. Uses
    /// <c>FOR UPDATE SKIP LOCKED</c> on PostgreSQL so several API instances never claim the same row.
    /// </summary>
    Task<IReadOnlyList<PaymentLedgerClaim>> ClaimDueAsync(int batchSize, CancellationToken cancellationToken = default);

    /// <summary>Returns leases abandoned by a crashed worker to the queue.</summary>
    Task<int> ReclaimStaleAsync(TimeSpan age, CancellationToken cancellationToken = default);

    /// <summary>Marks an obligation complete — only after its income row was committed.</summary>
    Task<bool> MarkCompletedAsync(Guid obligationId, Guid financialTransactionId, CancellationToken cancellationToken = default);

    /// <summary>Records a failed attempt with its class, reason and the next time to try (null = final).</summary>
    Task MarkFailureAsync(
        Guid obligationId,
        int attempts,
        string failureKind,
        string errorCode,
        string error,
        DateTime? nextAttemptAt,
        CancellationToken cancellationToken = default);

    /// <summary>Operator recovery: puts a failed obligation back in the queue with a fresh attempt budget.</summary>
    Task<bool> RequeueAsync(Guid obligationId, Guid? businessId, CancellationToken cancellationToken = default);

    /// <summary>Completes an obligation whose income already exists (idempotent repair).</summary>
    Task<bool> MarkCompletedIfIncomeExistsAsync(Guid obligationId, CancellationToken cancellationToken = default);

    Task<PaymentLedgerSummary> GetSummaryAsync(Guid? businessId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Settled payments with neither an income row nor an obligation — the repair candidates a
    /// reconciliation pass turns back into work. Bounded and oldest-first.
    /// </summary>
    Task<IReadOnlyList<Payment>> FindSettledPaymentsMissingObligationAsync(
        int take, Guid? businessId = null, CancellationToken cancellationToken = default);

    Task<int> CountSettledPaymentsMissingObligationAsync(
        Guid? businessId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PaymentLedgerObligationView>> ListAsync(
        PaymentLedgerFilter filter,
        CancellationToken cancellationToken = default);

    Task<int> CountAsync(PaymentLedgerFilter filter, CancellationToken cancellationToken = default);

    Task<PaymentLedgerObligationView?> GetViewAsync(Guid obligationId, CancellationToken cancellationToken = default);
}

/// <summary>One claimed unit of work. Deliberately small: the processor reloads what it needs.</summary>
public sealed record PaymentLedgerClaim(Guid ObligationId, Guid PaymentId, Guid BusinessId, int Attempts);

public sealed record PaymentLedgerSummary(
    int Pending,
    int Processing,
    int Failed,
    int Completed,
    int SettledPaymentsMissingIncome,
    int ObligationsWithoutIncome,
    int OldestPendingAgeMinutes);

public sealed record PaymentLedgerFilter(
    Guid? BusinessId = null,
    string? Status = null,
    bool OnlyProblems = false,
    int Page = 1,
    int PageSize = 25);

/// <summary>
/// An operational view of one obligation. Contains identifiers and timing — never M-Pesa messages,
/// tokens or customer contact details — because this is a diagnostics surface, not a payment screen.
/// </summary>
public sealed record PaymentLedgerObligationView(
    Guid Id,
    Guid PaymentId,
    Guid BusinessId,
    string? PaymentReference,
    decimal PaymentAmount,
    string PaymentStatus,
    string Status,
    int Attempts,
    DateTime CreatedAt,
    DateTime? LastAttemptAt,
    DateTime NextAttemptAt,
    DateTime? CompletedAt,
    DateTime? LockedAt,
    Guid? FinancialTransactionId,
    string? FailureKind,
    string? LastErrorCode,
    string? LastError,
    int AgeMinutes);
