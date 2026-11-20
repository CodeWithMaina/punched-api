namespace PunchedApi.Application.Services;

/// <summary>What one processing attempt achieved.</summary>
public enum LedgerProcessOutcome
{
    /// <summary>The income row was created by this attempt.</summary>
    Created,

    /// <summary>The income row already existed (replay, race, or reconciliation).</summary>
    AlreadyRecorded,

    /// <summary>No obligation exists for the payment (reconciliation will create one).</summary>
    NoObligation,

    /// <summary>The payment no longer exists — needs investigation.</summary>
    PaymentMissing,

    /// <summary>The payment is not settled, so it is not income (yet).</summary>
    PaymentNotSettled,

    /// <summary>A transient failure was recorded and a retry was scheduled.</summary>
    RetryScheduled,

    /// <summary>The attempt failed in a way that needs a human; no automatic retry.</summary>
    FailedPermanently
}

public sealed record LedgerProcessResult(
    LedgerProcessOutcome Outcome,
    Guid PaymentId,
    Guid? FinancialTransactionId = null,
    string? ErrorCode = null,
    string? Error = null);

/// <summary>
/// Turns one obligation into exactly one verified income row, or records why it could not.
///
/// <para>This is the only place the retry policy is decided: transient failures back off and return
/// to the queue; permanent ones (invalid amount, missing payment, non-settled payment) stop and stay
/// visible. The income row is written <i>before</i> the obligation is completed, so a crash in
/// between causes a harmless replay rather than a completed obligation with no income.</para>
/// </summary>
public interface IPaymentLedgerProcessor
{
    Task<LedgerProcessResult> ProcessAsync(Guid paymentId, CancellationToken cancellationToken = default);
}
