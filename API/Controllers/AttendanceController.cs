using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Filters;
using PunchedApi.Application.Attendance;
using PunchedApi.Application.DTOs;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Staff-facing attendance hot path (plan §13.2) — status, clock-in,
/// clock-out, history. Business owners may also clock (the owner counts the
/// shop floor). Base route: /v1/attendance. Direction is NEVER a client
/// choice: the server derives it from persisted session state (§5.3–§5.4).
/// Error-code → HTTP mapping is the StampController switch pattern (§9.6);
/// MODULE_DISABLED is emitted by [RequireModule], never by this controller.
/// </summary>
[ApiController]
[Route("v1/attendance")]
[Produces("application/json")]
[Authorize(Roles = "Business,Staff")]
[RequireModule("attendance")]
[EnableRateLimiting("general")]
public class AttendanceController : ControllerBase
{
    private readonly IAttendanceService _attendanceService;

    public AttendanceController(IAttendanceService attendanceService)
    {
        _attendanceService = attendanceService;
    }

    /// <summary>
    /// Current attendance state for the authenticated user. No route, query
    /// or body identifiers — there is nothing to spoof. Safe GET: no
    /// idempotency, deliberately no output caching.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(ApiResponse<AttendanceStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Status()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _attendanceService.StatusAsync(userId.Value);
        return result.Success ? Ok(result) : MapError(result);
    }

    /// <summary>
    /// Clock in by scanning the business's printed location QR. One QR serves
    /// both verbs — the server opens a session only if none is open. Honors
    /// the optional Idempotency-Key header for safe retries. Rate-limited to
    /// 60 scans per hour per (IP + user).
    /// </summary>
    [HttpPost("clock-in")]
    [EnableRateLimiting("attendance-clock")]
    [ProducesResponseType(typeof(ApiResponse<AttendanceStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ClockIn([FromBody] AttendanceClockRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var idempotencyKey = ReadIdempotencyKey();
        var result = await _attendanceService.ClockInAsync(userId.Value, request, idempotencyKey);
        return result.Success ? Ok(result) : MapError(result);
    }

    /// <summary>
    /// Clock out with the SAME physical QR — the server finds the open
    /// session, closes it and computes worked_minutes. No open session ⇒ 409
    /// NOT_CLOCKED_IN. Same contract as clock-in otherwise.
    /// </summary>
    [HttpPost("clock-out")]
    [EnableRateLimiting("attendance-clock")]
    [ProducesResponseType(typeof(ApiResponse<AttendanceStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ClockOut([FromBody] AttendanceClockRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var idempotencyKey = ReadIdempotencyKey();
        var result = await _attendanceService.ClockOutAsync(userId.Value, request, idempotencyKey);
        return result.Success ? Ok(result) : MapError(result);
    }

    /// <summary>
    /// The authenticated user's own attendance history — always scoped to the
    /// actor's business AND staff user id; this surface never returns another
    /// staff member's rows, even if a query parameter is supplied (§13.2).
    /// </summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(ApiResponse<PaginatedResponse<AttendanceHistoryItem>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> History([FromQuery] AttendanceHistoryQuery query)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _attendanceService.HistoryAsync(userId.Value, query ?? new AttendanceHistoryQuery());
        return result.Success ? Ok(result) : MapError(result);
    }

    /// <summary>The §9.6 error-code contract, mapped by a switch (StampController pattern).</summary>
    private IActionResult MapError<T>(ApiResponse<T> result) =>
        result.Error?.Code switch
        {
            // 400 — actionable QR / payload problems
            "INVALID_QR" or "QR_REVOKED" or "QR_WRONG_ORGANIZATION" or "LOCATION_INACTIVE" or
            "INVALID_DATE_RANGE" or "INVALID_EVENT_TYPE" => BadRequest(result),

            // 401 — authenticated principal missing (never reached via [Authorize])
            "UNAUTHORIZED" => Unauthorized(result),

            // 403 — role / scope / configuration
            "FORBIDDEN" or "NOT_LINKED" or "FORBIDDEN_SCOPE" or "MODULE_DISABLED" =>
                StatusCode(StatusCodes.Status403Forbidden, result),

            // 404 — non-enumerable, matching the rest of the API
            "NOT_FOUND" or "STAFF_NOT_FOUND" => NotFound(result),

            // 409 — state / idempotency / availability conflicts
            "ALREADY_CLOCKED_IN" or "NOT_CLOCKED_IN" or "ATTENDANCE_DISABLED" or
            "ATTENDANCE_NOT_CONFIGURED" or "VERIFICATION_METHOD_UNAVAILABLE" or
            "IDEMPOTENCY_CONFLICT" => StatusCode(StatusCodes.Status409Conflict, result),

            _ => BadRequest(result)
        };

    private string? ReadIdempotencyKey() =>
        Request.Headers.TryGetValue("Idempotency-Key", out var key) ? key.ToString() : null;

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}