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
    Refunded = 8
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
    DarajaC2B = 2
}
