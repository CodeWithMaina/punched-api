using Microsoft.Extensions.Options;

namespace PunchedApi.Application.Services;

public sealed record PaymentLedgerBatchOutcome(
    int Claimed,
    int Created,
    int AlreadyRecorded,
    int RetryScheduled,
    int FailedPermanently);

/// <summary>
/// The durable safety net behind every settlement.
///
/// <para>Each poll reclaims abandoned leases, claims a bounded batch of due obligations
/// (<c>FOR UPDATE SKIP LOCKED</c> on PostgreSQL, so several API instances can run this at once) and
/// processes each row in its own scope. A row that fails cannot take the batch down with it, and the
/// next poll picks up whatever backoff has made due. A separate, slower reconciliation pass repairs
/// obligations that were never written (crash between settlement and obligation insert) and reports
/// historical inconsistencies without touching them.</para>
///
/// <para>No connection is held while waiting: the poll sleeps between batches and every attempt uses
/// a short-lived context.</para>
/// </summary>
public sealed class PaymentLedgerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PaymentLedgerOptions _options;
    private readonly ILogger<PaymentLedgerWorker> _logger;

    public PaymentLedgerWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<PaymentLedgerOptions> options,
        ILogger<PaymentLedgerWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("PaymentLedgerWorker disabled by configuration.");
            return;
        }

        var poll = TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds));
        var reconcileEvery = TimeSpan.FromMinutes(Math.Max(1, _options.ReconciliationIntervalMinutes));
        var nextReconcile = _options.ReconcileOnStartup ? DateTime.UtcNow : DateTime.UtcNow.Add(reconcileEvery);

        _logger.LogInformation(
            "PaymentLedgerWorker started. Claim {BatchSize} every {PollInterval}s; reconcile every {ReconcileMinutes} min.",
            _options.BatchSize, poll.TotalSeconds, reconcileEvery.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow >= nextReconcile)
                {
                    await RunReconciliationAsync(stoppingToken);
                    nextReconcile = DateTime.UtcNow.Add(reconcileEvery);
                }

                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must survive anything the database throws at it: the obligation rows are
                // durable, so the work is still there when the next poll succeeds.
                _logger.LogError(ex, "Payment-ledger worker cycle failed; outstanding obligations remain queued.");
            }

            try
            {
                await Task.Delay(poll, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("PaymentLedgerWorker stopped.");
    }

    /// <summary>Runs one outbox batch. Public so tests can drive processing deterministically.</summary>
    public async Task<PaymentLedgerBatchOutcome> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PaymentLedgerClaim> claims;

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IPaymentLedgerOutbox>();

            await outbox.ReclaimStaleAsync(
                TimeSpan.FromMinutes(Math.Max(1, _options.StaleProcessingMinutes)), cancellationToken);

            claims = await outbox.ClaimDueAsync(Math.Max(1, _options.BatchSize), cancellationToken);
        }

        var created = 0;
        var already = 0;
        var retried = 0;
        var failed = 0;

        foreach (var claim in claims)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // One scope per row: an exception or poisoned DbContext cannot strand the rest of the batch.
                await using var rowScope = _scopeFactory.CreateAsyncScope();
                var processor = rowScope.ServiceProvider.GetRequiredService<IPaymentLedgerProcessor>();
                var result = await processor.ProcessAsync(claim.PaymentId, cancellationToken);

                switch (result.Outcome)
                {
                    case LedgerProcessOutcome.Created: created += 1; break;
                    case LedgerProcessOutcome.AlreadyRecorded: already += 1; break;
                    case LedgerProcessOutcome.RetryScheduled: retried += 1; break;
                    case LedgerProcessOutcome.FailedPermanently: failed += 1; break;
                    default:
                        _logger.LogWarning(
                            "Ledger obligation {ObligationId} for payment {PaymentId} produced {Outcome}.",
                            claim.ObligationId, claim.PaymentId, result.Outcome);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The lease will expire and the row will be reclaimed; nothing is lost.
                _logger.LogError(ex,
                    "Processing ledger obligation {ObligationId} for payment {PaymentId} threw; it will be reclaimed after the lease expires.",
                    claim.ObligationId, claim.PaymentId);
            }
        }

        if (claims.Count > 0)
        {
            _logger.LogInformation(
                "Payment-ledger batch: {Claimed} claimed, {Created} created, {Already} already recorded, {Retried} rescheduled, {Failed} parked.",
                claims.Count, created, already, retried, failed);
        }

        return new PaymentLedgerBatchOutcome(claims.Count, created, already, retried, failed);
    }

    /// <summary>Runs one reconciliation pass. Public so tests and operators can trigger it.</summary>
    public async Task<PaymentLedgerReconciliationResult> RunReconciliationAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reconciliation = scope.ServiceProvider.GetRequiredService<IPaymentLedgerReconciliationService>();
        return await reconciliation.ReconcileAsync(
            Math.Max(1, _options.ReconciliationBatchSize), cancellationToken);
    }
}

