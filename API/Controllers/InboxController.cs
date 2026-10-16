using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// The authenticated user's inbox archive (Phase 1.8): archived rows leave the
/// default inbox list (<c>GET /v1/users/notifications</c>) without a status enum
/// and remain available here. A user's own filter is the authorization check on
/// every id-scoped route — another user's row is indistinguishable from a
/// missing one.
/// </summary>
[ApiController]
[Route("v1/inbox")]
[Authorize]
[EnableRateLimiting("general")]
[Produces("application/json")]
public sealed class InboxController : ControllerBase
{
    private readonly INotificationsService _notifications;

    public InboxController(INotificationsService notifications) => _notifications = notifications;

    /// <summary>Paged archived notifications for the authenticated user.</summary>
    [HttpGet("archive")]
    [ProducesResponseType(typeof(ApiResponse<PaginatedResponse<NotificationDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetArchive(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        NoStore();
        var result = await _notifications.GetArchivedAsync(
            userId.Value, Math.Max(page, 1), Math.Clamp(pageSize, 1, 100), cancellationToken);
        return Ok(ApiResponse<PaginatedResponse<NotificationDto>>.Ok(result));
    }

    /// <summary>Archive one notification; it leaves the default inbox list.</summary>
    [HttpPost("{id:guid}/archive")]
    [ProducesResponseType(typeof(ApiResponse<MessageResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Archive(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        NoStore();
        var owned = await _notifications.ArchiveByIdAsync(userId.Value, id);
        if (!owned)
            return NotFound(ApiResponse<MessageResponse>.Fail("NOT_FOUND", "Notification not found."));

        return Ok(ApiResponse<MessageResponse>.Ok(new MessageResponse { Message = "Notification archived." }));
    }

    /// <summary>Restore an archived notification to the default inbox list.</summary>
    [HttpPost("{id:guid}/unarchive")]
    [ProducesResponseType(typeof(ApiResponse<MessageResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unarchive(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        NoStore();
        var owned = await _notifications.UnarchiveByIdAsync(userId.Value, id);
        if (!owned)
            return NotFound(ApiResponse<MessageResponse>.Fail("NOT_FOUND", "Notification not found."));

        return Ok(ApiResponse<MessageResponse>.Ok(new MessageResponse { Message = "Notification restored." }));
    }

    private Guid? GetUserId()
    {
        var value = User.FindFirst("userId")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private void NoStore() => Response.Headers.CacheControl = "no-store";
}