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

    // ── Navigation ──────────────────────────────────────────
    public virtual Business Business { get; set; } = null!;
}
