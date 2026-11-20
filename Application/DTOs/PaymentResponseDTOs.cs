using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

public class PaymentResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("customerId")]
    public Guid? CustomerId { get; set; }

    /// <summary>
    /// The customer's display name, resolved for the payments register (one query per page,
    /// never one per row). Null for a payment with no customer attached.
    /// </summary>
    [JsonPropertyName("customerName")]
    public string? CustomerName { get; set; }

    [JsonPropertyName("appointmentId")]
    public Guid? AppointmentId { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "KES";

    [JsonPropertyName("reference")]
    public string Reference { get; set; } = string.Empty;

    [JsonPropertyName("externalReference")]
    public string? ExternalReference { get; set; }

    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }

    // ── Manual M-PESA submission & verification ─────────────
    // Everything here is server-derived: the message is stored verbatim and the
    // parsed fields come from `MpesaMessageParser`, never from the client.

    /// <summary>The confirmation SMS exactly as the customer pasted it.</summary>
    [JsonPropertyName("submittedMessage")]
    public string? SubmittedMessage { get; set; }

    [JsonPropertyName("submittedAt")]
    public DateTime? SubmittedAt { get; set; }

    [JsonPropertyName("parsedTransactionCode")]
    public string? ParsedTransactionCode { get; set; }

    [JsonPropertyName("parsedAmount")]
    public decimal? ParsedAmount { get; set; }

    [JsonPropertyName("parsedTransactionAt")]
    public DateTime? ParsedTransactionAt { get; set; }

    [JsonPropertyName("parsedRecipient")]
    public string? ParsedRecipient { get; set; }

    [JsonPropertyName("verifiedAt")]
    public DateTime? VerifiedAt { get; set; }

    [JsonPropertyName("verifiedByUserId")]
    public Guid? VerifiedByUserId { get; set; }

    [JsonPropertyName("rejectedAt")]
    public DateTime? RejectedAt { get; set; }

    [JsonPropertyName("rejectedByUserId")]
    public Guid? RejectedByUserId { get; set; }

    [JsonPropertyName("rejectionReason")]
    public string? RejectionReason { get; set; }

    /// <summary>Append-only trail of submissions and decisions, newest last.</summary>
    [JsonPropertyName("verificationEvents")]
    public List<PaymentVerificationEventResponse> VerificationEvents { get; set; } = new();

    [JsonPropertyName("attempts")]
    public List<PaymentAttemptResponse> Attempts { get; set; } = new();

    /// <summary>
    /// The ledger row this payment was recognised as, once the money is settled. Null while the
    /// payment is unpaid: a cashbook entry exists only where the money really is.
    /// </summary>
    [JsonPropertyName("financialTransactionId")]
    public Guid? FinancialTransactionId { get; set; }

    /// <summary>The cashbook reference (FIN-…) of that ledger row.</summary>
    [JsonPropertyName("ledgerReference")]
    public string? LedgerReference { get; set; }

    /// <summary>Who recorded the income in the books, when the server could resolve it.</summary>
    [JsonPropertyName("ledgerRecordedByName")]
    public string? LedgerRecordedByName { get; set; }

    /// <summary>
    /// Business-facing state of the books for this payment:
    /// <c>recorded</c> (verified income exists), <c>processing</c> (settled, the outbox still owes
    /// the row — it will appear automatically), <c>attention</c> (settlement succeeded but the books
    /// need a human), or null when the payment is not settled and therefore not income.
    ///
    /// <para>Deliberately a coarse state, never the internal error text: a business needs to know
    /// whether the money is in its books, not which database constraint failed.</para>
    /// </summary>
    [JsonPropertyName("ledgerProcessingStatus")]
    public string? LedgerProcessingStatus { get; set; }
}

/// <summary>One entry of a manual M-PESA payment's verification trail.</summary>
public class PaymentVerificationEventResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>"submitted" | "under_review" | "verified" | "rejected".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("actorUserId")]
    public Guid? ActorUserId { get; set; }

    [JsonPropertyName("actorName")]
    public string? ActorName { get; set; }

    [JsonPropertyName("amount")]
    public decimal? Amount { get; set; }

    [JsonPropertyName("transactionCode")]
    public string? TransactionCode { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

public class PaymentAttemptResponse
{
    [JsonPropertyName("attemptNumber")]
    public int AttemptNumber { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("checkoutRequestId")]
    public string? CheckoutRequestId { get; set; }

    [JsonPropertyName("providerReference")]
    public string? ProviderReference { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }
}

public class PaymentListResponse
{
    [JsonPropertyName("items")]
    public List<PaymentResponse> Items { get; set; } = new();

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    /// <summary>Total pages for the filtered result set.</summary>
    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }
}

