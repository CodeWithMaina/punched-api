using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Raw provider callback/webhook event. Persisted BEFORE processing so no payload
/// is ever lost and duplicates can be detected. Processing is idempotent keyed on
/// <see cref="EventKey"/> (unique index).
/// </summary>
public class PaymentCallback : BaseEntity
{
    /// <summary>FK to the business this event belongs to (resolved from shortcode/till). Null when unresolvable.</summary>
    public Guid? BusinessId { get; set; }

    /// <summary>FK to the payment this event targets (resolved from references). Null when unmatched.</summary>
    public Guid? PaymentId { get; set; }

    /// <summary>Provider channel: "stk", "c2b_confirmation", "c2b_validation", "status".</summary>
    [Required]
    [MaxLength(30)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Deduplication key: e.g. "stk:{CheckoutRequestId}:{ResultCode}" or "c2b:{TransID}". Unique.</summary>
    [Required]
    [MaxLength(200)]
    public string EventKey { get; set; } = string.Empty;

    /// <summary>Result code from the provider (0 = success for STK/C2B).</summary>
    [MaxLength(50)]
    public string? ResultCode { get; set; }

    [MaxLength(500)]
    public string? ResultDescription { get; set; }

    /// <summary>Provider transaction id (TransID / MpesaReceiptNumber) when present.</summary>
    [MaxLength(100)]
    public string? TransactionId { get; set; }

    /// <summary>Raw JSON payload exactly as received (never contains credentials).</summary>
    [Required]
    public string RawPayload { get; set; } = string.Empty;

    /// <summary>True once the event has been applied to a payment (or permanently ignored).</summary>
    public bool Processed { get; set; }

    /// <summary>Outcome: "applied", "duplicate", "unmatched", "rejected".</summary>
    [MaxLength(20)]
    public string? Outcome { get; set; }

    public DateTime? ProcessedAt { get; set; }

    // ── Navigation ──────────────────────────────────────────
    /// <summary>The payment this event targets. Null while unmatched (reconciliation queue).</summary>
    public virtual Payment? Payment { get; set; }
}

/// <summary>
/// Per-business payment configuration. One row per business (unique index).
/// M-PESA credentials are stored AES-GCM encrypted at rest and NEVER returned
/// to the browser or written to logs.
/// </summary>
public class BusinessPaymentConfig : BaseEntity
{
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>Cash payments enabled for this business.</summary>
    public bool CashEnabled { get; set; } = true;

    /// <summary>M-PESA payments enabled for this business.</summary>
    public bool MpesaEnabled { get; set; }

    /// <summary>M-PESA account type: "paybill" or "till" (drives STK TransactionType).</summary>
    [MaxLength(10)]
    public string MpesaAccountType { get; set; } = "paybill";

    /// <summary>Business's own PayBill or Till number. Payments settle directly into this account.</summary>
    [MaxLength(20)]
    public string? MpesaShortCode { get; set; }

    /// <summary>Consumer Key for Daraja (encrypted at rest).</summary>
    public string? ConsumerKeyEncrypted { get; set; }

    /// <summary>Consumer Secret for Daraja (encrypted at rest).</summary>
    public string? ConsumerSecretEncrypted { get; set; }

    /// <summary>STK Passkey for the shortcode (encrypted at rest).</summary>
    public string? PasskeyEncrypted { get; set; }

    public DateTime? CredentialsUpdatedAt { get; set; }

    /// <summary>Whether valid (decrypted) credentials are present — safe to expose to the UI.</summary>
    public bool HasCredentials => ConsumerKeyEncrypted != null && ConsumerSecretEncrypted != null && PasskeyEncrypted != null;

    // ── Manual M-PESA (customer pays the business directly) ──
    // Distinct from the Daraja credentials above: this path needs no API access at
    // all. The business publishes the details below on its storefront, the customer
    // pays from their own handset, and the owner verifies the pasted confirmation.

    /// <summary>Manual M-PESA collection enabled. Independent of <see cref="MpesaEnabled"/> (which is the Daraja STK path).</summary>
    public bool ManualMpesaEnabled { get; set; }

