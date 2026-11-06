namespace PunchedApi.Domain.Entities;

/// <summary>
/// Payment lifecycle status. Transitions are governed by
/// <see cref="PunchedApi.Application.Services.PaymentStateMachine"/> — never set
/// this property directly from a controller; go through the state machine.
/// </summary>
public enum PaymentStatus
{
    /// <summary>Payment record created, no attempt started yet (e.g. cash: awaiting physical money).</summary>
    Created = 0,

    /// <summary>STK push sent; customer is looking at the M-PESA prompt (or C2B payment is awaited).</summary>
    AwaitingCustomer = 1,

    /// <summary>A provider confirmation (callback) has been received and is being processed.</summary>
    Processing = 2,

    /// <summary>Terminal success. Money received by the business.</summary>
    Success = 3,

    /// <summary>Attempt failed but the payment can be retried (new attempt).</summary>
    Failed = 4,

    /// <summary>Terminal: no provider interaction within the allowed window (STK not completed in time).</summary>
    Expired = 5,

    /// <summary>Terminal: explicitly cancelled before payment.</summary>
    Cancelled = 6,

    /// <summary>Terminal: a Safaricom reversal completed against a successful payment.</summary>
    Reversed = 7,

    /// <summary>Terminal: a refund against a successful payment completed.</summary>
    Refunded = 8,

    /// <summary>
    /// Manual M-PESA: the customer pasted their confirmation SMS. The message has
    /// been parsed and accepted, and the payment is now queued for the business to
    /// verify. <b>Never</b> treated as money received.
    /// </summary>
    Submitted = 9,

    /// <summary>
    /// Manual M-PESA: the business has opened the submission for review. A
    /// non-terminal holding state so a queue can distinguish "untouched" from
    /// "being looked at" without a second table.
    /// </summary>
    UnderReview = 10,

    /// <summary>
    /// Terminal: the business confirmed the money arrived. This is the manual
    /// M-PESA equivalent of <see cref="Success"/> — it settles the appointment and
    /// releases commission exactly as <see cref="Success"/> does.
    /// </summary>
    Verified = 11,

    /// <summary>
    /// Terminal: the business rejected the submitted proof (wrong amount, unknown
    /// transaction, reused code). The reason is recorded and the customer may
    /// submit again with a new payment.
    /// </summary>
    Rejected = 12
}

/// <summary>
/// Status predicates that are easy to get wrong when they are repeated inline.
/// </summary>
public static class PaymentStatuses
{
    /// <summary>
    /// True when the business actually holds the money. Both the provider-confirmed
    /// (<see cref="PaymentStatus.Success"/>) and the owner-verified
    /// (<see cref="PaymentStatus.Verified"/>) paths count: the customer paid either
    /// way, and every aggregate, commission release and receipt reads this rule.
    /// </summary>
    public static bool IsSettled(PaymentStatus status) =>
        status is PaymentStatus.Success or PaymentStatus.Verified;

    /// <summary>True while the payment is waiting on a human decision.</summary>
    public static bool IsAwaitingVerification(PaymentStatus status) =>
        status is PaymentStatus.Submitted or PaymentStatus.UnderReview;

    /// <summary>True when the customer may paste a (new) M-PESA confirmation for this payment.</summary>
    public static bool CanAcceptSubmission(PaymentStatus status) =>
        status is PaymentStatus.Created or PaymentStatus.AwaitingCustomer
            or PaymentStatus.Failed or PaymentStatus.Rejected;

    /// <summary>Wire value (snake_case) for a status.</summary>
    public static string ToWire(PaymentStatus status) =>
        status switch
        {
            PaymentStatus.AwaitingCustomer => "awaiting_customer",
            PaymentStatus.UnderReview => "under_review",
            _ => status.ToString().ToLowerInvariant()
        };
}

/// <summary>What the payment is for.</summary>
public enum PaymentType
{
    /// <summary>Full payment for the appointment's services.</summary>
    FullPayment = 0,

    /// <summary>Upfront booking fee (partial payment on booking).</summary>
    BookingFee = 1,

    /// <summary>Payment against an outstanding balance.</summary>
    BalancePayment = 2
}

/// <summary>How the customer pays. Cash needs no provider; M-PESA goes through the business's own account.</summary>
public enum PaymentMethod
{
    Cash = 0,
    Mpesa = 1
}

/// <summary>The concrete provider channel used for an attempt.</summary>
public enum PaymentProviderKind
{
    /// <summary>No provider — physical cash handled by staff.</summary>
    Cash = 0,

    /// <summary>Daraja STK Push (M-PESA Express) against the business's PayBill or Till.</summary>
    DarajaStk = 1,

    /// <summary>Daraja C2B — customer pays the business's PayBill/Till from their own M-PESA menu.</summary>
    DarajaC2B = 2,

    /// <summary>
    /// Manual M-PESA — the customer pays the business's PayBill/Till themselves and
    /// pastes the confirmation SMS. No provider callback exists for this channel:
    /// the business owner is the authority, which is why a submission can only ever
    /// reach <see cref="PaymentStatus.Verified"/> through an explicit human decision.
    /// </summary>
    ManualMpesa = 3
}
