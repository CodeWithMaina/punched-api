using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PunchedApi.API.Filters;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

// Phase 4 owner configuration (plan §13.3, §15): settings, locations, QR.
// Base route prefix /v1/businesses/me/attendance/... — server-scoped business only (§10.2).
public partial class BusinessController
{
    /// <summary>GET v1/businesses/me/attendance/settings — effective policy + readiness.</summary>
    [HttpGet("me/attendance/settings")]
    [Authorize(Roles = "Business")]
    [RequireModule("attendance")]
    [ProducesResponseType(typeof(ApiResponse<AttendanceSettingsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAttendanceSettings()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _attendanceLocationService.GetSettingsAsync(userId.Value);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>PUT v1/businesses/me/attendance/settings — pause/configure mode + verifications.</summary>
    [HttpPut("me/attendance/settings")]
    [Authorize(Roles = "Business")]
    [RequireModule("attendance")]
    [ProducesResponseType(typeof(ApiResponse<AttendanceSettingsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateAttendanceSettings([FromBody] AttendanceSettingsRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _attendanceLocationService.UpdateSettingsAsync(userId.Value, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>GET v1/businesses/me/attendance/locations — list with live-QR state.</summary>
    [HttpGet("me/attendance/locations")]
    [Authorize(Roles = "Business")]
    [RequireModule("attendance")]
    [ProducesResponseType(typeof(ApiResponse<List<AttendanceLocationSummaryResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAttendanceLocations([FromQuery] bool includeInactive = false)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _attendanceLocationService.ListLocationsAsync(userId.Value, includeInactive);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>POST v1/businesses/me/attendance/locations — create (D8: unique name).</summary>
    [HttpPost("me/attendance/locations")]
    [Authorize(Roles = "Business")]
    [RequireModule("attendance")]
    [ProducesResponseType(typeof(ApiResponse<AttendanceLocationDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateAttendanceLocation([FromBody] CreateAttendanceLocationRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _attendanceLocationService.CreateLocationAsync(userId.Value, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>GET v1/businesses/me/attendance/locations/{id} — detail (never the token).</summary>
    [HttpGet("me/attendance/locations/{locationId:guid}")]
    [Authorize(Roles = "Business")]
    [RequireModule("attendance")]
    [ProducesResponseType(typeof(ApiResponse<AttendanceLocationDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAttendanceLocation(Guid locationId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _attendanceLocationService.GetLocationAsync(userId.Value, locationId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>PUT v1/businesses/me/attendance/locations/{id} — rename / deactivate.</summary>
    [HttpPut("me/attendance/locations/{locationId:guid}")]
    [Authorize(Roles = "Business")]
    [RequireModule("attendance")]
    [ProducesResponseType(typeof(ApiResponse<AttendanceLocationDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateAttendanceLocation(Guid locationId, [FromBody] UpdateAttendanceLocationRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _attendanceLocationService.UpdateLocationAsync(userId.Value, locationId, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>DELETE v1/businesses/me/attendance/locations/{id} — refused with LOCATION_HAS_HISTORY.</summary>
    [HttpDelete("me/attendance/locations/{locationId:guid}")]
    [Authorize(Roles = "Business")]
    [RequireModule("attendance")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
        public async Task<IActionResult> DeleteAttendanceLocation(Guid locationId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _attendanceLocationService.DeleteLocationAsync(userId.Value, locationId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    // ── QR CREDENTIALS ─────────────────────────────────────────────────

    /// <summary>POST …/locations/{id}/qr — mint a printed credential. Token returned ONCE.</summary>
    [HttpPost("me/attendance/locations/{locationId:guid}/qr")]
    [Authorize(Roles = "Business")]
    [RequireModule("attendance")]
    [ProducesResponseType(typeof(ApiResponse<AttendanceQrCredentialResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> MintAttendanceQr(Guid locationId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _attendanceLocationService.MintQrAsync(userId.Value, locationId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>POST …/locations/{id}/qr/regenerate — distinct copy + audit QR_REGENERATED.</summary>
    [HttpPost("me/attendance/locations/{locationId:guid}/qr/regenerate")]
    [Authorize(Roles = "Business")]
    [RequireModule("attendance")]
    [ProducesResponseType(typeof(ApiResponse<AttendanceQrCredentialResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RegenerateAttendanceQr(Guid locationId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _attendanceLocationService.RotateQrAsync(userId.Value, locationId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>DELETE …/locations/{id}/qr — revoke the live credential.</summary>
    [HttpDelete("me/attendance/locations/{locationId:guid}/qr")]
    [Authorize(Roles = "Business")]
    [RequireModule("attendance")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RevokeAttendanceQr(Guid locationId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _attendanceLocationService.RevokeQrAsync(userId.Value, locationId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    // ── TEAM OVERVIEW + STAFF HISTORY ──────────────────────────────────

    /// <summary>GET v1/businesses/me/attendance/overview — who is clocked in now.</summary>
    [HttpGet("me/attendance/overview")]
    [Authorize(Roles = "Business")]
    [RequireModule("attendance")]
    [ProducesResponseType(typeof(ApiResponse<AttendanceOverviewResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAttendanceOverview([FromQuery] DateOnly? date = null)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _attendanceLocationService.GetOverviewAsync(userId.Value, date);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>GET v1/businesses/me/attendance/staff/{id}/history — owner drill-down (business-scoped).</summary>
    [HttpGet("me/attendance/staff/{staffUserId:guid}/history")]
    [Authorize(Roles = "Business")]
    [RequireModule("attendance")]
    [ProducesResponseType(typeof(ApiResponse<PaginatedResponse<AttendanceHistoryItem>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAttendanceStaffHistory(Guid staffUserId, [FromQuery] AttendanceHistoryQuery query)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _attendanceLocationService.GetStaffHistoryAsync(
            userId.Value, staffUserId, query ?? new AttendanceHistoryQuery());
        return result.Success ? Ok(result) : MapFailure(result);
    }
}


