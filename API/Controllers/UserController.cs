using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Interfaces;
using Microsoft.AspNetCore.RateLimiting;

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
    private readonly ILogger<UserController> _logger;

    public UserController(
        IAuthService authService,
        IUserService userService,
        INotificationsService notificationsService,
        IPreferenceResolver preferenceResolver,
        IAdminService adminService,
        ILogger<UserController> logger)
    {
        _authService = authService;
        _userService = userService;
        _notificationsService = notificationsService;
        _preferenceResolver = preferenceResolver;
        _adminService = adminService;
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

        await _notificationsService.MarkReadAsync(userId.Value, request?.NotificationId);
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
