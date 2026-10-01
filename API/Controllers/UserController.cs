using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.API.Controllers;

/// <summary>
/// User profile controller.
/// Base route: /v1/users
/// </summary>
[ApiController]
[Route("v1/users")]
[Produces("application/json")]
[Authorize]
[EnableRateLimiting("general")]
public class UserController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IUserService _userService;
    private readonly INotificationsService _notificationsService;
    private readonly IPreferenceResolver _preferenceResolver;
    private readonly IAdminService _adminService;
    private readonly ApplicationDbContext _context;
    private readonly ILogger<UserController> _logger;

    public UserController(
        IAuthService authService,
        IUserService userService,
        INotificationsService notificationsService,
        IPreferenceResolver preferenceResolver,
        IAdminService adminService,
        ApplicationDbContext context,
        ILogger<UserController> logger)
    {
        _authService = authService;
        _userService = userService;
        _notificationsService = notificationsService;
        _preferenceResolver = preferenceResolver;
        _adminService = adminService;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get current authenticated user's profile.
    /// </summary>
    [HttpGet("profile")]
    [ProducesResponseType(typeof(ApiResponse<UserProfileResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetProfile()
    {
        var userAuthId = GetUserAuthId();
        if (userAuthId == null)
            return Unauthorized(ApiResponse<UserProfileResponse>.Fail("UNAUTHORIZED", "Invalid token."));

        var result = await _authService.GetProfileAsync(userAuthId.Value);
        if (!result.Success) return NotFound(result);
        return Ok(result);
    }

    /// <summary>
    /// Update current authenticated user's profile.
    /// </summary>
    [HttpPatch("profile")]
    [ProducesResponseType(typeof(ApiResponse<UserProfileResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var userAuthId = GetUserAuthId();
        if (userAuthId == null)
            return Unauthorized(ApiResponse<UserProfileResponse>.Fail("UNAUTHORIZED", "Invalid token."));

        var result = await _userService.UpdateProfileAsync(userAuthId.Value, request);
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// List the authenticated user's in-app notifications (all roles).
    /// </summary>
    [HttpGet("notifications")]
    [ProducesResponseType(typeof(ApiResponse<List<NotificationDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNotifications([FromQuery] bool unreadOnly = false)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        NoStore();
        var notifications = await _notificationsService.GetAsync(userId.Value, unreadOnly);
        return Ok(ApiResponse<List<NotificationDto>>.Ok(notifications));
    }

    /// <summary>
    /// Mark one notification (or all) as read for the authenticated user.
    /// </summary>
    [HttpPost("notifications/read")]
    [ProducesResponseType(typeof(ApiResponse<MessageResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkNotificationsRead([FromBody] MarkNotificationReadRequest? request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        NoStore();
        if (request?.NotificationId is Guid notificationId)
            await _notificationsService.MarkReadByIdAsync(userId.Value, notificationId);
        else
            await _notificationsService.MarkAllReadAsync(userId.Value);
        return Ok(ApiResponse<MessageResponse>.Ok(new MessageResponse { Message = "Notifications updated." }));
    }

    /// <summary>
    /// Unread count for the authenticated user's inbox — the polling badge no
    /// longer needs to fetch up to 50 rows to compute itself.
    /// </summary>
    [HttpGet("notifications/unread-count")]
    [ProducesResponseType(typeof(ApiResponse<UnreadCountResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUnreadCount()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        NoStore();
        var unreadCount = await _notificationsService.GetUnreadCountAsync(userId.Value);
        return Ok(ApiResponse<UnreadCountResponse>.Ok(new UnreadCountResponse { UnreadCount = unreadCount }));
    }

    /// <summary>
    /// Mark one notification read. Another user's row is indistinguishable from a
    /// missing one (404), so the route cannot be used to probe for ids.
    /// </summary>
    [HttpPost("notifications/{id:guid}/read")]
    [ProducesResponseType(typeof(ApiResponse<MessageResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkNotificationRead(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        NoStore();
        var owned = await _notificationsService.MarkReadByIdAsync(userId.Value, id);
        if (!owned)
            return NotFound(ApiResponse<MessageResponse>.Fail("NOT_FOUND", "Notification not found."));

        return Ok(ApiResponse<MessageResponse>.Ok(new MessageResponse { Message = "Notification marked read." }));
    }

    /// <summary>Mark every unread notification of the authenticated user read.</summary>
    [HttpPost("notifications/read-all")]
    [ProducesResponseType(typeof(ApiResponse<MessageResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkAllNotificationsRead()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        NoStore();
        var marked = await _notificationsService.MarkAllReadAsync(userId.Value);
        return Ok(ApiResponse<MessageResponse>.Ok(new MessageResponse
        {
            Message = marked == 1 ? "1 notification marked read." : $"{marked} notifications marked read."
        }));
    }

    /// <summary>
    /// The caller's RESOLVED notification preferences (defaults + business + user
    /// overrides with the deciding level) so the client renders checkboxes without
    /// re-implementing the resolution rules.
    /// </summary>
    [HttpGet("me/notification-preferences")]
    [ProducesResponseType(typeof(ApiResponse<List<ResolvedPreferenceDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNotificationPreferences([FromQuery] Guid? businessId = null)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        NoStore();
        var resolved = await _preferenceResolver.GetResolvedAsync(userId.Value, businessId);
        return Ok(ApiResponse<List<ResolvedPreferenceDto>>.Ok(resolved.ToList()));
    }

    /// <summary>
    /// Upsert the caller's own preference rows. Scope is always the caller's
    /// <c>User.Id</c> (from the token, never the body); rows equal to the system
    /// default are deleted so the table stays sparse. There is deliberately no
    /// force/override flag in the model — <c>Force</c> is server-side only.
    /// </summary>
    [HttpPut("me/notification-preferences")]
    [ProducesResponseType(typeof(ApiResponse<List<ResolvedPreferenceDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateNotificationPreferences([FromBody] UpdateNotificationPreferencesRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        NoStore();

        if (request.Preferences.Count == 0)
            return BadRequest(ApiResponse<List<ResolvedPreferenceDto>>.Fail("VALIDATION_ERROR", "At least one preference is required."));

        try
        {
            await _preferenceResolver.UpsertAsync(userId.Value, request.BusinessId, request.Preferences);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<List<ResolvedPreferenceDto>>.Fail("VALIDATION_ERROR", ex.Message));
        }

        var resolved = await _preferenceResolver.GetResolvedAsync(userId.Value, request.BusinessId);
        return Ok(ApiResponse<List<ResolvedPreferenceDto>>.Ok(resolved.ToList()));
    }

    /// <summary>
    /// Register (or refresh) a browser endpoint that can receive VAPID web pushes.
    /// Scope is always the caller's own user id; no client-supplied user id is accepted.
    /// </summary>
    [HttpPost("push/token")]
    [ProducesResponseType(typeof(ApiResponse<MessageResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpsertPushToken([FromBody] PushRegistrationRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (request is null || string.IsNullOrWhiteSpace(request.Endpoint) ||
            string.IsNullOrWhiteSpace(request.Keys?.P256dh) || string.IsNullOrWhiteSpace(request.Keys?.Auth))
        {
            return BadRequest(ApiResponse<MessageResponse>.Fail("VALIDATION_ERROR", "Push subscription details are required."));
        }

        var existing = await _context.PushDevices
            .SingleOrDefaultAsync(device => device.UserId == userId.Value && device.Endpoint == request.Endpoint, HttpContext.RequestAborted);

        if (existing is null)
        {
            _context.PushDevices.Add(new PunchedApi.Domain.Entities.PushDevice
            {
                Id = Guid.NewGuid(),
                UserId = userId.Value,
                Endpoint = request.Endpoint.Trim(),
                P256dh = request.Keys.P256dh.Trim(),
                Auth = request.Keys.Auth.Trim(),
                UserAgent = Request.Headers["User-Agent"].ToString(),
                IsActive = true,
                LastSeenAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.P256dh = request.Keys.P256dh.Trim();
            existing.Auth = request.Keys.Auth.Trim();
            existing.UserAgent = Request.Headers["User-Agent"].ToString();
            existing.IsActive = true;
            existing.LastSeenAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(ApiResponse<MessageResponse>.Ok(new MessageResponse { Message = "Push token registered." }));
    }

    /// <summary>
    /// Deactivate a browser push endpoint for the caller. If no endpoint is supplied,
    /// all active devices for the user are disabled.
    /// </summary>
    [HttpDelete("push/token")]
    [ProducesResponseType(typeof(ApiResponse<MessageResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeletePushToken([FromQuery] string? endpoint = null)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var query = _context.PushDevices.Where(device => device.UserId == userId.Value && device.IsActive);
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            query = query.Where(device => device.Endpoint == endpoint);
        }

        var devices = await query.ToListAsync(HttpContext.RequestAborted);
        foreach (var device in devices)
        {
            device.IsActive = false;
            device.LastSeenAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(ApiResponse<MessageResponse>.Ok(new MessageResponse { Message = "Push token removed." }));
    }

    /// <summary>Private API responses must never be stored by a shared device or proxy.</summary>
    private void NoStore() => Response.Headers["Cache-Control"] = "no-store";

    /// <summary>
    /// Self-service account deletion (customers only). Soft-deletes the user,
    /// revokes sessions/tokens. Confirmation is enforced client-side.
    /// </summary>
    [HttpDelete("profile")]
    [Authorize(Roles = "Customer")]
    [ProducesResponseType(typeof(ApiResponse<MessageResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteAccount()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _adminService.DeleteUserAsync(userId.Value);
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    private Guid? GetUserId()
    {
        var value = User.FindFirst("userId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private Guid? GetUserAuthId()
    {
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}

public sealed class PushRegistrationRequest
{
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = string.Empty;

    [JsonPropertyName("keys")]
    public PushRegistrationKeys? Keys { get; set; }
}

public sealed class PushRegistrationKeys
{
    [JsonPropertyName("p256dh")]
    public string P256dh { get; set; } = string.Empty;

    [JsonPropertyName("auth")]
    public string Auth { get; set; } = string.Empty;
}
