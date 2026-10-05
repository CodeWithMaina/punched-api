using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Application.Notifications;

namespace PunchedApi.API.Controllers;

[ApiController]
[Route("v1/admin/notifications")]
[Authorize(Roles = "Admin")]
[EnableRateLimiting("general")]
[Produces("application/json")]
public sealed class AdminNotificationsController : ControllerBase
{
    private readonly AdminNotificationOperationsService _service;

    public AdminNotificationsController(AdminNotificationOperationsService service) => _service = service;

    [HttpGet("overview")]
    [ProducesResponseType(typeof(ApiResponse<AdminNotificationOperationsOverviewDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOverview([FromQuery] string range = "24h", CancellationToken cancellationToken = default)
    {
        NoStore();
        if (!AdminNotificationOperationsService.IsSupportedRange(range))
            return BadRequest(ApiResponse<AdminNotificationOperationsOverviewDto>.Fail(
                "VALIDATION_ERROR", "Range must be one of 1h, 24h, 7d, or 30d."));

        var result = await _service.GetOverviewAsync(range, cancellationToken);
        return Ok(ApiResponse<AdminNotificationOperationsOverviewDto>.Ok(result));
    }

    [HttpGet("trends")]
    [ProducesResponseType(typeof(ApiResponse<AdminNotificationTrendsDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTrends([FromQuery] string range = "24h", CancellationToken cancellationToken = default)
    {
        NoStore();
        if (!AdminNotificationOperationsService.IsSupportedRange(range))
            return BadRequest(ApiResponse<AdminNotificationTrendsDto>.Fail(
                "VALIDATION_ERROR", "Range must be one of 1h, 24h, 7d, or 30d."));

        var result = await _service.GetTrendsAsync(range, cancellationToken);
        return Ok(ApiResponse<AdminNotificationTrendsDto>.Ok(result));
    }

    [HttpGet("failures")]
    [ProducesResponseType(typeof(ApiResponse<PaginatedResponse<AdminNotificationFailureDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFailures(
        [FromQuery] string range = "24h",
        [FromQuery] string? channel = null,
        [FromQuery] string? classification = null,
        [FromQuery] bool? retryEligible = null,
        [FromQuery] string? notificationType = null,
        [FromQuery] Guid? businessId = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        NoStore();
        if (!AdminNotificationOperationsService.IsSupportedRange(range))
            return BadRequest(ApiResponse<PaginatedResponse<AdminNotificationFailureDto>>.Fail(
                "VALIDATION_ERROR", "Range must be one of 1h, 24h, 7d, or 30d."));
        if (channel is not null && !NotificationDefaults.AllChannels.Contains(channel))
            return BadRequest(ApiResponse<PaginatedResponse<AdminNotificationFailureDto>>.Fail(
                "VALIDATION_ERROR", "Channel is not supported."));
        if (classification is not null && classification is not ("retry_exhausted" or "permanent" or "unknown"))
            return BadRequest(ApiResponse<PaginatedResponse<AdminNotificationFailureDto>>.Fail(
                "VALIDATION_ERROR", "Failure classification is not supported."));
        if (notificationType?.Length > 100 || search?.Length > 100)
            return BadRequest(ApiResponse<PaginatedResponse<AdminNotificationFailureDto>>.Fail(
                "VALIDATION_ERROR", "Search and notification type are limited to 100 characters."));

        var result = await _service.GetFailuresAsync(new AdminNotificationFailureQuery
        {
            Range = range,
            Channel = channel,
            Classification = classification,
            RetryEligible = retryEligible,
            NotificationType = notificationType,
            BusinessId = businessId,
            Search = search,
            Page = Math.Max(page, 1),
            PageSize = Math.Clamp(pageSize, 1, 100)
        }, cancellationToken);
        return Ok(ApiResponse<PaginatedResponse<AdminNotificationFailureDto>>.Ok(result));
    }

    [HttpGet("failures/{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AdminNotificationFailureDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFailure(Guid id, CancellationToken cancellationToken = default)
    {
        NoStore();
        var result = await _service.GetFailureDetailAsync(id, cancellationToken);
        if (result is null)
            return NotFound(ApiResponse<AdminNotificationFailureDetailDto>.Fail("NOT_FOUND", "Failed notification not found."));
        return Ok(ApiResponse<AdminNotificationFailureDetailDto>.Ok(result));
    }

    [HttpPost("{id:guid}/retry")]
    [ProducesResponseType(typeof(ApiResponse<AdminNotificationRetryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken = default)
    {
        NoStore();
        var decision = await _service.RetryAsync(id, cancellationToken);
        if (!decision.Found)
            return NotFound(ApiResponse<AdminNotificationRetryResponse>.Fail(decision.Code, decision.Message));
        if (!decision.WasAccepted)
            return Conflict(ApiResponse<AdminNotificationRetryResponse>.Fail(decision.Code, decision.Message));

        return Ok(ApiResponse<AdminNotificationRetryResponse>.Ok(new AdminNotificationRetryResponse
        {
            Id = id,
            Accepted = true,
            Message = decision.Message
        }));
    }

    private void NoStore() => Response.Headers.CacheControl = "no-store";
}