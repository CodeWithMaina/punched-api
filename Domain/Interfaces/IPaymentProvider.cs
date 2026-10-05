namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Provider-agnostic payment provider seam. The core payment domain
/// (<see cref="PunchedApi.Application.Services.PaymentService"/>) depends only on
/// this interface — Daraja-specific code lives behind it in Infrastructure
/// (Infrastructure/Services/Payments/*). Future providers (Paystack, Stripe,
/// Flutterwave, bank transfer) implement the same interface without redesigning
/// the payment domain.
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Which channel this provider serves (must match Payment.Provider).</summary>
    Entities.PaymentProviderKind Kind { get; }

    /// <summary>
    /// True when this provider can push the request to the provider (STK).
    /// False for channels where the customer pays the business directly
    /// (cash, C2B) and Punched only records the lifecycle.
    /// </summary>
    bool InitiatesPush { get; }

    /// <summary>
    /// Initiate a provider interaction for the payment (e.g. STK Push).
    /// For non-pushing providers (Cash, C2B) returns NotSupported — creating the
    /// payment does not require provider interaction for those channels.
    /// </summary>
    Task<ProviderInitiationResult> InitiateAsync(Entities.Payment payment, Entities.BusinessPaymentConfig config, CancellationToken cancellationToken = default);

    /// <summary>Query the provider's view of a transaction (Transaction Status). Best-effort.</summary>
    Task<ProviderStatusResult> GetStatusAsync(string checkoutRequestId, Entities.BusinessPaymentConfig config, CancellationToken cancellationToken = default);

    /// <summary>Cancel an in-flight provider request if the provider supports it. Returns false when unsupported.</summary>
    Task<bool> CancelAsync(Entities.Payment payment, Entities.BusinessPaymentConfig config, CancellationToken cancellationToken = default);

    /// <summary>Refund/reverse a successful payment. Returns false when the provider does not support it for this business.</summary>
    Task<ProviderReversalResult> ReverseAsync(Entities.Payment payment, string reason, Entities.BusinessPaymentConfig config, CancellationToken cancellationToken = default);
}

/// <summary>Result of initiating a provider interaction.</summary>
public class ProviderInitiationResult
{
    public bool Success { get; set; }
    public string? CheckoutRequestId { get; set; }
    public string? MerchantRequestId { get; set; }
    public string? CustomerMessage { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>True when the failure is transient (network timeout, provider outage) and safe to auto-retry.</summary>
    public bool Retryable { get; set; }
}

/// <summary>Result of querying the provider for transaction status.</summary>
public class ProviderStatusResult
{
    public bool Success { get; set; }
    /// <summary>Provider status: "success", "failed", "pending", "unknown".</summary>
    public string Status { get; set; } = "unknown";
    public string? ResultCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ReceiptNumber { get; set; }
}

/// <summary>Result of a refund/reverse request.</summary>
public class ProviderReversalResult
{
    public bool Success { get; set; }
    public bool Supported { get; set; }
    public string? TransactionId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
