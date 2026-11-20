using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ── Requests ─────────────────────────────────────────────

/// <summary>Create a payment for an appointment. Amount is computed server-side — NEVER sent by the client.</summary>
public class CreatePaymentRequest
{
    [JsonPropertyName("appointmentId")]
    public Guid AppointmentId { get; set; }

    /// <summary>"cash" or "mpesa".</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "cash";

    /// <summary>"full_payment" | "booking_fee" | "balance_payment". Defaults to full_payment.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "full_payment";

    /// <summary>M-PESA phone (07XX / 2547XX). Required for method=mpesa.</summary>
    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }

    /// <summary>
    /// Optional cash amount (major units), used ONLY for a cash <c>balance_payment</c>:
    /// the operator states how much cash they actually took, so a partial collection is
    /// possible. The figure is never trusted — the server rejects it unless it is greater
    /// than zero and no larger than the balance it computed itself, so overpayment and
    /// client-invented money are both impossible. Omit it to collect the whole balance.
    /// </summary>
    [JsonPropertyName("amount")]
    public decimal? Amount { get; set; }

    /// <summary>Optional idempotency key (client-generated).</summary>
    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }
}

public class ConfirmCashRequest
{
    /// <summary>Required audit note explaining the confirmation (who took the money, etc.).</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>
    /// What the operator actually counted, in major units. Omit to confirm exactly what
    /// the payment is for. A smaller figure records a partial collection and leaves the
    /// rest outstanding as its own payment; a larger figure is refused.
    /// </summary>
    [JsonPropertyName("amount")]
    public decimal? Amount { get; set; }
}

public class ReversePaymentRequest
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

public class SavePaymentConfigRequest
{
    [JsonPropertyName("cashEnabled")]
    public bool CashEnabled { get; set; } = true;

    [JsonPropertyName("mpesaEnabled")]
    public bool MpesaEnabled { get; set; }

    /// <summary>"paybill", "till" or "send_money" — the single method the business accepts.</summary>
    [JsonPropertyName("mpesaAccountType")]
    public string MpesaAccountType { get; set; } = "paybill";

    [JsonPropertyName("mpesaShortCode")]
    public string? MpesaShortCode { get; set; }

    /// <summary>Daraja consumer key. Null/omitted keeps the existing value.</summary>
    [JsonPropertyName("consumerKey")]
    public string? ConsumerKey { get; set; }

    [JsonPropertyName("consumerSecret")]
    public string? ConsumerSecret { get; set; }

    /// <summary>STK passkey for the shortcode. Null/omitted keeps the existing value.</summary>
    [JsonPropertyName("passkey")]
    public string? Passkey { get; set; }

    // ── Manual M-PESA (no Daraja account needed) ────────────

    /// <summary>Turns on the "customer pays our PayBill/Till and pastes the SMS" flow.</summary>
    [JsonPropertyName("manualMpesaEnabled")]
    public bool ManualMpesaEnabled { get; set; }

    [JsonPropertyName("mpesaPaybillNumber")]
    public string? MpesaPaybillNumber { get; set; }

    [JsonPropertyName("mpesaPaybillAccount")]
    public string? MpesaPaybillAccount { get; set; }

    [JsonPropertyName("mpesaTillNumber")]
    public string? MpesaTillNumber { get; set; }

    /// <summary>
    /// M-PESA number for the Send Money route. Accepts 07…, 01…, +254…, 254…, 7…, 1…
    /// (spaces/dashes tolerated); stored normalised as 254XXXXXXXXX. Null/blank clears it.
    /// </summary>
    [JsonPropertyName("mpesaPhoneNumber")]
    public string? MpesaPhoneNumber { get; set; }

    /// <summary>Shown at checkout so the customer can check the name on their confirmation matches.</summary>
    [JsonPropertyName("mpesaPayeeName")]
    public string? MpesaPayeeName { get; set; }

    [JsonPropertyName("mpesaInstructions")]
    public string? MpesaInstructions { get; set; }

    /// <summary>
    /// "Payment Included": the customer pays during booking. Null/omitted keeps the
    /// stored value, so a client that does not know about this setting cannot switch a
    /// business's storefront policy off by leaving the field out.
    /// </summary>
    [JsonPropertyName("payUpfrontEnabled")]
    public bool? PayUpfrontEnabled { get; set; }

