using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Business notification settings — the workspace behind
/// <c>/dashboard/settings?section=notifications</c>.
///
/// Every endpoint is scoped to a business id in the route and re-checks that the
/// authenticated caller owns (or, for a platform administrator, may manage) that
/// business. A client-supplied business id is never trusted on its own.
/// </summary>
[ApiController]
[Route("v1/businesses/{businessId:guid}/notifications")]
[Authorize(Policy = MembershipPolicies.OwnerOrStaffOrAdmin)]
[EnableRateLimiting("general")]
[Produces("application/json")]
public sealed class BusinessNotificationsController : ControllerBase
{
    private readonly BusinessNotificationSettingsService _service;
    private readonly IBusinessScopeResolver _scopeResolver;
    private readonly ILogger<BusinessNotificationsController> _logger;

    public BusinessNotificationsController(
        BusinessNotificationSettingsService service,
        IBusinessScopeResolver scopeResolver,
        ILogger<BusinessNotificationsController> logger)
    {
        _service = service;
        _scopeResolver = scopeResolver;
        _logger = logger;
    }

    // ── Overview ────────────────────────────────────────────

    /// <summary>Notification system status and delivery statistics for this business.</summary>
    [HttpGet("overview")]
    [ProducesResponseType(typeof(ApiResponse<BusinessNotificationOverviewDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetOverview(
        Guid businessId,
        [FromQuery] string range = "30d",
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        CancellationToken cancellationToken = default)
    {
        NoStore();
        await EnsureTenantAccessAsync(businessId, cancellationToken);

        var to = (toUtc ?? DateTime.UtcNow).ToUniversalTime();
        var from = fromUtc?.ToUniversalTime() ?? range switch
        {
            "today" => to.Date,
            "7d" => to.AddDays(-7),
            "30d" => to.AddDays(-30),
            "90d" => to.AddDays(-90),
            _ => to.AddDays(-30),
        };

        if (to <= from)
            return BadRequest(ApiResponse<BusinessNotificationOverviewDto>.Fail(
                "VALIDATION_ERROR", "The end of the range must be after its start."));

        var result = await _service.GetOverviewAsync(businessId, from, to, cancellationToken);
        return Ok(ApiResponse<BusinessNotificationOverviewDto>.Ok(result));
    }

    // ── Channels ────────────────────────────────────────────

    /// <summary>Per-channel configuration, with platform restrictions made explicit.</summary>
    [HttpGet("channels")]
    [ProducesResponseType(typeof(ApiResponse<List<BusinessNotificationChannelDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetChannels(Guid businessId, CancellationToken cancellationToken = default)
    {
        NoStore();
        await EnsureTenantAccessAsync(businessId, cancellationToken);
        var result = await _service.GetChannelsAsync(businessId, cancellationToken);
        return Ok(ApiResponse<List<BusinessNotificationChannelDto>>.Ok(result));
    }

    /// <summary>Updates sender identity, branding and enabled channels.</summary>
    [HttpPut("channels")]
    [ProducesResponseType(typeof(ApiResponse<List<BusinessNotificationChannelDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateChannels(
        Guid businessId,
        [FromBody] UpdateBusinessNotificationChannelsRequest request,
        CancellationToken cancellationToken = default)
    {
        NoStore();
        await EnsureTenantAccessAsync(businessId, cancellationToken);

        if (request.ReplyTo is not null && !LooksLikeEmail(request.ReplyTo))
            return BadRequest(ApiResponse<List<BusinessNotificationChannelDto>>.Fail(
                "VALIDATION_ERROR", "The reply-to address is not a valid email address."));

        if (request.ContactEmail is not null && !LooksLikeEmail(request.ContactEmail))
            return BadRequest(ApiResponse<List<BusinessNotificationChannelDto>>.Fail(
                "VALIDATION_ERROR", "The contact email is not a valid email address."));

        if (request.PrimaryColor is not null
            && !System.Text.RegularExpressions.Regex.IsMatch(
                request.PrimaryColor.Trim(), "^#([A-Fa-f0-9]{6}|[A-Fa-f0-9]{3})$"))
        {
            return BadRequest(ApiResponse<List<BusinessNotificationChannelDto>>.Fail(
                "VALIDATION_ERROR", "The brand colour must be a hex value such as #2563EB."));
        }

        try
        {
            var result = await _service.UpdateChannelsAsync(businessId, request, GetUserId(), cancellationToken);
            return Ok(ApiResponse<List<BusinessNotificationChannelDto>>.Ok(result));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(ApiResponse<List<BusinessNotificationChannelDto>>.Fail(
                "NOT_FOUND", "Business not found."));
        }
    }

    // ── Preferences ─────────────────────────────────────────

    /// <summary>The business's resolved (category × channel) preference matrix.</summary>
    [HttpGet("preferences")]
    [ProducesResponseType(typeof(ApiResponse<List<BusinessNotificationPreferenceDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPreferences(Guid businessId, CancellationToken cancellationToken = default)
    {
        NoStore();
        await EnsureTenantAccessAsync(businessId, cancellationToken);
        var result = await _service.GetPreferencesAsync(businessId, cancellationToken);
        return Ok(ApiResponse<List<BusinessNotificationPreferenceDto>>.Ok(result));
    }

    /// <summary>Replaces the business's notification preferences.</summary>
    [HttpPut("preferences")]
    [ProducesResponseType(typeof(ApiResponse<List<BusinessNotificationPreferenceDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdatePreferences(
        Guid businessId,
        [FromBody] UpdateBusinessNotificationPreferencesRequest request,
        CancellationToken cancellationToken = default)
    {
        NoStore();
        await EnsureTenantAccessAsync(businessId, cancellationToken);
        var result = await _service.UpdatePreferencesAsync(businessId, request, GetUserId(), cancellationToken);
        return Ok(ApiResponse<List<BusinessNotificationPreferenceDto>>.Ok(result));
    }

    // ── Templates + variables ───────────────────────────────

    /// <summary>The template catalogue for this business.</summary>
    [HttpGet("templates")]
    [ProducesResponseType(typeof(ApiResponse<List<BusinessNotificationTemplateDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetTemplates(
        Guid businessId,
        [FromQuery] string? search = null,
        [FromQuery] string? category = null,
        [FromQuery] string? channel = null,
        CancellationToken cancellationToken = default)
    {
        NoStore();
        await EnsureTenantAccessAsync(businessId, cancellationToken);

        if (channel is not null && !NotificationDefaults.AllChannels.Contains(channel.Trim().ToLowerInvariant()))
            return BadRequest(ApiResponse<List<BusinessNotificationTemplateDto>>.Fail(
                "VALIDATION_ERROR", $"Channel '{channel}' is not supported."));

        var result = await _service.GetTemplatesAsync(
            businessId, search, category, channel?.Trim().ToLowerInvariant(), cancellationToken);

        return Ok(ApiResponse<List<BusinessNotificationTemplateDto>>.Ok(result));
    }

    /// <summary>The variables a business may insert for one notification type.</summary>
    [HttpGet("variables/{type}")]
    [ProducesResponseType(typeof(ApiResponse<List<BusinessNotificationVariableDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetVariables(
        Guid businessId,
        string type,
        CancellationToken cancellationToken = default)
    {
        NoStore();
        await EnsureTenantAccessAsync(businessId, cancellationToken);

        try
        {
            var variables = _service.GetVariables(type.Trim());
            return Ok(ApiResponse<List<BusinessNotificationVariableDto>>.Ok(variables.ToList()));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<List<BusinessNotificationVariableDto>>.Fail(
                "VALIDATION_ERROR", ex.Message));
        }
    }

    // ── Preview + test send ─────────────────────────────────

    /// <summary>Renders a template draft with realistic sample data.</summary>
    [HttpPost("preview")]
    [ProducesResponseType(typeof(ApiResponse<PreviewTemplateResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Preview(
        Guid businessId,
        [FromBody] BusinessNotificationPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        NoStore();
        await EnsureTenantAccessAsync(businessId, cancellationToken);

        if (!NotificationDefaults.AllChannels.Contains((request.Channel ?? string.Empty).Trim().ToLowerInvariant()))
            return BadRequest(ApiResponse<PreviewTemplateResponse>.Fail(
                "VALIDATION_ERROR", $"Channel '{request.Channel}' is not supported."));

        try
        {
            var result = await _service.PreviewAsync(businessId, request, cancellationToken);
            return Ok(ApiResponse<PreviewTemplateResponse>.Ok(result));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<PreviewTemplateResponse>.Fail("VALIDATION_ERROR", ex.Message));
        }
    }

    /// <summary>Sends a one-off test message through the requested channel.</summary>
    [HttpPost("test-send")]
    [ProducesResponseType(typeof(ApiResponse<BusinessNotificationTestSendResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> TestSend(
        Guid businessId,
        [FromBody] BusinessNotificationTestSendRequest request,
        CancellationToken cancellationToken = default)
    {
        NoStore();
        await EnsureTenantAccessAsync(businessId, cancellationToken);

        try
        {
            var result = await _service.TestSendAsync(businessId, request, GetUserId(), cancellationToken);
            return Ok(ApiResponse<BusinessNotificationTestSendResponse>.Ok(result));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<BusinessNotificationTestSendResponse>.Fail(
                "VALIDATION_ERROR", ex.Message));
        }
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private void NoStore() => Response.Headers.CacheControl = "no-store";

    private static bool LooksLikeEmail(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length is > 3 and <= 256
            && trimmed.Contains('@')
            && trimmed.IndexOf('@') > 0
            && trimmed.LastIndexOf('@') < trimmed.Length - 1;
    }

    /// <summary>
    /// Confirms the authenticated caller may manage notifications for
    /// <paramref name="businessId"/>: a platform administrator may manage any
    /// business; otherwise the caller must own or belong to the business.
    /// </summary>
    private async Task EnsureTenantAccessAsync(Guid businessId, CancellationToken ct)
    {
        if (User.IsInRole("Admin")) return;

        var userIdClaim = User.FindFirst("userId")?.Value;
        if (Guid.TryParse(userIdClaim, out var userId))
        {
            var owned = await _scopeResolver.GetOwnedBusinessIdAsync(userId);
            if (owned == businessId) return;

            var member = await _scopeResolver.GetMemberBusinessIdAsync(userId);
            if (member == businessId) return;
        }

        _logger.LogWarning(
            "Notification settings access denied for user {UserId} on business {BusinessId}",
            userIdClaim, businessId);

        throw new UnauthorizedAccessException(
            $"User is not authorised to manage notifications for business {businessId}.");
    }
}
