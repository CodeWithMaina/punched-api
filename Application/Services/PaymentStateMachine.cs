namespace PunchedApi.Application.Services;

using PunchedApi.Domain.Entities;

/// <summary>
/// Payment lifecycle state machine. Every status change must go through
/// <see cref="Transition"/> — arbitrary status changes are rejected. Attempting an
/// invalid transition throws <see cref="InvalidPaymentTransitionException"/>.
///
/// Transition table (who / event / retryable / terminal / reversible):
/// ─────────────────────────────────────────────────────────────────────────────
/// CREATED → AWAITING_CUSTOMER  STK push initiated (customer/provider) — retryable via new attempt, not terminal, not reversible
/// CREATED → SUCCESS            Authorised staff/business confirms CASH received — not retryable, terminal, reversible
/// CREATED → CANCELLED          Customer/staff/business cancels before paying — terminal
/// CREATED → SUBMITTED          Manual M-PESA: customer pastes a valid confirmation SMS — not terminal
/// AWAITING_CUSTOMER → PROCESSING  Callback received (any result) — system
/// AWAITING_CUSTOMER → SUCCESS     Callback ResultCode=0 — terminal, reversible
/// AWAITING_CUSTOMER → FAILED      Callback error / STK rejected — retryable
/// AWAITING_CUSTOMER → EXPIRED     STK timeout with no callback — retryable
/// AWAITING_CUSTOMER → CANCELLED   Customer cancels / explicit cancel endpoint — terminal
/// AWAITING_CUSTOMER → SUBMITTED   Manual M-PESA: a message arrived while the STK was still open
/// PROCESSING → SUCCESS / FAILED   Callback fully applied — system
/// FAILED → AWAITING_CUSTOMER   Manual or automatic retry (new attempt) — not terminal
/// FAILED → SUBMITTED           Manual M-PESA: customer pastes a confirmation after a failure
/// EXPIRED → AWAITING_CUSTOMER  Retry after expiry — not terminal
/// SUBMITTED → UNDER_REVIEW     Owner opens the submission — non-terminal holding state
/// SUBMITTED → VERIFIED         Owner confirms the money arrived — terminal, reversible
/// SUBMITTED → REJECTED         Owner refuses the proof — terminal until a new submission
/// SUBMITTED → EXPIRED          Nobody acted within the verification window — terminal
/// UNDER_REVIEW → VERIFIED      Owner confirms after reviewing — terminal, reversible
/// UNDER_REVIEW → REJECTED      Owner refuses after reviewing — terminal until a new submission
/// UNDER_REVIEW → EXPIRED       Review window elapsed — terminal
/// REJECTED → SUBMITTED         Customer pastes a NEW confirmation for the same payment
/// SUCCESS → REVERSED           Daraja reversal applied (authorised only) — terminal
/// SUCCESS → REFUNDED           Refund completed (authorised only) — terminal
/// VERIFIED → REVERSED          A verified manual payment is later reversed (authorised only) — terminal
/// Terminal states: SUCCESS/VERIFIED (unless reversed/refunded), REJECTED, CANCELLED, REVERSED, REFUNDED.
/// REVERSED/REFUNDED can never go back to SUCCESS. A settled payment can never
/// return to CREATED/AWAITING_CUSTOMER/FAILED.
/// </summary>
public static class PaymentStateMachine
{
    private static readonly Dictionary<PaymentStatus, HashSet<PaymentStatus>> Allowed = new()
    {
        [PaymentStatus.Created] = new HashSet<PaymentStatus>
        {
            PaymentStatus.AwaitingCustomer, PaymentStatus.Processing, PaymentStatus.Success,
            PaymentStatus.Cancelled, PaymentStatus.Failed, PaymentStatus.Submitted
        },
        [PaymentStatus.AwaitingCustomer] = new HashSet<PaymentStatus>
        {
            PaymentStatus.Processing, PaymentStatus.Success, PaymentStatus.Failed,
            PaymentStatus.Expired, PaymentStatus.Cancelled, PaymentStatus.Submitted
        },
        [PaymentStatus.Processing] = new HashSet<PaymentStatus> { PaymentStatus.Success, PaymentStatus.Failed },
        [PaymentStatus.Failed] = new HashSet<PaymentStatus> { PaymentStatus.AwaitingCustomer, PaymentStatus.Submitted },
        [PaymentStatus.Expired] = new HashSet<PaymentStatus> { PaymentStatus.AwaitingCustomer },
        // Manual M-PESA verification loop. SUBMITTED is deliberately NOT terminal:
        // a submission is a claim, and a claim must be able to be decided.
        [PaymentStatus.Submitted] = new HashSet<PaymentStatus>
        {
            PaymentStatus.UnderReview, PaymentStatus.Verified, PaymentStatus.Rejected, PaymentStatus.Expired
        },
        [PaymentStatus.UnderReview] = new HashSet<PaymentStatus>
        {
            PaymentStatus.Verified, PaymentStatus.Rejected, PaymentStatus.Expired
        },
        // A rejected payment may be re-submitted with a different receipt — the
        // customer paid again, and refusing to record it would lose the money.
        [PaymentStatus.Rejected] = new HashSet<PaymentStatus> { PaymentStatus.Submitted },
        [PaymentStatus.Success] = new HashSet<PaymentStatus> { PaymentStatus.Reversed, PaymentStatus.Refunded },
        [PaymentStatus.Verified] = new HashSet<PaymentStatus> { PaymentStatus.Reversed, PaymentStatus.Refunded },
        [PaymentStatus.Cancelled] = new HashSet<PaymentStatus>(),
        [PaymentStatus.Reversed] = new HashSet<PaymentStatus>(),
        [PaymentStatus.Refunded] = new HashSet<PaymentStatus>()
    };

    /// <summary>Is the transition from → to allowed?</summary>
    public static bool CanTransition(PaymentStatus from, PaymentStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Apply a validated transition. Throws when the transition is not allowed.</summary>
    public static void Transition(Domain.Entities.Payment payment, PaymentStatus to)
    {
        if (!CanTransition(payment.Status, to))
            throw new InvalidPaymentTransitionException(payment.Status, to);

        payment.Status = to;
        switch (to)
        {
            case PaymentStatus.Success:
                payment.CompletedAt ??= DateTime.UtcNow;
                payment.FailedAt = null;
                break;
            case PaymentStatus.Verified:
                // A verified manual payment is settled money, so it stamps the same
                // completion instant a provider-confirmed payment does.
                payment.CompletedAt ??= DateTime.UtcNow;
                payment.VerifiedAt ??= DateTime.UtcNow;
                payment.FailedAt = null;
                break;
            case PaymentStatus.Rejected:
                payment.RejectedAt = DateTime.UtcNow;
                break;
            case PaymentStatus.Failed:
                payment.FailedAt = DateTime.UtcNow;
                break;
            case PaymentStatus.Cancelled:
                payment.CancelledAt = DateTime.UtcNow;
                break;
            case PaymentStatus.Reversed:
            case PaymentStatus.Refunded:
                payment.ReversedAt = DateTime.UtcNow;
                break;
        }
    }
}

/// <summary>Thrown when an invalid payment status transition is attempted. Mapped to HTTP 409.</summary>
public class InvalidPaymentTransitionException : InvalidOperationException
{
    public InvalidPaymentTransitionException(PaymentStatus from, PaymentStatus to)
        : base($"Invalid payment transition: {from} → {to}.")
    {
        From = from;
        To = to;
    }

    public PaymentStatus From { get; }
    public PaymentStatus To { get; }
}
