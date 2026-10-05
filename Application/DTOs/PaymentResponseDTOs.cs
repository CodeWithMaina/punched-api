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

    [JsonPropertyName("attempts")]
    public List<PaymentAttemptResponse> Attempts { get; set; } = new();
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

    [JsonPropertyName("amountPaid")]
    public decimal AmountPaid { get; set; }

    [JsonPropertyName("balance")]
    public decimal Balance { get; set; }

    /// <summary>"unpaid" | "partially_paid" | "paid".</summary>
    [JsonPropertyName("paymentStatus")]
    public string PaymentStatus { get; set; } = "unpaid";

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

    [JsonPropertyName("unmatchedCallbackCount")]
    public int UnmatchedCallbackCount { get; set; }
}
