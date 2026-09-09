using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PunchedApi.Application.DTOs;
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
    private readonly IAdminService _adminService;
    private readonly ILogger<UserController> _logger;

    public UserController(
        IAuthService authService,
        IUserService userService,
        INotificationsService notificationsService,
        IAdminService adminService,
        ILogger<UserController> logger)
    {
        _authService = authService;
        _userService = userService;
        _notificationsService = notificationsService;
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
