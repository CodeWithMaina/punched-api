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
/// The three manual M-PESA routes a business may offer, in ONE place so the DB
/// column, the validation and the public API cannot drift apart.
/// </summary>
public static class ManualMpesaMethods
{
    /// <summary>Business number + account number.</summary>
    public const string Paybill = "paybill";

    /// <summary>Till number (Buy Goods and Services).</summary>
    public const string Till = "till";

    /// <summary>A plain M-PESA phone number (Send Money).</summary>
    public const string SendMoney = "send_money";

    /// <summary>All known values, for validation and constraint checks.</summary>
    public static readonly string[] All = [Paybill, Till, SendMoney];

    public static bool IsKnown(string? value) => value is Paybill or Till or SendMoney;

    /// <summary>
    /// Canonicalises a stored or client value. Historical aliases ("phone") and casing
    /// fold to the canonical value; anything unknown defaults to PayBill, which is the
    /// column's own default and keeps an old client that never sent a type working.
    /// </summary>
    public static string Normalise(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Till => Till,
        "send_money" or "sendmoney" or "phone" => SendMoney,
        _ => Paybill
    };
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

    /// <summary>
    /// M-PESA account type: "paybill", "till" or "send_money". Selects the single
    /// method the business accepts on its storefront (only that method's fields are
    /// ever exposed publicly) and, for "paybill"/"till", drives the Daraja STK
    /// TransactionType.
    /// </summary>
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

    /// <summary>
    /// M-PESA mobile number (Send Money) customers send to. Stored normalised in
    /// E.164 digits form without the plus: <c>2547XXXXXXXX</c> / <c>2541XXXXXXXX</c>.
    /// Null when the business collects on a PayBill or a Till.
    /// </summary>
    [MaxLength(12)]
    public string? MpesaPhoneNumber { get; set; }

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

    // ── Storefront payment options ──────────────────────────
    // Two independent policy switches that decide what a customer is offered when
    // booking from the storefront. They are deliberately separate from the channel
    // configuration above: a business chooses WHEN money is taken (up front or after the
    // service), and separately HOW it can be taken (cash at the desk, Daraja, manual
    // M-PESA). Conflating the two would make turning off a channel silently change the
    // booking policy.

    /// <summary>
    /// "Payment Included": the customer pays during the booking. On by default — that is
    /// the behaviour every existing storefront already had, so a migration must not change
    /// what a live booking page does.
    /// </summary>
    public bool PayUpfrontEnabled { get; set; } = true;

    /// <summary>
    /// "Pay After Service": the customer books now and settles when the service is done.
    /// Off by default: it changes what the customer is asked for up front, and an
    /// appointment booked this way immediately carries an outstanding payment — so it is
    /// an explicit decision by the owner rather than something a migration imposes.
    /// </summary>
    public bool PayAfterServiceEnabled { get; set; }

    /// <summary>
    /// True when the storefront must ask the customer which option they want. With one
    /// option enabled the flow applies it without asking; with both it has to ask; with
    /// neither the business is not taking bookings.
    /// </summary>
    public bool OffersPaymentChoice => PayUpfrontEnabled && PayAfterServiceEnabled;

    /// <summary>
    /// True while the business takes storefront bookings at all. The settings endpoint
    /// refuses to save a configuration where this would be false, so a false value means
    /// the row was changed outside the app (see the booking-time guard, which still
    /// refuses such a booking rather than silently picking an option).
    /// </summary>
    public bool BookingAvailable => PayUpfrontEnabled || PayAfterServiceEnabled;

    /// <summary>
    /// The single manual M-PESA route this business actually exposes, or null when
    /// none is complete. Only one method is ever active, so the storefront can never
    /// show an inactive method's saved values or a half-configured instruction.
    ///
    /// <para>The explicitly selected <see cref="MpesaAccountType"/> wins when its own
    /// fields are complete (so switching methods does not require deleting the other
    /// method's saved values — the owner can toggle back). When the stored type has no
    /// complete configuration, the single complete method is used instead, so PayBill
    /// and Till rows created before the method selector existed behave exactly as
    /// before.</para>
    /// </summary>
    public string? ActiveManualMethod()
    {
        var type = ManualMpesaMethods.Normalise(MpesaAccountType);
        var hasPaybill = !string.IsNullOrWhiteSpace(MpesaPaybillNumber)
                         && !string.IsNullOrWhiteSpace(MpesaPaybillAccount);
        var hasTill = !string.IsNullOrWhiteSpace(MpesaTillNumber);
        var hasPhone = !string.IsNullOrWhiteSpace(MpesaPhoneNumber);

        if (type == ManualMpesaMethods.SendMoney && hasPhone) return ManualMpesaMethods.SendMoney;
        if (type == ManualMpesaMethods.Till && hasTill) return ManualMpesaMethods.Till;
        if (type == ManualMpesaMethods.Paybill && hasPaybill) return ManualMpesaMethods.Paybill;

        // Legacy rows: the stored type predates the selector, so fall back to the one
        // method that is actually complete.
        if (hasPaybill) return ManualMpesaMethods.Paybill;
        if (hasTill) return ManualMpesaMethods.Till;
        return null;
    }

    /// <summary>
    /// True when the business can actually be paid on this channel: manual M-PESA is
    /// on and the active method resolves to a complete configuration. A half-configured
    /// method is not payable, so it does not count.
    /// </summary>
    public bool HasManualMpesaDetails => ManualMpesaEnabled && ActiveManualMethod() != null;

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