    /// <summary>PayBill number customers send to. Null when the business collects on a Till.</summary>
    [MaxLength(20)]
    public string? MpesaPaybillNumber { get; set; }

    /// <summary>PayBill account number customers must enter. Null when the business collects on a Till.</summary>
    [MaxLength(40)]
    public string? MpesaPaybillAccount { get; set; }

    /// <summary>Till (Buy Goods) number customers send to. Null when the business collects on a PayBill.</summary>
    [MaxLength(20)]
    public string? MpesaTillNumber { get; set; }

    /// <summary>The name the customer should see on their M-PESA confirmation, so they can check it matches.</summary>
    [MaxLength(100)]
    public string? MpesaPayeeName { get; set; }

    /// <summary>Free-text instructions the owner wants shown at checkout (e.g. "Send the booking fee, then paste the SMS").</summary>
    [MaxLength(500)]
    public string? MpesaInstructions { get; set; }

    /// <summary>
    /// Percentage of the appointment's service total charged up front as a booking
    /// fee (0–100, 2dp). 0 disables the fee entirely, so the whole amount is settled
    /// at the appointment. The default of 50 keeps the historical half-up-front
    /// behaviour for a business that has never opened this setting.
    /// </summary>
    [Range(0, 100)]
    public decimal BookingFeePercentage { get; set; } = 50m;

    /// <summary>
    /// True when the business can actually be paid on this channel: manual M-PESA is
    /// on and at least one of a PayBill (with its account number) or a Till is
    /// configured. A half-configured PayBill is not payable, so it does not count.
    /// </summary>
    public bool HasManualMpesaDetails =>
        ManualMpesaEnabled &&
        ((!string.IsNullOrWhiteSpace(MpesaPaybillNumber) && !string.IsNullOrWhiteSpace(MpesaPaybillAccount))
         || !string.IsNullOrWhiteSpace(MpesaTillNumber));

    /// <summary>Resolves the booking fee for a service total, rounded to 2dp (banker-free, away-from-zero).</summary>
    public decimal BookingFeeFor(decimal serviceTotal)
    {
        if (serviceTotal <= 0) return 0m;
        var percent = Math.Clamp(BookingFeePercentage, 0m, 100m);
        return decimal.Round(serviceTotal * percent / 100m, 2, MidpointRounding.AwayFromZero);
    }

    // ── Navigation ──────────────────────────────────────────
    public virtual Business Business { get; set; } = null!;
}

/// <summary>
/// Append-only audit trail of a manual M-PESA payment's verification lifecycle.
///
/// <para>Separate from <see cref="PaymentAttempt"/> (which records provider calls):
/// this records <i>human</i> decisions and customer submissions. Rows are never
/// updated or deleted, so "who rejected what, when, and why" survives a later
/// approval — which is exactly what the business's payment history screen reads.</para>
/// </summary>
public class PaymentVerificationEvent : BaseEntity
{
    /// <summary>Tenant scope; mandatory for isolation.</summary>
    [Required]
    public Guid BusinessId { get; set; }

    [Required]
    public Guid PaymentId { get; set; }

    /// <summary>"submitted" | "under_review" | "verified" | "rejected".</summary>
    [Required]
    [MaxLength(20)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Who acted (the customer for a submission, the owner/staff for a decision). Null for system events.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Actor's display name captured at the time, so history survives a rename.</summary>
    [MaxLength(100)]
    public string? ActorName { get; set; }

    /// <summary>Amount involved in this event (the submitted amount, or the verified amount).</summary>
    public decimal? Amount { get; set; }

    /// <summary>M-PESA transaction code this event concerns.</summary>
    [MaxLength(20)]
    public string? TransactionCode { get; set; }

    /// <summary>The raw message for a submission; null for decisions.</summary>
    [MaxLength(1000)]
    public string? Message { get; set; }

    /// <summary>Reason/note attached to the event (rejection reason, review note).</summary>
    [MaxLength(500)]
    public string? Reason { get; set; }

    // ── Navigation ──────────────────────────────────────────
    public virtual Payment Payment { get; set; } = null!;
}