/// <summary>Per-appointment payment summary computed server-side from services + payments.</summary>
public class AppointmentPaymentSummary
{
    [JsonPropertyName("appointmentId")]
    public Guid AppointmentId { get; set; }

    [JsonPropertyName("serviceTotal")]
    public decimal ServiceTotal { get; set; }

    /// <summary>
    /// What the business asks for up front, derived from its configured booking-fee
    /// percentage. 0 when the business collects nothing in advance.
    /// </summary>
    [JsonPropertyName("bookingFee")]
    public decimal BookingFee { get; set; }

    /// <summary>The configured percentage this <see cref="BookingFee"/> was computed from.</summary>
    [JsonPropertyName("bookingFeePercentage")]
    public decimal BookingFeePercentage { get; set; }

    [JsonPropertyName("amountPaid")]
    public decimal AmountPaid { get; set; }

    [JsonPropertyName("balance")]
    public decimal Balance { get; set; }

    /// <summary>"unpaid" | "partially_paid" | "paid".</summary>
    [JsonPropertyName("paymentStatus")]
    public string PaymentStatus { get; set; } = "unpaid";

    /// <summary>"none" | "pending" | "awaiting_verification" | "verified" | "rejected".</summary>
    [JsonPropertyName("verificationStatus")]
    public string VerificationStatus { get; set; } = "none";

    [JsonPropertyName("payments")]
    public List<PaymentResponse> Payments { get; set; } = new();
}

/// <summary>Business payment configuration. Secrets are NEVER included in any response.</summary>
public class PaymentConfigResponse
{
    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("cashEnabled")]
    public bool CashEnabled { get; set; }

    [JsonPropertyName("mpesaEnabled")]
    public bool MpesaEnabled { get; set; }

    [JsonPropertyName("mpesaAccountType")]
    public string MpesaAccountType { get; set; } = "paybill";

    [JsonPropertyName("mpesaShortCode")]
    public string? MpesaShortCode { get; set; }

    /// <summary>True when Daraja credentials are stored (value itself never returned).</summary>
    [JsonPropertyName("hasCredentials")]
    public bool HasCredentials { get; set; }

    [JsonPropertyName("credentialsUpdatedAt")]
    public DateTime? CredentialsUpdatedAt { get; set; }

    // ── Manual M-PESA ───────────────────────────────────────

    [JsonPropertyName("manualMpesaEnabled")]
    public bool ManualMpesaEnabled { get; set; }

    [JsonPropertyName("mpesaPaybillNumber")]
    public string? MpesaPaybillNumber { get; set; }

    [JsonPropertyName("mpesaPaybillAccount")]
    public string? MpesaPaybillAccount { get; set; }

    [JsonPropertyName("mpesaTillNumber")]
    public string? MpesaTillNumber { get; set; }

    /// <summary>The Send Money route's M-PESA number, normalised to 254XXXXXXXXX. Null when unused.</summary>
    [JsonPropertyName("mpesaPhoneNumber")]
    public string? MpesaPhoneNumber { get; set; }

    [JsonPropertyName("mpesaPayeeName")]
    public string? MpesaPayeeName { get; set; }

    [JsonPropertyName("mpesaInstructions")]
    public string? MpesaInstructions { get; set; }

    [JsonPropertyName("bookingFeePercentage")]
    public decimal BookingFeePercentage { get; set; }

    /// <summary>True when the manual channel is switched on AND fully configured — what the storefront keys off.</summary>
    [JsonPropertyName("hasManualMpesaDetails")]
    public bool HasManualMpesaDetails { get; set; }

    /// <summary>"Payment Included": customers may pay during booking.</summary>
    [JsonPropertyName("payUpfrontEnabled")]
    public bool PayUpfrontEnabled { get; set; } = true;

    /// <summary>"Pay After Service": customers may book now and pay later.</summary>
    [JsonPropertyName("payAfterServiceEnabled")]
    public bool PayAfterServiceEnabled { get; set; }
}

