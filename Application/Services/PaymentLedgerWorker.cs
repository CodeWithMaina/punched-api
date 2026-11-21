using Microsoft.Extensions.Options;
using PunchedApi.Application.BackgroundProcessing;

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
/// <para>No connection is held while waiting: the poll sleeps between batches (adaptive,
/// jittered, and woken by new settlements) and every attempt uses a short-lived context.</para>
/// </summary>
public sealed class PaymentLedgerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PaymentLedgerOptions _options;
    private readonly ILogger<PaymentLedgerWorker> _logger;
    private readonly PollingProfile? _profile;
    private readonly BackgroundProcessingMetrics _metrics;
    private readonly IPollWakeChannel? _wakeChannel;
    private readonly int _batchSize;

    public PaymentLedgerWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<PaymentLedgerOptions> options,
        ILogger<PaymentLedgerWorker> logger,
        BackgroundProcessingOptions? backgroundProcessing = null,
        BackgroundProcessingMetrics? metrics = null,
        IPollWakeChannel? wakeChannel = null)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _profile = backgroundProcessing?.For(BackgroundProcessingOptions.Names.PaymentLedger);
        _metrics = metrics ?? BackgroundProcessingMetrics.Shared;
        _wakeChannel = wakeChannel;
        _batchSize = Math.Max(1, _profile?.BatchSize ?? _options.BatchSize);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("PaymentLedgerWorker disabled by configuration.");
            return Task.CompletedTask;
        }

        var reconcileEvery = TimeSpan.FromMinutes(Math.Max(1, _options.ReconciliationIntervalMinutes));
        var nextReconcile = _options.ReconcileOnStartup ? DateTime.UtcNow : DateTime.UtcNow.Add(reconcileEvery);

        _logger.LogInformation(
            "PaymentLedgerWorker starting: claim {BatchSize} per poll; reconcile every {ReconcileMinutes} min.",
            _batchSize, reconcileEvery.TotalMinutes);

        return AdaptivePollLoop.RunAsync(
            BackgroundProcessingOptions.Names.PaymentLedger,
            ResolveProfile(),
            _logger,
            _metrics,
            async ct =>
            {
                if (DateTime.UtcNow >= nextReconcile)
                {
                    await RunReconciliationAsync(ct);
                    nextReconcile = DateTime.UtcNow.Add(reconcileEvery);
                }

                var outcome = await ProcessBatchAsync(ct);
                return new PollWorkResult(
                    outcome.Claimed,
                    outcome.Created + outcome.AlreadyRecorded + outcome.FailedPermanently,
                    outcome.Claimed >= _batchSize);
            },
            _wakeChannel,
            stoppingToken);
    }

    private PollingProfile ResolveProfile()
    {
        if (_profile is not null) return _profile;

        var seconds = Math.Max(1, _options.PollIntervalSeconds);
        return new PollingProfile
        {
            ActiveIntervalSeconds = Math.Max(1, Math.Min(2, seconds)),
            FirstIdleIntervalSeconds = seconds,
            MaxIdleIntervalSeconds = seconds,
            BackoffMultiplier = 1,
            JitterRatio = 0,
            MaxIdleDurationSeconds = 0
        };
    }

    /// <summary>Runs one outbox batch. Public so tests can drive processing deterministically.</summary>

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

