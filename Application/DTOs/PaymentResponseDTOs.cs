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

    [JsonPropertyName("mpesaPayeeName")]
    public string? MpesaPayeeName { get; set; }

    [JsonPropertyName("mpesaInstructions")]
    public string? MpesaInstructions { get; set; }

    [JsonPropertyName("bookingFeePercentage")]
    public decimal BookingFeePercentage { get; set; }

    /// <summary>True when the manual channel is switched on AND fully configured — what the storefront keys off.</summary>
    [JsonPropertyName("hasManualMpesaDetails")]
    public bool HasManualMpesaDetails { get; set; }
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

    /// <summary>"paybill" | "till" | null — which shape the customer should use.</summary>
    [JsonPropertyName("accountType")]
    public string? AccountType { get; set; }

    [JsonPropertyName("paybillNumber")]
    public string? PaybillNumber { get; set; }

    [JsonPropertyName("paybillAccount")]
    public string? PaybillAccount { get; set; }

    [JsonPropertyName("tillNumber")]
    public string? TillNumber { get; set; }

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

    [JsonPropertyName("serviceNames")]
    public List<string> ServiceNames { get; set; } = new();

    /// <summary>What the business is owed for this payment (server-computed).</summary>
    [JsonPropertyName("expectedAmount")]
    public decimal ExpectedAmount { get; set; }

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

    [JsonPropertyName("verificationEvents")]
    public List<PaymentVerificationEventResponse> VerificationEvents { get; set; } = new();
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

    /// <summary>Payments waiting on a decision (Submitted or UnderReview).</summary>
    [JsonPropertyName("awaitingCount")]
    public int AwaitingCount { get; set; }

    [JsonPropertyName("awaitingAmount")]
    public decimal AwaitingAmount { get; set; }

    [JsonPropertyName("verifiedCount")]
    public int VerifiedCount { get; set; }

    [JsonPropertyName("rejectedCount")]
    public int RejectedCount { get; set; }
}
