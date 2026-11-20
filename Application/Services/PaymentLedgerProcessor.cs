using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <inheritdoc />
public sealed class PaymentLedgerProcessor : IPaymentLedgerProcessor
{
    private readonly ApplicationDbContext _context;
    private readonly IFinancialLedgerService _ledger;
    private readonly IPaymentLedgerOutbox _outbox;
    private readonly PaymentLedgerOptions _options;
    private readonly ILogger<PaymentLedgerProcessor> _logger;

    public PaymentLedgerProcessor(
        ApplicationDbContext context,
        IFinancialLedgerService ledger,
        IPaymentLedgerOutbox outbox,
        IOptions<PaymentLedgerOptions> options,
        ILogger<PaymentLedgerProcessor> logger)
    {
        _context = context;
        _ledger = ledger;
        _outbox = outbox;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<LedgerProcessResult> ProcessAsync(Guid paymentId, CancellationToken cancellationToken = default)
    {
        var obligation = await _context.PaymentLedgerObligations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.PaymentId == paymentId, cancellationToken);

        if (obligation == null)
            return new LedgerProcessResult(LedgerProcessOutcome.NoObligation, paymentId);

        // Already done: replaying the worker (or a replayed settlement) must be a no-op.
        if (obligation.Status == PaymentLedgerObligationStatus.Completed)
            return new LedgerProcessResult(
                LedgerProcessOutcome.AlreadyRecorded, paymentId, obligation.FinancialTransactionId);

        var payment = await _context.Payments
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

        if (payment == null)
            return await FailAsync(obligation, "PAYMENT_NOT_FOUND",
                "The payment this obligation refers to no longer exists.", permanent: true, cancellationToken);

        if (!PaymentStatuses.IsSettled(payment.Status))
        {
            // A claim that was later rejected/reversed does not become income. This is not an outage;
            // it needs a human if the status looks wrong. Reconciliation re-queues it if the payment
            // later settles.
            return await FailAsync(obligation, "PAYMENT_NOT_SETTLED",
                "The payment is not in a settled state, so it is not income.", permanent: true, cancellationToken);
        }

        var result = await _ledger.TryEnsureIncomeForPaymentAsync(payment, cancellationToken);

        if (result.Succeeded)
        {
            var transaction = result.Transaction!;
            var completed = await _outbox.MarkCompletedAsync(obligation.Id, transaction.Id, cancellationToken);

            if (completed)
            {
                _logger.LogInformation(
                    "Payment {PaymentId} (business {BusinessId}) recognised as income {TransactionId} ({Reference}); obligation {ObligationId} completed.",
                    paymentId, payment.BusinessId, transaction.Id, transaction.Reference, obligation.Id);
            }
            else
            {
                // Someone else completed the obligation first — the outcome is identical.
                _logger.LogInformation(
                    "Payment {PaymentId} already had income {TransactionId}; obligation {ObligationId} was already completed.",
                    paymentId, transaction.Id, obligation.Id);
            }

            return new LedgerProcessResult(
                result.AlreadyExisted ? LedgerProcessOutcome.AlreadyRecorded : LedgerProcessOutcome.Created,
                paymentId,
                transaction.Id);
        }

        var attempts = obligation.Attempts + 1;

        if (!result.Transient)
        {
            // Invalid financial data: retrying cannot fix it, and retrying forever would hide it.
            await _outbox.MarkFailureAsync(
                obligation.Id,
                attempts: attempts,
                failureKind: PaymentLedgerFailureKind.Permanent,
                errorCode: result.FailureCode ?? "PERMANENT_FAILURE",
                error: result.FailureMessage ?? "The ledger row could not be created.",
                nextAttemptAt: null,
                cancellationToken);

            _logger.LogError(
                "Payment {PaymentId} (business {BusinessId}) cannot be recognised in the ledger: {Code}. Obligation {ObligationId} needs investigation.",
                paymentId, payment.BusinessId, result.FailureCode, obligation.Id);

            return new LedgerProcessResult(
                LedgerProcessOutcome.FailedPermanently, paymentId, ErrorCode: result.FailureCode,
                Error: result.FailureMessage);
        }

        var exhausted = attempts >= Math.Max(1, _options.MaxAttempts);
        DateTime? nextAttempt = exhausted ? null : DateTime.UtcNow.Add(BackoffFor(attempts));

        await _outbox.MarkFailureAsync(
            obligation.Id,
            attempts: attempts,
            failureKind: PaymentLedgerFailureKind.Transient,
            errorCode: result.FailureCode ?? "TRANSIENT_FAILURE",
            error: result.FailureMessage ?? "The ledger write failed and will be retried.",
            nextAttemptAt: nextAttempt,
            cancellationToken);

        if (exhausted)
        {
            _logger.LogError(
                "Payment {PaymentId} (business {BusinessId}) exhausted {Attempts} ledger attempts with {Code}; obligation {ObligationId} is parked for investigation.",
                paymentId, payment.BusinessId, attempts, result.FailureCode, obligation.Id);

            return new LedgerProcessResult(
                LedgerProcessOutcome.FailedPermanently, paymentId, ErrorCode: result.FailureCode,
                Error: result.FailureMessage);
        }

        _logger.LogWarning(
            "Ledger write for payment {PaymentId} (business {BusinessId}) failed transiently ({Code}); attempt {Attempt}/{Max}, next try at {NextAttempt:o}.",
            paymentId, payment.BusinessId, result.FailureCode, attempts, _options.MaxAttempts, nextAttempt);

        return new LedgerProcessResult(
            LedgerProcessOutcome.RetryScheduled, paymentId, ErrorCode: result.FailureCode,
            Error: result.FailureMessage);
    }

    /// <summary>Exponential backoff, bounded: 15 s, 30 s, 1 min … capped at the configured maximum.</summary>
    private TimeSpan BackoffFor(int attempts)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 10);
        var seconds = (long)Math.Max(1, _options.BaseBackoffSeconds) * (1L << exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, Math.Max(1, _options.MaxBackoffSeconds)));
    }

    private async Task<LedgerProcessResult> FailAsync(
        PaymentLedgerObligation obligation,
        string code,
        string message,
        bool permanent,
        CancellationToken cancellationToken)
    {
        await _outbox.MarkFailureAsync(
            obligation.Id,
            attempts: obligation.Attempts + 1,
            failureKind: permanent ? PaymentLedgerFailureKind.Permanent : PaymentLedgerFailureKind.Transient,
            errorCode: code,
            error: message,
            nextAttemptAt: permanent ? null : DateTime.UtcNow.Add(BackoffFor(obligation.Attempts + 1)),
            cancellationToken);

        _logger.LogWarning(
            "Ledger obligation {ObligationId} for payment {PaymentId} stopped: {Code}.",
            obligation.Id, obligation.PaymentId, code);

        return new LedgerProcessResult(
            permanent ? LedgerProcessOutcome.FailedPermanently : LedgerProcessOutcome.RetryScheduled,
            obligation.PaymentId,
            ErrorCode: code,
            Error: message);
    }
}

