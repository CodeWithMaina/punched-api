namespace PunchedApi.Application.Services;

/// <summary>
/// The safety net: finds settled payments the books have not recognised and hands them back to the
/// normal outbox worker. It never writes income itself, never deletes anything, and is safe to run
/// concurrently on several API instances (every repair is an idempotent insert or conditional update).
/// </summary>
public interface IPaymentLedgerReconciliationService
{
    /// <summary>One bounded reconciliation pass. Returns what it found and what it queued.</summary>
    Task<PaymentLedgerReconciliationResult> ReconcileAsync(int batchSize, CancellationToken cancellationToken = default);

    /// <summary>A read-only consistency report for authorized operators.</summary>
    Task<PaymentLedgerConsistencyReport> GetConsistencyReportAsync(
        Guid? businessId, int sampleSize = 5, CancellationToken cancellationToken = default);
}

public sealed record PaymentLedgerReconciliationResult(
    int ObligationsCreated,
    int ObligationsCompleted,
    int ObligationsRequeued,
    int SettledPaymentsWithoutIncome,
    int DuplicateIncomeGroups,
    int OrphanedIncomeRows,
    int FailedObligations,
    DateTime StartedAt,
    DateTime FinishedAt);

public sealed record PaymentLedgerAnomalySample(
    string Kind,
    Guid? PaymentId,
    string? Reference,
    string? Detail);

public sealed record PaymentLedgerConsistencyReport(
    PaymentLedgerSummary Summary,
    int SettledPaymentsWithoutIncome,
    int LedgerRowsForUnsettledPayments,
    int DuplicateIncomeGroups,
    int OrphanedIncomeRows,
    IReadOnlyList<PaymentLedgerAnomalySample> Samples);
