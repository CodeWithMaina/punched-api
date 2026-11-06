using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>Contract for the payment domain service (provider-independent).</summary>
public interface IPaymentService
{
    Task<ApiResponse<PaymentResponse>> CreatePaymentAsync(CreatePaymentRequest request, Guid userId, string role, string? idempotencyKey = null);
    Task<ApiResponse<PaymentResponse>> GetPaymentAsync(Guid paymentId, Guid userId, string role);
    Task<ApiResponse<PaymentListResponse>> ListPaymentsAsync(PaymentListQuery query, Guid userId, string role);
    Task<ApiResponse<AppointmentPaymentSummary>> GetAppointmentPaymentSummaryAsync(Guid appointmentId, Guid userId, string role);
    Task<ApiResponse<PaymentDashboardResponse>> GetDashboardAsync(Guid userId, string role);
    Task<ApiResponse<PaymentResponse>> ConfirmCashAsync(Guid paymentId, string? note, Guid userId, string role, string? idempotencyKey = null);
    Task<ApiResponse<PaymentResponse>> RetryPaymentAsync(Guid paymentId, Guid userId, string role, string? idempotencyKey = null);
    Task<ApiResponse<PaymentResponse>> CancelPaymentAsync(Guid paymentId, Guid userId, string role);
    Task<ApiResponse<PaymentResponse>> ReversePaymentAsync(Guid paymentId, string reason, Guid userId);
    Task<int> ExpireStalePaymentsAsync();
    Task<BusinessPaymentConfig> GetOrCreateConfigAsync(Guid businessId);

    // ── Manual M-PESA (customer pays the business's own PayBill/Till) ──

    /// <summary>What a storefront visitor is told about paying this business. Anonymous.</summary>
    Task<ApiResponse<PublicPaymentDetailsResponse>> GetPublicPaymentDetailsAsync(Guid businessId);

    /// <summary>
    /// Record a pasted M-PESA confirmation. Parses and validates the message, checks the
    /// amount against what is owed and rejects a reused receipt code. Never settles the
    /// payment — it only ever reaches <c>Submitted</c>.
    /// </summary>
    Task<ApiResponse<PaymentResponse>> SubmitMpesaMessageAsync(
        Guid paymentId, SubmitMpesaMessageRequest request, Guid userId, string role);

    /// <summary>The owner's verification queue and history, tenant-scoped.</summary>
    Task<ApiResponse<PaymentVerificationQueueResponse>> GetVerificationQueueAsync(
        PaymentVerificationQuery query, Guid userId, string role);

    /// <summary>Mark a submission as being reviewed (non-terminal, idempotent).</summary>
    Task<ApiResponse<PaymentResponse>> MarkVerificationUnderReviewAsync(Guid paymentId, Guid userId, string role);

    /// <summary>
    /// Confirm the money arrived. The only path from a pasted message to a settled
    /// payment; also moves the appointment to <c>confirmed</c> and releases commission.
    /// </summary>
    Task<ApiResponse<PaymentResponse>> VerifyPaymentAsync(
        Guid paymentId, VerifyPaymentRequest request, Guid userId, string role);

    /// <summary>Refuse a submission with a reason the customer is shown.</summary>
    Task<ApiResponse<PaymentResponse>> RejectPaymentAsync(
        Guid paymentId, RejectPaymentRequest request, Guid userId, string role);

    /// <summary>Lapse submissions nobody decided in time (maintenance path).</summary>
    Task<int> ExpireStaleVerificationsAsync();
}

/// <summary>Contract for the business payment configuration service.</summary>
public interface IPaymentConfigService
{
    Task<ApiResponse<PaymentConfigResponse>> GetAsync(Guid businessId);
    Task<ApiResponse<PaymentConfigResponse>> SaveAsync(Guid businessId, SavePaymentConfigRequest request);
}

/// <summary>Contract for the idempotent provider callback processing service.</summary>
public interface IPaymentCallbackService
{
    /// <summary>Process a Daraja STK push result callback. Returns the stored outcome.</summary>
    Task<(string outcome, Guid? paymentId)> ProcessStkCallbackAsync(string rawPayload);

    /// <summary>Process a Daraja C2B confirmation callback. Returns the stored outcome.</summary>
    Task<(string outcome, Guid? paymentId)> ProcessC2BConfirmationAsync(string rawPayload);

    /// <summary>Process a Daraja C2B validation request. Returns (accepted, description, callbackId).</summary>
    Task<(bool accepted, string description)> ProcessC2BValidationAsync(string rawPayload);
}
