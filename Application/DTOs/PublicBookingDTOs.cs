using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

/// <summary>
/// A booking made from the public storefront — no account, no token, no verification step.
///
/// <para><b>Why this exists next to <see cref="CreateAppointmentRequest"/>.</b> The customer
/// appointment API derives the customer from the caller's token, which is exactly right for the
/// signed-in customer app and wrong for a storefront: an anonymous visitor, a visitor whose
/// account belongs to another business, and the business's own owner browsing their own page all
/// have no "customer token" to offer. This request therefore carries the customer's details
/// instead, and the server resolves the business-specific customer record from them.</para>
///
/// <para>Nothing here can name a customer id: the appointment is always made for the customer the
/// server resolves, so a caller cannot book on somebody else's behalf.</para>
/// </summary>
public class PublicBookingRequest
{
    /// <summary>
    /// The business being booked with. Compared against the active tenant when the request
    /// arrives on a tenant host, so a body cannot book across tenants.
    /// </summary>
    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("serviceIds")]
    public Guid[] ServiceIds { get; set; } = [];

    [JsonPropertyName("staffUserId")]
    public Guid? StaffUserId { get; set; }

    [JsonPropertyName("scheduledAt")]
    public DateTime ScheduledAt { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>
    /// <c>pay_upfront</c> (pay during booking — "Payment Included") or
    /// <c>pay_after_service</c>. Validated against the business's own configuration, exactly as
    /// it is for a signed-in customer.
    /// </summary>
    [JsonPropertyName("paymentOption")]
    public string? PaymentOption { get; set; }

    /// <summary>The customer's name, as they gave it.</summary>
    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    /// <summary>Kenyan mobile number, in any of the shapes a person types it.</summary>
    [JsonPropertyName("phoneNumber")]
    public string PhoneNumber { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The M-PESA confirmation SMS the customer pasted, when they are paying during booking.
    ///
    /// <para>Optional, and never a precondition: the appointment is booked with or without it.
    /// The server parses the receipt, amount and payer out of the message and stores those parsed
    /// fields as the payment's evidence; a message that cannot be parsed is reported back to the
    /// customer and leaves the booking in place with an outstanding balance.</para>
    /// </summary>
    [JsonPropertyName("mpesaMessage")]
    public string? MpesaMessage { get; set; }

    /// <summary>
    /// Client-supplied key that makes a retried submit idempotent, so a double tap cannot open a
    /// second payment for the same booking.
    /// </summary>
    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// The temporary hold this booking converts, when the business requires a booking fee.
    ///
    /// <para>Required in that case and validated server-side: the hold must be live, unexpired and
    /// for exactly this interval. Its presence is what proves the customer claimed the slot before
    /// paying rather than booking a time somebody else was holding.</para>
    /// </summary>
    [JsonPropertyName("holdId")]
    public Guid? HoldId { get; set; }
}


/// <summary>
/// The state of the money on a public booking, in the vocabulary the storefront shows: not due,
/// owed, awaiting the business's verification, partially paid, or paid.
/// </summary>
public static class PublicPaymentStates
{
    /// <summary>Nothing is owed yet — a pay-after-service booking, or no fee configured.</summary>
    public const string NotDue = "not_due";

    /// <summary>Money is owed and nothing has been submitted.</summary>
    public const string Unpaid = "unpaid";

    /// <summary>A confirmation has been submitted and is waiting on the business.</summary>
    public const string PendingVerification = "pending_verification";

    /// <summary>The business (or the provider) has confirmed the money.</summary>
    public const string Paid = "paid";

    /// <summary>Part of the money has been confirmed; a balance remains.</summary>
    public const string PartiallyPaid = "partially_paid";
}

/// <summary>What the storefront is told after a public booking.</summary>
public class PublicBookingResponse
{
    /// <summary>
    /// The appointment, already created and booked. Its status is independent of the payment's:
    /// a booking that owes money is still a booking.
    /// </summary>
    [JsonPropertyName("appointment")]
    public AppointmentResponse Appointment { get; set; } = null!;

    /// <summary>The booking-fee payment created for this booking, when one was.</summary>
    [JsonPropertyName("payment")]
    public PaymentResponse? Payment { get; set; }

    /// <summary>The server's own figures for the appointment: total, paid, balance.</summary>
    [JsonPropertyName("summary")]
    public AppointmentPaymentSummary Summary { get; set; } = null!;

    /// <summary>One of <see cref="PublicPaymentStates"/>.</summary>
    [JsonPropertyName("paymentState")]
    public string PaymentState { get; set; } = PublicPaymentStates.NotDue;

    /// <summary>What the customer should read on the confirmation screen.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Anything the customer must act on — an unparseable paste, a receipt already used. The
    /// booking still stands; these explain what remains outstanding.
    /// </summary>
    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// A late M-PESA confirmation for an appointment already booked from the storefront: the same
/// submission, aimed at the booking-fee payment the booking created.
/// </summary>
public class SubmitPublicPaymentProofRequest
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }
}
