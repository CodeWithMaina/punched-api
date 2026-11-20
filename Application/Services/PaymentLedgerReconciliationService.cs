using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <inheritdoc />
public sealed class PaymentLedgerReconciliationService : IPaymentLedgerReconciliationService
{
    private readonly ApplicationDbContext _context;
    private readonly IPaymentLedgerOutbox _outbox;
    private readonly ILogger<PaymentLedgerReconciliationService> _logger;

    public PaymentLedgerReconciliationService(
        ApplicationDbContext context,
        IPaymentLedgerOutbox outbox,
        ILogger<PaymentLedgerReconciliationService> logger)
    {
        _context = context;
        _outbox = outbox;
        _logger = logger;
    }

    public async Task<PaymentLedgerReconciliationResult> ReconcileAsync(
        int batchSize, CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        var take = Math.Clamp(batchSize, 1, 500);

        // 1) Settled payments with no income and no obligation. This is the repair path for a crash
        // between settlement and obligation insert (or for payments settled before this table existed).
        var missing = await _outbox.FindSettledPaymentsMissingObligationAsync(take, null, cancellationToken);

        var created = 0;
        foreach (var payment in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _outbox.EnsureObligationAsync(payment, cancellationToken))
                created += 1;
        }

        // 2) Obligations whose income exists but which were never marked complete (a crash after the
        // income commit and before completion). Bounded, and only rows that are not already complete.
        var openWithIncome = await _context.FinancialTransactions
            .AsNoTracking()
            .Where(t => t.SourcePaymentId != null)
            .Select(t => t.SourcePaymentId!.Value)
            .Distinct()
            .Join(
                _context.PaymentLedgerObligations.AsNoTracking()
                    .Where(o => o.Status != PaymentLedgerObligationStatus.Completed),
                paymentId => paymentId,
                obligation => obligation.PaymentId,
                (paymentId, obligation) => obligation.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        var completed = 0;
        foreach (var obligationId in openWithIncome)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _outbox.MarkCompletedIfIncomeExistsAsync(obligationId, cancellationToken))
                completed += 1;
        }

        // 3) Read-only anomaly counts. These are reported, never "fixed": deleting or rewriting
        // financial history to make a report look clean is the one thing a ledger must not do.
        var duplicateGroups = await CountDuplicateIncomeGroupsAsync(null, cancellationToken);
        var orphans = await CountOrphanedIncomeRowsAsync(null, cancellationToken);
        var stillMissing = await _outbox.CountSettledPaymentsMissingObligationAsync(null, cancellationToken);
        var failed = await _context.PaymentLedgerObligations
            .CountAsync(o => o.Status == PaymentLedgerObligationStatus.Failed, cancellationToken);

        var finished = DateTime.UtcNow;

        _logger.LogInformation(
            "Payment-ledger reconciliation finished in {Elapsed} ms: created {Created} obligation(s), completed {Completed}, {StillMissing} settled payment(s) still without income, {Duplicates} duplicate-income group(s), {Orphans} orphaned income row(s), {Failed} failed obligation(s).",
            (int)(finished - started).TotalMilliseconds, created, completed, stillMissing, duplicateGroups, orphans, failed);

        return new PaymentLedgerReconciliationResult(
            created, completed, stillMissing, duplicateGroups, orphans, failed, started, finished);
    }

    public async Task<PaymentLedgerConsistencyReport> GetConsistencyReportAsync(
        Guid? businessId, int sampleSize = 5, CancellationToken cancellationToken = default)
    {
        var samples = new List<PaymentLedgerAnomalySample>();
        var take = Math.Clamp(sampleSize, 1, 25);

        var settledMissing = await _outbox.FindSettledPaymentsMissingObligationAsync(take, businessId, cancellationToken);

        var settledMissingCount = await _outbox.CountSettledPaymentsMissingObligationAsync(businessId, cancellationToken);

        foreach (var payment in settledMissing)
        {
            samples.Add(new PaymentLedgerAnomalySample(
                "settled_without_income", payment.Id, payment.Reference,
                $"Business {payment.BusinessId}"));
        }

        // Income rows whose payment is no longer settled (rejected/reversed status but verified income).
        var unsettledRows = _context.FinancialTransactions.AsNoTracking()
            .Where(t => t.SourcePaymentId != null && t.Type == FinancialTransactionType.Income)
            .Where(t => !_context.Payments.Any(p =>
                p.Id == t.SourcePaymentId &&
                (p.Status == PaymentStatus.Success || p.Status == PaymentStatus.Verified)));

        if (businessId is Guid scoped)
            unsettledRows = unsettledRows.Where(t => t.BusinessId == scoped);

        var unsettledCount = await unsettledRows.CountAsync(cancellationToken);
        foreach (var row in await unsettledRows
            .OrderByDescending(t => t.TransactionDate)
            .Take(take)
            .Select(t => new { t.Id, t.Reference, t.SourcePaymentId, t.BusinessId })
            .ToListAsync(cancellationToken))
        {
            samples.Add(new PaymentLedgerAnomalySample(
                "income_for_unsettled_payment", row.SourcePaymentId, row.Reference,
                $"Transaction {row.Id} (business {row.BusinessId})"));
        }

        var duplicates = await DuplicateIncomeGroupsAsync(businessId, take, cancellationToken);
        foreach (var duplicate in duplicates)
        {
            samples.Add(new PaymentLedgerAnomalySample(
                "duplicate_income", duplicate.PaymentId, null,
                $"{duplicate.Count} income rows"));
        }

        var orphanLimit = take;
        var orphanRows = _context.FinancialTransactions.AsNoTracking()
            .Where(t => t.SourcePaymentId != null
                        && !_context.Payments.Any(p => p.Id == t.SourcePaymentId));

        if (businessId is Guid orphanScope)
            orphanRows = orphanRows.Where(t => t.BusinessId == orphanScope);

        var orphanCount = await orphanRows.CountAsync(cancellationToken);
        foreach (var row in await orphanRows
            .OrderByDescending(t => t.TransactionDate)
            .Take(orphanLimit)
            .Select(t => new { t.Id, t.Reference, t.SourcePaymentId, t.BusinessId })
            .ToListAsync(cancellationToken))
        {
            samples.Add(new PaymentLedgerAnomalySample(
                "orphaned_income", row.SourcePaymentId, row.Reference,
                $"Transaction {row.Id} (business {row.BusinessId})"));
        }

        var summary = await _outbox.GetSummaryAsync(businessId, cancellationToken);
        var duplicateCount = await CountDuplicateIncomeGroupsAsync(businessId, cancellationToken);

        return new PaymentLedgerConsistencyReport(
            summary,
            settledMissingCount,
            unsettledCount,
            duplicateCount,
            orphanCount,
            samples);
    }

    /// <summary>Income rows sharing one source payment — historical data damage the report must surface.</summary>
    private async Task<int> CountDuplicateIncomeGroupsAsync(Guid? businessId, CancellationToken cancellationToken)
    {
        var query = IncomeByPaymentQuery(businessId);
        var groups = await query
            .GroupBy(t => t.SourcePaymentId)
            .Select(g => new { Count = g.Count() })
            .Where(g => g.Count > 1)
            .CountAsync(cancellationToken);
        return groups;
    }

    private async Task<IReadOnlyList<(Guid PaymentId, int Count)>> DuplicateIncomeGroupsAsync(
        Guid? businessId, int take, CancellationToken cancellationToken)
    {
        var query = IncomeByPaymentQuery(businessId);
        var groups = await query
            .GroupBy(t => t.SourcePaymentId)
            .Select(g => new { PaymentId = g.Key, Count = g.Count() })
            .Where(g => g.Count > 1)
            .OrderByDescending(g => g.Count)
            .Take(take)
            .ToListAsync(cancellationToken);

        return groups
            .Where(g => g.PaymentId != null)
            .Select(g => (g.PaymentId!.Value, g.Count))
            .ToList();
    }

    private async Task<int> CountOrphanedIncomeRowsAsync(Guid? businessId, CancellationToken cancellationToken)
    {
        var query = _context.FinancialTransactions.AsNoTracking()
            .Where(t => t.SourcePaymentId != null
                        && !_context.Payments.Any(p => p.Id == t.SourcePaymentId));

        if (businessId is Guid scoped)
            query = query.Where(t => t.BusinessId == scoped);

        return await query.CountAsync(cancellationToken);
    }

    private IQueryable<FinancialTransaction> IncomeByPaymentQuery(Guid? businessId)
    {
        var query = _context.FinancialTransactions.AsNoTracking()
            .Where(t => t.SourcePaymentId != null && t.Type == FinancialTransactionType.Income);

        if (businessId is Guid scoped)
            query = query.Where(t => t.BusinessId == scoped);

        return query;
    }
}