    /// <summary>
    /// "Pay After Service": the customer books now and pays when the service is done.
    /// Null/omitted keeps the stored value.
    /// </summary>
    [JsonPropertyName("payAfterServiceEnabled")]
    public bool? PayAfterServiceEnabled { get; set; }

    /// <summary>
    /// Percentage of the service total collected up front (0–100). Null/omitted
    /// keeps the stored value, so a client that does not know about this setting
    /// cannot silently reset a business's pricing.
    /// </summary>
    [JsonPropertyName("bookingFeePercentage")]
    public decimal? BookingFeePercentage { get; set; }
}

/// <summary>
/// The customer pastes the M-PESA confirmation SMS they received.
///
/// <para>There is deliberately no amount field. The amount that matters is the one
/// the message itself states, which the server parses — accepting a client-supplied
/// number would let anyone "pay" any amount by typing it.</para>
/// </summary>
public class SubmitMpesaMessageRequest
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Owner confirms a submitted payment.
///
/// <para>The note is optional audit colour. The confirmation flags and the optional
/// received amount/reference are what the Finance &gt; Reconciliation workspace asks the
/// owner to state out loud before money is recognised: that the transaction exists on
/// their own M-PESA statement, that the amount and code match, and that the money
/// landed in this business's account. They are recorded on the verification trail as
/// evidence. They are nullable so a lightweight caller (or a replayed request) is not
/// forced to re-assert them.</para>
/// </summary>
public class VerifyPaymentRequest
{
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>What the owner actually saw credited, when it differs from the claim.</summary>
    [JsonPropertyName("receivedAmount")]
    public decimal? ReceivedAmount { get; set; }

    /// <summary>The receipt code the owner read off their own statement, when they corrected it.</summary>
    [JsonPropertyName("transactionReference")]
    public string? TransactionReference { get; set; }

    [JsonPropertyName("transactionExists")]
    public bool? TransactionExists { get; set; }

    [JsonPropertyName("amountMatches")]
    public bool? AmountMatches { get; set; }

    [JsonPropertyName("codeMatches")]
    public bool? CodeMatches { get; set; }

    [JsonPropertyName("belongsToBusiness")]
    public bool? BelongsToBusiness { get; set; }
}

/// <summary>
/// Owner flags a submitted payment for further investigation. The note explains what
/// needs looking into and is kept internal (never shown to the customer).
/// </summary>
public class FlagPaymentRequest
{
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>Owner rejects a submitted payment. The reason is recorded and shown to the customer.</summary>
public class RejectPaymentRequest
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Filters for the owner's payment-verification queue and history.</summary>
public class PaymentVerificationQuery
{
    /// <summary>
    /// <c>submitted</c> | <c>under_review</c> | <c>flagged</c> | <c>attention</c> (flagged +
    /// under review) | <c>verified</c> | <c>rejected</c> | <c>awaiting</c> (the default) | <c>all</c>.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>Free-text match against the customer name, M-PESA code or reference.</summary>
    public string? Search { get; set; }

    /// <summary>Restrict to claims submitted within the window (submission date).</summary>
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    /// <summary>Restrict to claims whose extracted transaction date falls in the window.</summary>
    public DateTime? TransactionFrom { get; set; }
    public DateTime? TransactionTo { get; set; }

    /// <summary>Restrict by the amount the customer claims to have paid.</summary>
    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public class PaymentListQuery
{
    public Guid? AppointmentId { get; set; }
    public Guid? CustomerId { get; set; }
    public string? Status { get; set; }
    public string? Method { get; set; }
    public string? Type { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    /// <summary>Free-text search across reference, phone, M-Pesa code and customer name/email.</summary>
    public string? Search { get; set; }

    /// <summary>
    /// The order the register is read in: <c>date-d</c> / <c>newest</c> (default), <c>date-a</c> /
    /// <c>oldest</c>, <c>amt-d</c> / <c>amount-desc</c>, <c>amt-a</c> / <c>amount-asc</c>,
    /// <c>name-a</c> / <c>name</c>. Unknown values fall back to newest-first; the read never fails
    /// because a client sent an ordering it does not recognise.
    /// </summary>
    public string? Sort { get; set; }

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}
