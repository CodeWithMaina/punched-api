using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Owns commission configuration, the immutable commission ledger and the
/// commission lifecycle. Caller identity + role are always passed in; the
/// business is resolved server-side and never trusted from request DTOs.
///
/// <para>The accrual/settlement/reversal methods are called by the appointment
/// and payment workflows and are idempotent — a retried completion call, a
/// replayed payment callback or a repeated background job can never produce a
/// second entry for the same economic event.</para>
/// </summary>
public interface ICommissionService
{
    // ── Configuration ───────────────────────────────────────────
    Task<ApiResponse<CommissionConfigResponse>> GetConfigAsync(Guid callerUserId, string role);
    Task<ApiResponse<CommissionConfigResponse>> UpdateConfigAsync(Guid callerUserId, string role, UpdateCommissionConfigRequest request);
    Task<ApiResponse<List<CommissionRateResponse>>> GetRatesAsync(Guid callerUserId, string role);
    Task<ApiResponse<CommissionRateResponse>> UpsertRateAsync(Guid callerUserId, string role, UpsertCommissionRateRequest request);
    Task<ApiResponse<bool>> DeleteRateAsync(Guid callerUserId, string role, Guid rateId);

    // ── Accrual and settlement (called by other modules) ────────
    /// <summary>
    /// Accrues commission entries for an appointment that reached a terminal
    /// status. Returns the number of entries created (0 when nothing qualified).
    /// </summary>
    Task<int> AccrueForAppointmentAsync(Guid appointmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases accrued entries for an appointment whose payment settled.
    /// Returns the number of entries released.
    /// </summary>
    Task<int> SettleForAppointmentAsync(Guid businessId, Guid appointmentId, Guid? paymentId, CancellationToken cancellationToken = default);

    /// <summary>Reverses released/accrued entries because a payment was refunded or reversed.</summary>
    Task<int> ReverseForPaymentAsync(Guid businessId, Guid appointmentId, Guid? paymentId, string reason, CancellationToken cancellationToken = default);

    // ── Queries ─────────────────────────────────────────────────
    Task<ApiResponse<PaginatedResponse<CommissionEntryResponse>>> GetEntriesAsync(Guid callerUserId, string role, CommissionEntriesQueryRequest request);
    Task<ApiResponse<CommissionStaffBalanceResponse>> GetStaffBalanceAsync(Guid callerUserId, string role, Guid staffUserId);
    Task<ApiResponse<List<CommissionStaffBalanceResponse>>> GetBalancesAsync(Guid callerUserId, string role);
    Task<ApiResponse<CommissionReportResponse>> GetReportAsync(Guid callerUserId, string role, DateTime? from, DateTime? to);
    Task<ApiResponse<List<CommissionPayoutResponse>>> GetPayoutsAsync(Guid callerUserId, string role, Guid? staffUserId);

    // ── Lifecycle ───────────────────────────────────────────────
    Task<ApiResponse<List<CommissionEntryResponse>>> ApproveAsync(Guid callerUserId, string role, ApproveCommissionEntriesRequest request);
    Task<ApiResponse<CommissionPayoutResponse>> RecordPayoutAsync(Guid callerUserId, string role, RecordCommissionPayoutRequest request);
    Task<ApiResponse<CommissionEntryResponse>> AdjustAsync(Guid callerUserId, string role, CommissionAdjustmentRequest request);
    Task<ApiResponse<CommissionEntryResponse>> ReverseAsync(Guid callerUserId, string role, Guid entryId, CommissionReversalRequest request);
    Task<ApiResponse<CommissionEntryResponse>> DisputeAsync(Guid callerUserId, string role, Guid entryId, CommissionDisputeRequest request);
    Task<ApiResponse<CommissionEntryResponse>> ResolveDisputeAsync(Guid callerUserId, string role, Guid entryId, ResolveCommissionDisputeRequest request);
}
