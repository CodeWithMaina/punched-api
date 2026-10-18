using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Services;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Tenant-scoped notification template override management.
/// Base routes:
///   /v1/admin/businesses/{businessId}/notification-templates
///   /v1/admin/businesses/{businessId}/notification-templates/overrides
///
/// A business owner (or platform Admin) customises subject/body templates for
/// registered notification types. Overrides win at delivery time; absence falls
/// back to the platform default. Unknown tokens are rejected at save time.
/// </summary>
[ApiController]
[Produces("application/json")]
[Authorize]
[EnableRateLimiting("template-overrides")]
[Route("v1/admin/businesses/{businessId:guid}/notification-templates")]
public sealed class TemplateOverridesController : ControllerBase
{
    private readonly TemplateOverrideService _service;
    private readonly IBusinessScopeResolver _scopeResolver;
    private readonly ILogger<TemplateOverridesController> _logger;

    public TemplateOverridesController(
        TemplateOverrideService service,
        IBusinessScopeResolver scopeResolver,
        ILogger<TemplateOverridesController> logger)
    {
        _service = service;
        _scopeResolver = scopeResolver;
        _logger = logger;
    }

    // ══════════════════════════════════════════════════════════════════
    //  List overrides for a business (paginated)
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Lists this business's template overrides, optionally filtered by type/channel.</summary>
    [HttpGet("overrides")]
    [ProducesResponseType(typeof(ApiResponse<PaginatedNotificationTemplateOverridesDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListOverrides(
        Guid businessId,
        [FromQuery] string? type = null,
        [FromQuery] string? channel = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        await EnsureTenantAccessAsync(businessId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(channel) && !NotificationDefaults.AllChannels.Contains(channel))
        {
            return BadRequest(ApiResponse<PaginatedNotificationTemplateOverridesDto>.Fail(
                "VALIDATION_ERROR", $"Channel '{channel}' is not supported."));
        }

        var result = await _service.ListOverridesAsync(businessId, type, channel, page, pageSize, cancellationToken);
        return Ok(ApiResponse<PaginatedNotificationTemplateOverridesDto>.Ok(result));
    }

    /// <summary>Resolves the effective template for one (type, channel): override if present, else platform default.</summary>
    [HttpGet("effective/{type}/{channel}")]
    [ProducesResponseType(typeof(ApiResponse<EffectiveTemplateDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetEffective(
        Guid businessId,
        string type,
        string channel,
        CancellationToken cancellationToken = default)
    {
        await EnsureTenantAccessAsync(businessId, cancellationToken);

        if (!NotificationDefaults.AllChannels.Contains(channel))
        {
            return BadRequest(ApiResponse<EffectiveTemplateDto>.Fail(
                "VALIDATION_ERROR", $"Channel '{channel}' is not supported."));
        }

        var result = await _service.GetEffectiveAsync(businessId, type, channel, cancellationToken);
        if (result is null)
            return NotFound(ApiResponse<EffectiveTemplateDto>.Fail("NOT_FOUND", $"No template registered for type '{type}'."));

        return Ok(ApiResponse<EffectiveTemplateDto>.Ok(result));
    }
    // ══════════════════════════════════════════════════════════════════
    //  Get / upsert / delete one override
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Retrieves one override by id (tenant-scoped).</summary>
    [HttpGet("overrides/{overrideId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<NotificationTemplateOverrideDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetOverride(
        Guid businessId,
        Guid overrideId,
        CancellationToken cancellationToken = default)
    {
        await EnsureTenantAccessAsync(businessId, cancellationToken);

        var all = await _service.ListOverridesAsync(businessId, null, null, 1, 100, cancellationToken);
        var dto = all.Items.FirstOrDefault(i => i.Id == overrideId);
        if (dto is null)
            return NotFound(ApiResponse<NotificationTemplateOverrideDto>.Fail("NOT_FOUND", "Override not found."));

        return Ok(ApiResponse<NotificationTemplateOverrideDto>.Ok(dto));
    }

    /// <summary>Creates an override (version 1). Rejects unknown tokens.</summary>
    [HttpPost("overrides")]
    [ProducesResponseType(typeof(ApiResponse<NotificationTemplateOverrideDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateOverride(
        Guid businessId,
        [FromBody] UpsertNotificationTemplateOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureTenantAccessAsync(businessId, cancellationToken);

        try
        {
            var dto = await _service.CreateOverrideAsync(businessId, request, cancellationToken);
            return StatusCode(StatusCodes.Status201Created,
                ApiResponse<NotificationTemplateOverrideDto>.Ok(dto));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<NotificationTemplateOverrideDto>.Fail("VALIDATION_ERROR", ex.Message));
        }
    }

    /// <summary>Updates an override. Increments version; snapshots prior version.</summary>
    [HttpPut("overrides/{overrideId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<NotificationTemplateOverrideDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateOverride(
        Guid businessId,
        Guid overrideId,
        [FromBody] UpsertNotificationTemplateOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureTenantAccessAsync(businessId, cancellationToken);

        try
        {
            var dto = await _service.UpdateOverrideAsync(businessId, overrideId, request, cancellationToken);
            return Ok(ApiResponse<NotificationTemplateOverrideDto>.Ok(dto));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(ApiResponse<NotificationTemplateOverrideDto>.Fail("NOT_FOUND", "Override not found."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<NotificationTemplateOverrideDto>.Fail("VALIDATION_ERROR", ex.Message));
        }
    }

    /// <summary>Deletes an override. Future deliveries fall back to platform default.</summary>
    [HttpDelete("overrides/{overrideId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<MessageResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteOverride(
        Guid businessId,
        Guid overrideId,
        CancellationToken cancellationToken = default)
    {
        await EnsureTenantAccessAsync(businessId, cancellationToken);

        var deleted = await _service.DeleteOverrideAsync(businessId, overrideId, cancellationToken);
        if (!deleted)
            return NotFound(ApiResponse<MessageResponse>.Fail("NOT_FOUND", "Override not found."));

        return Ok(ApiResponse<MessageResponse>.Ok(new MessageResponse { Message = "Override deleted." }));
    }

    // ══════════════════════════════════════════════════════════════════
    //  Preview (render with sample payload)
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Renders a template (override or draft) with a sample payload and reports unknown tokens.</summary>
    [HttpPost("preview")]
    [ProducesResponseType(typeof(ApiResponse<PreviewTemplateResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Preview(
        Guid businessId,
        [FromBody] PreviewTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureTenantAccessAsync(businessId, cancellationToken);

        var result = _service.PreviewTemplate(request);
        return Ok(ApiResponse<PreviewTemplateResponse>.Ok(result));
    }

    // ══════════════════════════════════════════════════════════════════
    //  Version history / restore
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Lists version history for one override (newest first).</summary>
    [HttpGet("overrides/{overrideId:guid}/versions")]
    [ProducesResponseType(typeof(ApiResponse<List<TemplateOverrideVersionDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListVersions(
        Guid businessId,
        Guid overrideId,
        CancellationToken cancellationToken = default)
    {
        await EnsureTenantAccessAsync(businessId, cancellationToken);

        var versions = await _service.ListVersionsAsync(businessId, overrideId, cancellationToken);
        return Ok(ApiResponse<List<TemplateOverrideVersionDto>>.Ok(versions));
    }

    /// <summary>Restores a historical version as a new (current) version.</summary>
    [HttpPost("overrides/{overrideId:guid}/versions/{version:int}/restore")]
    [ProducesResponseType(typeof(ApiResponse<NotificationTemplateOverrideDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RestoreVersion(
        Guid businessId,
        Guid overrideId,
        int version,
        [FromQuery] string updatedBy,
        CancellationToken cancellationToken = default)
    {
        await EnsureTenantAccessAsync(businessId, cancellationToken);

        if (string.IsNullOrWhiteSpace(updatedBy))
        {
            return BadRequest(ApiResponse<NotificationTemplateOverrideDto>.Fail(
                "VALIDATION_ERROR", "updatedBy is required."));
        }

        try
        {
            var dto = await _service.RestoreVersionAsync(businessId, overrideId, version, updatedBy, cancellationToken);
            return Ok(ApiResponse<NotificationTemplateOverrideDto>.Ok(dto));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(ApiResponse<NotificationTemplateOverrideDto>.Fail("NOT_FOUND", "Override or version not found."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<NotificationTemplateOverrideDto>.Fail("VALIDATION_ERROR", ex.Message));
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  Tenant access guard
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Confirms the authenticated caller may manage templates for <paramref name="businessId"/>:
    /// platform Admin may manage any business; otherwise the caller must own the business.
    /// </summary>
    private async Task EnsureTenantAccessAsync(Guid businessId, CancellationToken ct)
    {
        var isAdmin = User.IsInRole("Admin");
        if (isAdmin) return;

        var userIdClaim = User.FindFirst("userId")?.Value;
        if (Guid.TryParse(userIdClaim, out var userId))
        {
            var owned = await _scopeResolver.GetOwnedBusinessIdAsync(userId);
            if (owned == businessId) return;
        }

        _logger.LogWarning("Template override access denied for user {UserId} on business {BusinessId}", userIdClaim, businessId);
        throw new UnauthorizedAccessException($"User is not authorised to manage templates for business {businessId}.");
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}

