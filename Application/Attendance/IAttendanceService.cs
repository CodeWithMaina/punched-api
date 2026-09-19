using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Attendance;

/// <summary>
/// Staff-facing attendance hot path (plan §5, §9, §13.2): clock-in /
/// clock-out through the verification engine with persisted sessions,
/// idempotency replay and audit; plus the read-only status and history views.
/// The service NEVER sets HTTP status codes — the controller maps
/// <c>error.code</c> → status with the StampController switch (§9.6), and
/// MODULE_DISABLED is emitted by <c>[RequireModule]</c> only, never here.
/// </summary>
public interface IAttendanceService
{
    /// <summary>GET v1/attendance/status — read-only, no idempotency, no caching.</summary>
    Task<ApiResponse<AttendanceStatusResponse>> StatusAsync(Guid actorUserId);

    /// <summary>POST v1/attendance/clock-in.</summary>
    Task<ApiResponse<AttendanceStatusResponse>> ClockInAsync(
        Guid actorUserId, AttendanceClockRequest request, string? idempotencyKey = null);

    /// <summary>POST v1/attendance/clock-out — same physical QR, direction from session state.</summary>
    Task<ApiResponse<AttendanceStatusResponse>> ClockOutAsync(
        Guid actorUserId, AttendanceClockRequest request, string? idempotencyKey = null);

    /// <summary>GET v1/attendance/history — paged, always scoped to the actor.</summary>
    Task<ApiResponse<PaginatedResponse<AttendanceHistoryItem>>> HistoryAsync(
        Guid actorUserId, AttendanceHistoryQuery query);
}