/// <summary>
/// What an anonymous storefront visitor is told about paying this business.
///
/// <para>Only the details a customer must see to make a transfer: never credentials,
/// never the shortcode's secret material. A business with the manual channel
/// switched off returns <c>enabled: false</c> and no numbers, so the storefront
/// cannot render a half-configured instruction.</para>
/// </summary>
public class PublicPaymentDetailsResponse
{
    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("businessName")]
    public string BusinessName { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "KES";

    /// <summary>Manual M-PESA is on and fully configured.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>"paybill" | "till" | "send_money" | null — the one method the customer should use.</summary>
    [JsonPropertyName("accountType")]
    public string? AccountType { get; set; }

    [JsonPropertyName("paybillNumber")]
    public string? PaybillNumber { get; set; }

    [JsonPropertyName("paybillAccount")]
    public string? PaybillAccount { get; set; }

    [JsonPropertyName("tillNumber")]
    public string? TillNumber { get; set; }

    /// <summary>Send Money only: the business's M-PESA number, normalised to 254XXXXXXXXX.</summary>
    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("payeeName")]
    public string? PayeeName { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    /// <summary>The percentage of a booking the customer pays up front.</summary>
    [JsonPropertyName("bookingFeePercentage")]
    public decimal BookingFeePercentage { get; set; }

    /// <summary>Cash collected at the appointment is still offered.</summary>
    [JsonPropertyName("cashEnabled")]
    public bool CashEnabled { get; set; }

    /// <summary>
    /// "Payment Included" — the customer may pay during the booking. Reported to the
    /// storefront so it offers exactly what this business allows, never a control that
    /// would be refused on submit.
    /// </summary>
    [JsonPropertyName("payUpfrontEnabled")]
    public bool PayUpfrontEnabled { get; set; } = true;

    /// <summary>"Pay After Service" — the customer may book now and settle later.</summary>
    [JsonPropertyName("payAfterServiceEnabled")]
    public bool PayAfterServiceEnabled { get; set; }

    /// <summary>
    /// True when the business takes storefront bookings at all. False only when BOTH
    /// options are off, which the settings screen refuses to save — the storefront still
    /// reads this flag so a business changed outside the app is told, not mis-booked.
    /// </summary>
    [JsonPropertyName("bookingAvailable")]
    public bool BookingAvailable { get; set; } = true;

    /// <summary>
    /// True when the customer must choose between the two options (both are enabled).
    /// With one option enabled the storefront applies it without asking.
    /// </summary>
    [JsonPropertyName("offersPaymentChoice")]
    public bool OffersPaymentChoice { get; set; }
}

/// <summary>Aggregate figures for the business payments dashboard (only real recorded payments — no invented metrics).</summary>
public class PaymentDashboardResponse
{
    [JsonPropertyName("todayCollected")]
    public decimal TodayCollected { get; set; }

    [JsonPropertyName("todayCount")]
    public int TodayCount { get; set; }

    [JsonPropertyName("monthCollected")]
    public decimal MonthCollected { get; set; }

    [JsonPropertyName("monthCount")]
    public int MonthCount { get; set; }

    [JsonPropertyName("pendingCount")]
    public int PendingCount { get; set; }

    [JsonPropertyName("pendingAmount")]
    public decimal PendingAmount { get; set; }

    [JsonPropertyName("failedCount")]
    public int FailedCount { get; set; }

    [JsonPropertyName("cashCollectedToday")]
    public decimal CashCollectedToday { get; set; }

    [JsonPropertyName("mpesaCollectedToday")]
    public decimal MpesaCollectedToday { get; set; }

    /// <summary>Manual M-PESA submissions waiting on the owner's decision.</summary>
    [JsonPropertyName("awaitingVerificationCount")]
    public int AwaitingVerificationCount { get; set; }

    /// <summary>Total value of those submissions.</summary>
    [JsonPropertyName("awaitingVerificationAmount")]
    public decimal AwaitingVerificationAmount { get; set; }

    [JsonPropertyName("unmatchedCallbackCount")]
    public int UnmatchedCallbackCount { get; set; }
}

/// <summary>One row of the owner's payment-verification queue.</summary>
public class PaymentVerificationItemResponse
{
    [JsonPropertyName("paymentId")]
    public Guid PaymentId { get; set; }

    [JsonPropertyName("appointmentId")]
    public Guid? AppointmentId { get; set; }

    [JsonPropertyName("appointmentStatus")]
    public string? AppointmentStatus { get; set; }

    [JsonPropertyName("scheduledAt")]
    public DateTime? ScheduledAt { get; set; }

    [JsonPropertyName("customerId")]
    public Guid? CustomerId { get; set; }

    [JsonPropertyName("customerName")]
    public string? CustomerName { get; set; }

    [JsonPropertyName("customerPhone")]
    public string? CustomerPhone { get; set; }

    /// <summary>Customer email when the claim is tied to a registered customer.</summary>
    [JsonPropertyName("customerEmail")]
    public string? CustomerEmail { get; set; }

    /// <summary>
    /// True when the payer is a registered customer, false for a guest checkout. The
    /// owner sees which, because a guest has no account to chase.
    /// </summary>
    [JsonPropertyName("isRegisteredCustomer")]
    public bool IsRegisteredCustomer { get; set; }

    /// <summary>The staff member the appointment is assigned to, when there is one.</summary>
    [JsonPropertyName("assignedStaffName")]
    public string? AssignedStaffName { get; set; }

