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
