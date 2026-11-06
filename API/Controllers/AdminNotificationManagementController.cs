using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Services;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Platform notification management — the operational module behind
/// <c>/admin/notifications</c>.
///
/// Read endpoints project masked, paginated views of the delivery ledger; write
/// endpoints (retry, cancel, channel override, template reset, provider test) are
/// audited through the append-only audit trail. Every endpoint is gated by the
/// <c>PlatformAdmin</c> policy, so a business token is refused regardless of what
/// the console renders.
/// </summary>
[ApiController]
[Route("v1/admin/notifications")]
[Authorize(Policy = MembershipPolicies.PlatformAdmin)]
[EnableRateLimiting("general")]
[Produces("application/json")]
public sealed class AdminNotificationManagementController : ControllerBase
{
    private readonly AdminNotificationManagementService _service;
    private readonly ILogger<AdminNotificationManagementController> _logger;

    public AdminNotificationManagementController(
        AdminNotificationManagementService service,
        ILogger<AdminNotificationManagementController> logger)
    {
        _service = service;
        _logger = logger;
    }

    // ── Log explorer ────────────────────────────────────────

    /// <summary>Server-side filtered, paginated notification log explorer.</summary>
    [HttpGet("logs")]
    [ProducesResponseType(typeof(ApiResponse<PaginatedResponse<AdminNotificationLogItemDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetLogs(
        [FromQuery] string? search = null,
        [FromQuery] Guid? businessId = null,
        [FromQuery] string? channel = null,
        [FromQuery] string? notificationType = null,
        [FromQuery] string? status = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] Guid? notificationId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        NoStore();
        if (channel is not null && !NotificationDefaults.AllChannels.Contains(channel))
            return BadRequest(ApiResponse<PaginatedResponse<AdminNotificationLogItemDto>>.Fail(
                "VALIDATION_ERROR", $"Channel '{channel}' is not supported."));

        var result = await _service.GetLogsAsync(
            search, businessId, channel, notificationType, status,
            fromUtc, toUtc, notificationId, page, pageSize, cancellationToken);

        return Ok(ApiResponse<PaginatedResponse<AdminNotificationLogItemDto>>.Ok(result));
    }

    /// <summary>One notification record with attempt history and a sanitized payload.</summary>
    [HttpGet("logs/{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AdminNotificationLogDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLogDetail(Guid id, CancellationToken cancellationToken = default)
    {
        NoStore();
        var detail = await _service.GetLogDetailAsync(id, cancellationToken);
        if (detail is null)
            return NotFound(ApiResponse<AdminNotificationLogDetailDto>.Fail("NOT_FOUND", "Notification not found."));

        return Ok(ApiResponse<AdminNotificationLogDetailDto>.Ok(detail));
    }

    /// <summary>Requeues a retry-exhausted notification.</summary>
    [HttpPost("logs/{id:guid}/retry")]
    [ProducesResponseType(typeof(ApiResponse<AdminNotificationActionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken = default)
    {
        NoStore();
        var result = await _service.RetryAsync(id, GetUserId(), cancellationToken);
        if (!result.Accepted && result.Code == "NOT_FOUND")
            return NotFound(ApiResponse<AdminNotificationActionResponse>.Fail(result.Code, result.Message));

        if (!result.Accepted)
            return Conflict(ApiResponse<AdminNotificationActionResponse>.Fail(result.Code, result.Message));

        return Ok(ApiResponse<AdminNotificationActionResponse>.Ok(result));
    }

    /// <summary>Cancels a notification that has not yet been claimed for delivery.</summary>
    [HttpPost("logs/{id:guid}/cancel")]
    [ProducesResponseType(typeof(ApiResponse<AdminNotificationActionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(
        Guid id,
        [FromBody] AdminNotificationCancelRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        NoStore();
        var result = await _service.CancelAsync(id, GetUserId(), request?.Reason, cancellationToken);
        if (!result.Accepted && result.Code == "NOT_FOUND")
            return NotFound(ApiResponse<AdminNotificationActionResponse>.Fail(result.Code, result.Message));

        if (!result.Accepted)
            return Conflict(ApiResponse<AdminNotificationActionResponse>.Fail(result.Code, result.Message));

        return Ok(ApiResponse<AdminNotificationActionResponse>.Ok(result));
    }

    // ── Delivery monitoring ─────────────────────────────────

    /// <summary>Queue depth, backlog, worker health and per-channel health.</summary>
    [HttpGet("delivery")]
    [ProducesResponseType(typeof(ApiResponse<AdminNotificationDeliveryMonitoringDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDelivery(CancellationToken cancellationToken = default)
    {
        NoStore();
        var result = await _service.GetDeliveryMonitoringAsync(cancellationToken);
        return Ok(ApiResponse<AdminNotificationDeliveryMonitoringDto>.Ok(result));
    }

    // ── Providers ───────────────────────────────────────────

    /// <summary>Masked provider configuration plus delivery-evidence health.</summary>
    [HttpGet("providers")]
    [ProducesResponseType(typeof(ApiResponse<List<AdminNotificationProviderDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProviders(CancellationToken cancellationToken = default)
    {
        NoStore();
        var result = await _service.GetProvidersAsync(cancellationToken);
        return Ok(ApiResponse<List<AdminNotificationProviderDto>>.Ok(result));
    }

    /// <summary>Runs a live connectivity check for one channel. No message is sent.</summary>
    [HttpPost("providers/{channel}/test")]
    [ProducesResponseType(typeof(ApiResponse<AdminNotificationProviderTestResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TestProvider(string channel, CancellationToken cancellationToken = default)
    {
        NoStore();
        if (!NotificationDefaults.AllChannels.Contains(channel))
            return BadRequest(ApiResponse<AdminNotificationProviderTestResultDto>.Fail(
                "VALIDATION_ERROR", $"Channel '{channel}' is not supported."));

        var result = await _service.TestProviderAsync(channel, GetUserId(), cancellationToken);
        return Ok(ApiResponse<AdminNotificationProviderTestResultDto>.Ok(result));
    }

    // ── Global templates ────────────────────────────────────

    /// <summary>The platform's code-defined templates, variables and override counts.</summary>
    [HttpGet("templates")]
    [ProducesResponseType(typeof(ApiResponse<List<AdminNotificationTemplateDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTemplates(CancellationToken cancellationToken = default)
    {
        NoStore();
        var result = await _service.GetGlobalTemplatesAsync(cancellationToken);
        return Ok(ApiResponse<List<AdminNotificationTemplateDto>>.Ok(result));
    }

    // ── Per-business management ─────────────────────────────

    /// <summary>Businesses with their notification posture.</summary>
    [HttpGet("businesses")]
    [ProducesResponseType(typeof(ApiResponse<PaginatedResponse<AdminNotificationBusinessSummaryDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBusinesses(
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        NoStore();
        var result = await _service.GetBusinessesAsync(search, page, pageSize, cancellationToken);
        return Ok(ApiResponse<PaginatedResponse<AdminNotificationBusinessSummaryDto>>.Ok(result));
    }

    /// <summary>Full notification posture for one business.</summary>
    [HttpGet("businesses/{businessId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AdminNotificationBusinessDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBusiness(Guid businessId, CancellationToken cancellationToken = default)
    {
        NoStore();
        var detail = await _service.GetBusinessDetailAsync(businessId, cancellationToken);
        if (detail is null)
            return NotFound(ApiResponse<AdminNotificationBusinessDetailDto>.Fail("NOT_FOUND", "Business not found."));

        return Ok(ApiResponse<AdminNotificationBusinessDetailDto>.Ok(detail));
    }

    /// <summary>Explicit, audited enable/disable of a channel for one business.</summary>
    [HttpPut("businesses/{businessId:guid}/channels/{channel}")]
    [ProducesResponseType(typeof(ApiResponse<AdminNotificationBusinessDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetBusinessChannel(
        Guid businessId,
        string channel,
        [FromBody] AdminNotificationChannelOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        NoStore();
        if (!NotificationDefaults.AllChannels.Contains(channel))
            return BadRequest(ApiResponse<AdminNotificationBusinessDetailDto>.Fail(
                "VALIDATION_ERROR", $"Channel '{channel}' is not supported."));

        var detail = await _service.SetBusinessChannelAsync(
            businessId, channel, request.Enabled, GetUserId(), request.Reason, cancellationToken);

        if (detail is null)
            return NotFound(ApiResponse<AdminNotificationBusinessDetailDto>.Fail("NOT_FOUND", "Business not found."));

        return Ok(ApiResponse<AdminNotificationBusinessDetailDto>.Ok(detail));
    }

    /// <summary>Resets every business template override back to the platform defaults.</summary>
    [HttpPost("businesses/{businessId:guid}/templates/reset")]
    [ProducesResponseType(typeof(ApiResponse<AdminNotificationBusinessDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetBusinessTemplates(
        Guid businessId,
        CancellationToken cancellationToken = default)
    {
        NoStore();
        var detail = await _service.ResetBusinessTemplatesAsync(businessId, GetUserId(), cancellationToken);
        if (detail is null)
            return NotFound(ApiResponse<AdminNotificationBusinessDetailDto>.Fail("NOT_FOUND", "Business not found."));

        return Ok(ApiResponse<AdminNotificationBusinessDetailDto>.Ok(detail));
    }

    // ── Analytics ───────────────────────────────────────────

    /// <summary>Delivery analytics over a date range, computed from real records.</summary>
    [HttpGet("analytics")]
    [ProducesResponseType(typeof(ApiResponse<AdminNotificationAnalyticsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetAnalytics(
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] int days = 30,
        CancellationToken cancellationToken = default)
    {
        NoStore();
        var to = (toUtc ?? DateTime.UtcNow).ToUniversalTime();
        var from = fromUtc?.ToUniversalTime() ?? to.AddDays(-Math.Clamp(days, 1, 365));

        if (to <= from)
            return BadRequest(ApiResponse<AdminNotificationAnalyticsDto>.Fail(
                "VALIDATION_ERROR", "The end of the range must be after its start."));

        var result = await _service.GetAnalyticsAsync(from, to, cancellationToken);
        return Ok(ApiResponse<AdminNotificationAnalyticsDto>.Ok(result));
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private void NoStore() => Response.Headers.CacheControl = "no-store";
}
