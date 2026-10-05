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
/// AWAITING_CUSTOMER → PROCESSING  Callback received (any result) — system
/// AWAITING_CUSTOMER → SUCCESS     Callback ResultCode=0 — terminal, reversible
/// AWAITING_CUSTOMER → FAILED      Callback error / STK rejected — retryable
/// AWAITING_CUSTOMER → EXPIRED     STK timeout with no callback — retryable
/// AWAITING_CUSTOMER → CANCELLED   Customer cancels / explicit cancel endpoint — terminal
/// PROCESSING → SUCCESS / FAILED   Callback fully applied — system
/// FAILED → AWAITING_CUSTOMER   Manual or automatic retry (new attempt) — not terminal
/// EXPIRED → AWAITING_CUSTOMER  Retry after expiry — not terminal
/// SUCCESS → REVERSED           Daraja reversal applied (authorised only) — terminal
/// SUCCESS → REFUNDED           Refund completed (authorised only) — terminal
/// Terminal states: SUCCESS (unless reversed/refunded), CANCELLED, REVERSED, REFUNDED.
/// REVERSED/REFUNDED can never go back to SUCCESS. A SUCCESS payment can never
/// return to CREATED/AWAITING_CUSTOMER/FAILED.
/// </summary>
public static class PaymentStateMachine
{
    private static readonly Dictionary<PaymentStatus, HashSet<PaymentStatus>> Allowed = new()
    {
        [PaymentStatus.Created] = new HashSet<PaymentStatus>
        {
            PaymentStatus.AwaitingCustomer, PaymentStatus.Processing, PaymentStatus.Success,
            PaymentStatus.Cancelled, PaymentStatus.Failed
        },
        [PaymentStatus.AwaitingCustomer] = new HashSet<PaymentStatus>
        {
            PaymentStatus.Processing, PaymentStatus.Success, PaymentStatus.Failed,
            PaymentStatus.Expired, PaymentStatus.Cancelled
        },
        [PaymentStatus.Processing] = new HashSet<PaymentStatus> { PaymentStatus.Success, PaymentStatus.Failed },
        [PaymentStatus.Failed] = new HashSet<PaymentStatus> { PaymentStatus.AwaitingCustomer },
        [PaymentStatus.Expired] = new HashSet<PaymentStatus> { PaymentStatus.AwaitingCustomer },
        [PaymentStatus.Success] = new HashSet<PaymentStatus> { PaymentStatus.Reversed, PaymentStatus.Refunded },
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
