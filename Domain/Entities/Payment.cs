using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// A customer payment owed/made to a business. Punched never holds these funds —
/// the business is always the recipient (its own PayBill/Till or its cash drawer).
/// The amount is ALWAYS computed server-side from the appointment's services;
/// the frontend is never the source of truth for money.
/// </summary>
public class Payment : BaseEntity
{
    /// <summary>FK to the receiving business (tenant scope; mandatory for isolation).</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>FK to the paying customer's User. Null when unknown (e.g. unmatched C2B event).</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>FK to the appointment this payment satisfies. Null for standalone payments.</summary>
    public Guid? AppointmentId { get; set; }

    /// <summary>What the payment is for (full payment, booking fee, balance).</summary>
    [Required]
    public PaymentType Type { get; set; } = PaymentType.FullPayment;

    /// <summary>Payment method (Cash or Mpesa).</summary>
    [Required]
    public PaymentMethod Method { get; set; } = PaymentMethod.Cash;

    /// <summary>Concrete provider channel selected at creation/first attempt.</summary>
    [Required]
    public PaymentProviderKind Provider { get; set; } = PaymentProviderKind.Cash;

    /// <summary>Current lifecycle status. Only change via <see cref="PunchedApi.Application.Services.PaymentStateMachine"/>.</summary>
    [Required]
    public PaymentStatus Status { get; set; } = PaymentStatus.Created;

    /// <summary>Amount in KES the business expects to receive (major units, 2dp).</summary>
    [Required]
    [Range(0.01, 1_000_000)]
    public decimal Amount { get; set; }

    /// <summary>ISO-4217 currency. MVP is KES only.</summary>
    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "KES";

    /// <summary>Punched-issued internal reference (PMT-…). Used as the C2B BillRefNumber and STK AccountReference.</summary>
    [Required]
    [MaxLength(40)]
    public string Reference { get; set; } = string.Empty;

    /// <summary>External provider reference (e.g. M-PESA receipt number) once confirmed.</summary>
    [MaxLength(100)]
    public string? ExternalReference { get; set; }

    /// <summary>Customer phone number used for M-PESA (2547XXXXXXXX).</summary>
    [MaxLength(20)]
    public string? PhoneNumber { get; set; }

    /// <summary>User (customer/staff/business) who created the payment. Null for system creation.</summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>UTC time the payment reached Success (null until then).</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>UTC time of the latest failure (null when no failure recorded).</summary>
    public DateTime? FailedAt { get; set; }

    /// <summary>UTC time the payment was cancelled (null when not cancelled).</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>UTC time a reversal/refund completed (null when none).</summary>
    public DateTime? ReversedAt { get; set; }

    /// <summary>Who confirmed a cash payment (staff/business user). Null for provider payments.</summary>
    public Guid? ConfirmedByUserId { get; set; }

    /// <summary>Why a payment was reversed/refunded/cancelled (audit). Null when none.</summary>
    [MaxLength(500)]
    public string? ReversalReason { get; set; }

    // ── Manual M-PESA submission & verification ─────────────
    // The customer pays the business's own PayBill/Till and pastes the confirmation
    // SMS. The message is parsed, but it is NEVER proof of payment on its own: the
    // business owner is the only authority that can move a submission to Verified.

    /// <summary>Raw confirmation SMS exactly as the customer pasted it. Kept verbatim for the owner to read.</summary>
    [MaxLength(1000)]
    public string? SubmittedMessage { get; set; }

    /// <summary>UTC time the customer submitted the message.</summary>
    public DateTime? SubmittedAt { get; set; }

    /// <summary>M-PESA transaction/receipt code extracted from the message (e.g. QJK42NUM). Unique across the platform.</summary>
    [MaxLength(20)]
    public string? ParsedTransactionCode { get; set; }

    /// <summary>Amount extracted from the message — the value the comparison is made against, never the amount the client sent.</summary>
    public decimal? ParsedAmount { get; set; }

    /// <summary>Transaction date/time extracted from the message, when the message carried one.</summary>
    public DateTime? ParsedTransactionAt { get; set; }

    /// <summary>Payee/recipient extracted from the message, when present.</summary>
    [MaxLength(200)]
    public string? ParsedRecipient { get; set; }

    /// <summary>UTC time the owner confirmed the money arrived (null until then).</summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>Owner/staff user who confirmed the payment.</summary>
    public Guid? VerifiedByUserId { get; set; }

    /// <summary>UTC time the owner rejected the submission (null until then).</summary>
    public DateTime? RejectedAt { get; set; }

    /// <summary>Owner/staff user who rejected the submission.</summary>
    public Guid? RejectedByUserId { get; set; }

    /// <summary>Why the submission was rejected. Recorded so the customer can be told what to fix.</summary>
    [MaxLength(500)]
    public string? RejectionReason { get; set; }

    // ── Navigation ──────────────────────────────────────────
    public virtual Business Business { get; set; } = null!;
    public virtual Appointment? Appointment { get; set; }
    public virtual ICollection<PaymentAttempt> Attempts { get; set; } = new List<PaymentAttempt>();
    public virtual ICollection<PaymentVerificationEvent> VerificationEvents { get; set; } =
        new List<PaymentVerificationEvent>();
}

/// <summary>
/// A single attempt against the provider for a payment. Attempts are append-only:
/// a failed attempt is never overwritten so the financial audit trail is preserved.
/// </summary>
public class PaymentAttempt : BaseEntity
{
    [Required]
    public Guid PaymentId { get; set; }

    /// <summary>1-based ordinal (per payment).</summary>
    public int AttemptNumber { get; set; }

    [Required]
    public PaymentProviderKind Provider { get; set; }

    /// <summary>Attempt lifecycle: pending → success | failed | expired.</summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "pending";

    /// <summary>Daraja CheckoutRequestID (STK) for correlating callbacks.</summary>
    [MaxLength(100)]
    public string? CheckoutRequestId { get; set; }

    /// <summary>Daraja MerchantRequestID (STK).</summary>
    [MaxLength(100)]
    public string? MerchantRequestId { get; set; }

    /// <summary>M-PESA receipt number when this attempt succeeded.</summary>
    [MaxLength(100)]
    public string? ProviderReference { get; set; }

    /// <summary>Daraja/normalised provider error code on failure.</summary>
    [MaxLength(50)]
    public string? ErrorCode { get; set; }

    /// <summary>Human-readable failure reason.</summary>
    [MaxLength(500)]
    public string? ErrorMessage { get; set; }

    public DateTime? CompletedAt { get; set; }

    // ── Navigation ──────────────────────────────────────────
    public virtual Payment Payment { get; set; } = null!;
}