    [JsonPropertyName("serviceNames")]
    public List<string> ServiceNames { get; set; } = new();

    /// <summary>What the business is owed for this payment (server-computed).</summary>
    [JsonPropertyName("expectedAmount")]
    public decimal ExpectedAmount { get; set; }

    /// <summary>
    /// The appointment's total value, when the claim is tied to one. Distinct from
    /// <see cref="ExpectedAmount"/> (this payment's slice) so the owner can see the
    /// bigger picture on a partial/booking-fee claim.
    /// </summary>
    [JsonPropertyName("bookingAmount")]
    public decimal? BookingAmount { get; set; }

    /// <summary>What is still owed on the appointment after this payment settles (0 when none).</summary>
    [JsonPropertyName("outstandingAmount")]
    public decimal? OutstandingAmount { get; set; }

    /// <summary>What the pasted message says was paid (server-parsed).</summary>
    [JsonPropertyName("submittedAmount")]
    public decimal? SubmittedAmount { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "KES";

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("reference")]
    public string Reference { get; set; } = string.Empty;

    [JsonPropertyName("transactionCode")]
    public string? TransactionCode { get; set; }

    [JsonPropertyName("transactionAt")]
    public DateTime? TransactionAt { get; set; }

    [JsonPropertyName("recipient")]
    public string? Recipient { get; set; }

    [JsonPropertyName("payer")]
    public string? Payer { get; set; }

    [JsonPropertyName("submittedMessage")]
    public string? SubmittedMessage { get; set; }

    [JsonPropertyName("submittedAt")]
    public DateTime? SubmittedAt { get; set; }

    /// <summary>True when the parsed amount differs from what is owed — the owner's first red flag.</summary>
    [JsonPropertyName("amountMismatch")]
    public bool AmountMismatch { get; set; }

    /// <summary>
    /// Set when this transaction code has been used on another payment. Surfaced so
    /// the owner can spot a reused receipt without leaving the queue.
    /// </summary>
    [JsonPropertyName("duplicateOfPaymentId")]
    public Guid? DuplicateOfPaymentId { get; set; }

    /// <summary>The internal note recorded by the last "flag for review" action, when the claim is flagged.</summary>
    [JsonPropertyName("flagNote")]
    public string? FlagNote { get; set; }

    /// <summary>UTC time the claim was last flagged (null when it never was).</summary>
    [JsonPropertyName("flaggedAt")]
    public DateTime? FlaggedAt { get; set; }

    /// <summary>UTC time the owner verified the claim (null until verified).</summary>
    [JsonPropertyName("verifiedAt")]
    public DateTime? VerifiedAt { get; set; }

    /// <summary>UTC time the owner rejected the claim (null until rejected).</summary>
    [JsonPropertyName("rejectedAt")]
    public DateTime? RejectedAt { get; set; }

    /// <summary>Why the claim was rejected, when it was. Shown to the customer too.</summary>
    [JsonPropertyName("rejectionReason")]
    public string? RejectionReason { get; set; }

    [JsonPropertyName("verificationEvents")]
    public List<PaymentVerificationEventResponse> VerificationEvents { get; set; } = new();

    /// <summary>
    /// The ledger row this claim was recognised as, once it has been verified (null while the
    /// claim still awaits a decision, and null after a rejection). This is what lets an owner
    /// follow a claim through to the money it became in one click, instead of searching the
    /// ledger for the receipt code.
    /// </summary>
    [JsonPropertyName("financialTransactionId")]
    public Guid? FinancialTransactionId { get; set; }
}

/// <summary>The owner's verification queue plus the counters the screen shows.</summary>
public class PaymentVerificationQueueResponse
{
    [JsonPropertyName("items")]
    public List<PaymentVerificationItemResponse> Items { get; set; } = new();

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    /// <summary>Total pages for the filtered queue.</summary>
    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }

    /// <summary>Payments waiting on a decision (Submitted, UnderReview or Flagged).</summary>
    [JsonPropertyName("awaitingCount")]
    public int AwaitingCount { get; set; }

    [JsonPropertyName("awaitingAmount")]
    public decimal AwaitingAmount { get; set; }

    [JsonPropertyName("submittedCount")]
    public int SubmittedCount { get; set; }

    /// <summary>Claims set aside for a second look (status Flagged).</summary>
    [JsonPropertyName("flaggedCount")]
    public int FlaggedCount { get; set; }

    [JsonPropertyName("underReviewCount")]
    public int UnderReviewCount { get; set; }

    [JsonPropertyName("verifiedCount")]
    public int VerifiedCount { get; set; }

    [JsonPropertyName("rejectedCount")]
    public int RejectedCount { get; set; }
}
