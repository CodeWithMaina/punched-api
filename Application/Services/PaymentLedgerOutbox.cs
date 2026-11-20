using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <inheritdoc />
public sealed class PaymentLedgerOutbox : IPaymentLedgerOutbox
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<PaymentLedgerOutbox> _logger;

    public PaymentLedgerOutbox(ApplicationDbContext context, ILogger<PaymentLedgerOutbox> logger)
    {
        _context = context;
        _logger = logger;
    }

    private bool IsPostgres =>
        _context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    private bool IsRelational => _context.Database.IsRelational();

    /// <summary>
    /// Inserts the obligation if (and only if) this payment does not have one yet.
    ///
    /// <para>PostgreSQL/SQLite: a single <c>INSERT … ON CONFLICT (payment_id) DO NOTHING</c>, so two
    /// concurrent settlements cannot both believe they created it, and the loser does not fail the
    /// surrounding settlement transaction. The row count is the answer.</para>
    /// </summary>
    public async Task<bool> EnsureObligationAsync(Payment payment, CancellationToken cancellationToken = default)
    {
        if (!PaymentStatuses.IsSettled(payment.Status))
            return false;

        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();

        if (IsRelational)
        {
            var inserted = await _context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO payment_ledger_obligations
                    (id, payment_id, business_id, status, attempts, next_attempt_at, created_at, updated_at)
                VALUES
                    ({id}, {payment.Id}, {payment.BusinessId},
                     {PaymentLedgerObligationStatus.Pending}, {0}, {now}, {now}, {now})
                ON CONFLICT (payment_id) DO NOTHING
                """, cancellationToken);

            if (inserted > 0)
            {
                _logger.LogInformation(
                    "Ledger obligation {ObligationId} created for payment {PaymentId} (business {BusinessId}).",
                    id, payment.Id, payment.BusinessId);
            }

            return inserted > 0;
        }

        // Test-only in-memory provider: there is no SQL engine, so the duplicate check is an
        // existence check followed by a tracked insert. Production correctness is the unique index.
        var exists = await _context.PaymentLedgerObligations
            .AnyAsync(o => o.PaymentId == payment.Id, cancellationToken);

        if (exists) return false;

        await _context.PaymentLedgerObligations.AddAsync(new PaymentLedgerObligation
        {
            Id = id,
            PaymentId = payment.Id,
            BusinessId = payment.BusinessId,
            Status = PaymentLedgerObligationStatus.Pending,
            Attempts = 0,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now
        }, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<PaymentLedgerClaim>> ClaimDueAsync(
        int batchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0) return Array.Empty<PaymentLedgerClaim>();

        if (IsPostgres) return await ClaimPostgresAsync(batchSize, cancellationToken);
        return await ClaimPortableAsync(batchSize, cancellationToken);
    }

    /// <summary>
    /// The production path: skip rows another instance has locked, lease the ones we get, all inside
    /// one short transaction. <c>SKIP LOCKED</c> is what makes multiple API instances safe.
    /// </summary>
    private async Task<IReadOnlyList<PaymentLedgerClaim>> ClaimPostgresAsync(
        int batchSize, CancellationToken cancellationToken)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        var connection = _context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        var claimed = new List<PaymentLedgerClaim>(batchSize);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = """
                SELECT id, payment_id, business_id, attempts
                FROM payment_ledger_obligations
                WHERE status = 'pending'
                  AND next_attempt_at <= now()
                ORDER BY next_attempt_at
                LIMIT @take
                FOR UPDATE SKIP LOCKED;
                """;
            command.Parameters.Add(new NpgsqlParameter("take", batchSize));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add(new PaymentLedgerClaim(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt32(3)));
            }
        }

        if (claimed.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return claimed;
        }

        var now = DateTime.UtcNow;
        var ids = claimed.Select(c => c.ObligationId).ToList();

        await _context.PaymentLedgerObligations
            .Where(o => ids.Contains(o.Id) && o.Status == PaymentLedgerObligationStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, PaymentLedgerObligationStatus.Processing)
                .SetProperty(o => o.LockedAt, now)
                .SetProperty(o => o.LastAttemptAt, now)
                .SetProperty(o => o.UpdatedAt, now), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    /// <summary>
    /// SQLite/InMemory fallback for tests and local runs. A single process claims a batch; there is
    /// no <c>SKIP LOCKED</c>, which is exactly why production uses PostgreSQL.
    /// </summary>
    private async Task<IReadOnlyList<PaymentLedgerClaim>> ClaimPortableAsync(
        int batchSize, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var rows = await _context.PaymentLedgerObligations
            .AsNoTracking()
            .Where(o => o.Status == PaymentLedgerObligationStatus.Pending && o.NextAttemptAt <= now)
            .OrderBy(o => o.NextAttemptAt)
            .Take(batchSize)
            .Select(o => new { o.Id, o.PaymentId, o.BusinessId, o.Attempts })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0) return Array.Empty<PaymentLedgerClaim>();

        var ids = rows.Select(r => r.Id).ToList();
        await _context.PaymentLedgerObligations
            .Where(o => ids.Contains(o.Id) && o.Status == PaymentLedgerObligationStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, PaymentLedgerObligationStatus.Processing)
                .SetProperty(o => o.LockedAt, now)
                .SetProperty(o => o.LastAttemptAt, now)
                .SetProperty(o => o.UpdatedAt, now), cancellationToken);

        return rows.Select(r => new PaymentLedgerClaim(r.Id, r.PaymentId, r.BusinessId, r.Attempts)).ToList();
    }

    public async Task<int> ReclaimStaleAsync(TimeSpan age, CancellationToken cancellationToken = default)
    {
        var threshold = DateTime.UtcNow.Subtract(age);
        var now = DateTime.UtcNow;

        var reclaimed = await _context.PaymentLedgerObligations
            .Where(o => o.Status == PaymentLedgerObligationStatus.Processing
                        && o.LockedAt != null && o.LockedAt <= threshold)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, PaymentLedgerObligationStatus.Pending)
                .SetProperty(o => o.LockedAt, (DateTime?)null)
                .SetProperty(o => o.NextAttemptAt, now)
                .SetProperty(o => o.UpdatedAt, now), cancellationToken);

        if (reclaimed > 0)
        {
            _logger.LogWarning(
                "Reclaimed {Count} stale payment-ledger obligation lease(s) older than {Age}.",
                reclaimed, age);
        }

        return reclaimed;
    }


    public async Task<bool> MarkCompletedAsync(
        Guid obligationId, Guid financialTransactionId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var updated = await _context.PaymentLedgerObligations
            .Where(o => o.Id == obligationId && o.Status != PaymentLedgerObligationStatus.Completed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, PaymentLedgerObligationStatus.Completed)
                .SetProperty(o => o.Attempts, o => o.Attempts + 1)
                .SetProperty(o => o.LastAttemptAt, now)
                .SetProperty(o => o.CompletedAt, now)
                .SetProperty(o => o.FinancialTransactionId, financialTransactionId)
                .SetProperty(o => o.LockedAt, (DateTime?)null)
                .SetProperty(o => o.FailureKind, (string?)null)
                .SetProperty(o => o.LastErrorCode, (string?)null)
                .SetProperty(o => o.LastError, (string?)null)
                .SetProperty(o => o.UpdatedAt, now), cancellationToken);

        return updated > 0;
    }

    public async Task MarkFailureAsync(
        Guid obligationId,
        int attempts,
        string failureKind,
        string errorCode,
        string error,
        DateTime? nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var boundedError = error.Length <= 500 ? error : error[..500];
        // A final failure parks the row as `failed`; a retryable one returns it to `pending` with a
        // backoff. Completed rows are never regressed — financial history does not move backwards.
        var status = nextAttemptAt is null
            ? PaymentLedgerObligationStatus.Failed
            : PaymentLedgerObligationStatus.Pending;

        await _context.PaymentLedgerObligations
            .Where(o => o.Id == obligationId && o.Status != PaymentLedgerObligationStatus.Completed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, status)
                .SetProperty(o => o.Attempts, attempts)
                .SetProperty(o => o.LastAttemptAt, now)
                .SetProperty(o => o.FailureKind, failureKind)
                .SetProperty(o => o.LastErrorCode, errorCode)
                .SetProperty(o => o.LastError, boundedError)
                .SetProperty(o => o.NextAttemptAt, nextAttemptAt ?? now)
                .SetProperty(o => o.LockedAt, (DateTime?)null)
                .SetProperty(o => o.UpdatedAt, now), cancellationToken);
    }

    public async Task<bool> RequeueAsync(
        Guid obligationId, Guid? businessId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var updated = await _context.PaymentLedgerObligations
            .Where(o => o.Id == obligationId
                        && o.Status != PaymentLedgerObligationStatus.Completed
                        && (businessId == null || o.BusinessId == businessId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, PaymentLedgerObligationStatus.Pending)
                .SetProperty(o => o.Attempts, 0)
                .SetProperty(o => o.NextAttemptAt, now)
                .SetProperty(o => o.LockedAt, (DateTime?)null)
                .SetProperty(o => o.FailureKind, (string?)null)
                .SetProperty(o => o.LastErrorCode, (string?)null)
                .SetProperty(o => o.LastError, (string?)null)
                .SetProperty(o => o.UpdatedAt, now), cancellationToken);

        if (updated > 0)
        {
            _logger.LogWarning(
                "Payment-ledger obligation {ObligationId} requeued for retry{Scope}.",
                obligationId, businessId is Guid id ? $" (business {id})" : string.Empty);
        }

        return updated > 0;
    }

    public async Task<bool> MarkCompletedIfIncomeExistsAsync(
        Guid obligationId, CancellationToken cancellationToken = default)
    {
        var obligation = await _context.PaymentLedgerObligations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == obligationId, cancellationToken);

        if (obligation == null || obligation.Status == PaymentLedgerObligationStatus.Completed)
            return false;

        var income = await _context.FinancialTransactions
            .AsNoTracking()
            .Where(t => t.SourcePaymentId == obligation.PaymentId)
            .Select(t => new { t.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (income == null) return false;

        return await MarkCompletedAsync(obligationId, income.Id, cancellationToken);
    }


    public async Task<int> CountAsync(PaymentLedgerFilter filter, CancellationToken cancellationToken = default)
    {
        var query = ScopedObligations(filter.BusinessId);

        if (filter.OnlyProblems)
            query = query.Where(o => o.Status == PaymentLedgerObligationStatus.Failed);
        else if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(o => o.Status == filter.Status);

        return await query.CountAsync(cancellationToken);
    }

    public async Task<PaymentLedgerSummary> GetSummaryAsync(
        Guid? businessId, CancellationToken cancellationToken = default)
    {
        var obligations = ScopedObligations(businessId);

        var counts = await obligations
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int Count(string status) => counts.FirstOrDefault(c => c.Status == status)?.Count ?? 0;

        var oldestPending = await obligations
            .Where(o => o.Status == PaymentLedgerObligationStatus.Pending)
            .OrderBy(o => o.CreatedAt)
            .Select(o => (DateTime?)o.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var settledMissingIncome = await SettledPaymentsMissingIncome(businessId)
            .CountAsync(cancellationToken);

        // Completed obligations whose income row is gone would mean financial history was deleted
        // outside the application. Reported, never repaired automatically.
        var completedWithoutIncome = await obligations
            .Where(o => o.Status == PaymentLedgerObligationStatus.Completed
                        && !_context.FinancialTransactions.Any(t => t.SourcePaymentId == o.PaymentId))
            .CountAsync(cancellationToken);

        return new PaymentLedgerSummary(
            Pending: Count(PaymentLedgerObligationStatus.Pending),
            Processing: Count(PaymentLedgerObligationStatus.Processing),
            Failed: Count(PaymentLedgerObligationStatus.Failed),
            Completed: Count(PaymentLedgerObligationStatus.Completed),
            SettledPaymentsMissingIncome: settledMissingIncome,
            ObligationsWithoutIncome: completedWithoutIncome,
            OldestPendingAgeMinutes: oldestPending is DateTime oldest
                ? Math.Max(0, (int)(DateTime.UtcNow - oldest).TotalMinutes)
                : 0);
    }

    public async Task<IReadOnlyList<PaymentLedgerObligationView>> ListAsync(
        PaymentLedgerFilter filter, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);

        var query = ScopedObligations(filter.BusinessId);

        if (filter.OnlyProblems)
            query = query.Where(o => o.Status == PaymentLedgerObligationStatus.Failed);
        else if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(o => o.Status == filter.Status);

        var now = DateTime.UtcNow;

        // Projection stays provider-agnostic: the age is computed in memory rather than with a
        // provider-specific date function, so the same query works on PostgreSQL and SQLite.
        var rows = await query
            .OrderBy(o => o.Status == PaymentLedgerObligationStatus.Failed ? 0 : 1)
            .ThenBy(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new
            {
                o.Id,
                o.PaymentId,
                o.BusinessId,
                PaymentReference = o.Payment.Reference,
                PaymentAmount = o.Payment.Amount,
                PaymentStatus = o.Payment.Status,
                ObligationStatus = o.Status,
                o.Attempts,
                o.CreatedAt,
                o.LastAttemptAt,
                o.NextAttemptAt,
                o.CompletedAt,
                o.LockedAt,
                o.FinancialTransactionId,
                o.FailureKind,
                o.LastErrorCode,
                o.LastError
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(o => new PaymentLedgerObligationView(
                o.Id,
                o.PaymentId,
                o.BusinessId,
                o.PaymentReference,
                o.PaymentAmount,
                PaymentStatusName(o.PaymentStatus),
                o.ObligationStatus,
                o.Attempts,
                o.CreatedAt,
                o.LastAttemptAt,
                o.NextAttemptAt,
                o.CompletedAt,
                o.LockedAt,
                o.FinancialTransactionId,
                o.FailureKind,
                o.LastErrorCode,
                o.LastError,
                AgeMinutes(o.CreatedAt, now)))
            .ToList();
    }

    public async Task<PaymentLedgerObligationView?> GetViewAsync(
        Guid obligationId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var row = await _context.PaymentLedgerObligations
            .AsNoTracking()
            .Where(o => o.Id == obligationId)
            .Select(o => new
            {
                o.Id,
                o.PaymentId,
                o.BusinessId,
                PaymentReference = o.Payment.Reference,
                PaymentAmount = o.Payment.Amount,
                PaymentStatus = o.Payment.Status,
                ObligationStatus = o.Status,
                o.Attempts,
                o.CreatedAt,
                o.LastAttemptAt,
                o.NextAttemptAt,
                o.CompletedAt,
                o.LockedAt,
                o.FinancialTransactionId,
                o.FailureKind,
                o.LastErrorCode,
                o.LastError
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row == null) return null;

        return new PaymentLedgerObligationView(
            row.Id,
            row.PaymentId,
            row.BusinessId,
            row.PaymentReference,
            row.PaymentAmount,
            PaymentStatusName(row.PaymentStatus),
            row.ObligationStatus,
            row.Attempts,
            row.CreatedAt,
            row.LastAttemptAt,
            row.NextAttemptAt,
            row.CompletedAt,
            row.LockedAt,
            row.FinancialTransactionId,
            row.FailureKind,
            row.LastErrorCode,
            row.LastError,
            AgeMinutes(row.CreatedAt, now));
    }

    private static string PaymentStatusName(PaymentStatus status) => status switch
    {
        PaymentStatus.Success => "success",
        PaymentStatus.Verified => "verified",
        _ => status.ToString().ToLowerInvariant()
    };

    private static int AgeMinutes(DateTime createdAt, DateTime now) =>
        Math.Max(0, (int)(now - createdAt).TotalMinutes);

    /// <summary>Settled payments the books have not recognised yet and no obligation covers.</summary>
    public async Task<int> CountSettledPaymentsMissingObligationAsync(
        Guid? businessId, CancellationToken cancellationToken = default) =>
        await SettledPaymentsMissingIncome(businessId).CountAsync(cancellationToken);

    public async Task<IReadOnlyList<Payment>> FindSettledPaymentsMissingObligationAsync(
        int take, Guid? businessId = null, CancellationToken cancellationToken = default)
    {
        return await SettledPaymentsMissingIncome(businessId)
            .OrderBy(p => p.CompletedAt ?? p.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);
    }

    private IQueryable<Payment> SettledPaymentsMissingIncome(Guid? businessId)
    {
        var payments = _context.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Success || p.Status == PaymentStatus.Verified);

        if (businessId is Guid id)
            payments = payments.Where(p => p.BusinessId == id);

        return payments.Where(p =>
            !_context.FinancialTransactions.Any(t => t.SourcePaymentId == p.Id)
            && !_context.PaymentLedgerObligations.Any(o => o.PaymentId == p.Id));
    }

    private IQueryable<PaymentLedgerObligation> ScopedObligations(Guid? businessId)
    {
        var query = _context.PaymentLedgerObligations.AsNoTracking();
        return businessId is Guid id ? query.Where(o => o.BusinessId == id) : query;
    }
}

