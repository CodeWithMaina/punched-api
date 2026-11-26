using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Settings;
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
    /// One filtered, sorted and paged page of the authenticated user's inbox.
    ///
    /// <para>This is the endpoint the notification module reads. It returns the rows, the unread
    /// count for the same scope (so a badge never has to guess), and the filter values that
    /// actually occur in the caller's own notifications — so the UI offers real choices.
    /// A supplied <c>businessId</c> is verified against the caller's memberships server-side.</para>
    /// </summary>
    [HttpGet("notifications/inbox")]
    [ProducesResponseType(typeof(ApiResponse<NotificationInboxPageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetNotificationInbox(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? status = null,
        [FromQuery] string? type = null,
        [FromQuery] string? category = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] string? sort = null,
        [FromQuery] string? direction = null,
        [FromQuery] Guid? businessId = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        NoStore();
        var result = await _notificationsService.GetInboxAsync(
            userId.Value,
            new NotificationInboxQuery
            {
                Page = page,
                PageSize = pageSize,
                Status = status,
                Type = type,
                Category = category,
                FromUtc = fromUtc,
                ToUtc = toUtc,
                Sort = sort,
                Direction = direction,
                BusinessId = businessId,
            },
            cancellationToken);

        if (!result.Success)
            return StatusCode(StatusCodes.Status403Forbidden, result);

        return Ok(result);
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
    /// The public half of the deployment's VAPID key pair, so the web app can create a
    /// push subscription. The private key is never exposed by any endpoint — it stays in
    /// the server's configuration and is used only to sign deliveries.
    /// </summary>
    [HttpGet("push/config")]
    [ProducesResponseType(typeof(ApiResponse<PushConfigResponse>), StatusCodes.Status200OK)]
    public IActionResult GetPushConfig([FromServices] IOptions<VapidSettings> vapid)
    {
        var settings = vapid.Value;
        var configured = settings.Enabled
                         && !string.IsNullOrWhiteSpace(settings.PublicKey)
                         && !string.IsNullOrWhiteSpace(settings.PrivateKey);

        // `publicKey` is returned whenever the deployment has one (it is not a secret) so
        // the UI can explain WHY push is unavailable. `configured` is the honest answer
        // to "can this server actually deliver a notification?", which needs both halves.
        return Ok(ApiResponse<PushConfigResponse>.Ok(new PushConfigResponse
        {
            Enabled = configured,
            PublicKey = string.IsNullOrWhiteSpace(settings.PublicKey) ? null : settings.PublicKey
        }));
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

        var endpoint = request.Endpoint.Trim();

        // A browser endpoint identifies a device, not a person. If the same endpoint was
        // registered by another account (a shared or handed-over browser), retire those
        // rows first: otherwise notifications for the previous user would keep arriving
        // on a device they no longer use. This also guarantees one active row per
        // (user, endpoint) pair below.
        var otherDevices = await _context.PushDevices
            .Where(device => device.Endpoint == endpoint && device.IsActive && device.UserId != userId.Value)
            .ToListAsync(HttpContext.RequestAborted);
        foreach (var other in otherDevices)
        {
            other.IsActive = false;
            other.LastSeenAt = DateTime.UtcNow;
        }

        var existing = await _context.PushDevices
            .SingleOrDefaultAsync(device => device.UserId == userId.Value && device.Endpoint == endpoint, HttpContext.RequestAborted);

        if (existing is null)
        {
            _context.PushDevices.Add(new PunchedApi.Domain.Entities.PushDevice
            {
                Id = Guid.NewGuid(),
                UserId = userId.Value,
                Endpoint = endpoint,
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

    /// <summary>
    /// The caller's own active push devices. Endpoints are reduced to their host so the
    /// response can be shown on screen without leaking a full capability URL, which is
    /// itself a credential for that subscription.
    /// </summary>
    [HttpGet("push/devices")]
    [ProducesResponseType(typeof(ApiResponse<List<PushDeviceSummary>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPushDevices()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var devices = await _context.PushDevices.AsNoTracking()
            .Where(device => device.UserId == userId.Value && device.IsActive)
            .OrderByDescending(device => device.LastSeenAt)
            .Select(device => new { device.Id, device.Endpoint, device.UserAgent, device.LastSeenAt })
            .ToListAsync(HttpContext.RequestAborted);

        var summaries = devices.Select(device => new PushDeviceSummary
        {
            Id = device.Id,
            Host = EndpointHost(device.Endpoint),
            UserAgent = device.UserAgent,
            LastSeenAt = device.LastSeenAt,
        }).ToList();

        return Ok(ApiResponse<List<PushDeviceSummary>>.Ok(summaries));
    }

    /// <summary>`https://fcm.googleapis.com/fcm/send/abc…` → `fcm.googleapis.com`.</summary>
    private static string EndpointHost(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "unknown";

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

/// <summary>
/// What the web app needs to know before it can subscribe a device: the VAPID public
/// key (safe to expose) and whether this deployment can actually deliver a push
/// (which also needs the private half, so it is answered server-side).
/// </summary>
public sealed class PushConfigResponse
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("publicKey")]
    public string? PublicKey { get; set; }
}

/// <summary>
/// One active push device for the caller. The full endpoint is a capability URL, so it
/// never leaves the server — the host is enough for a person to recognise the device.
/// </summary>
public sealed class PushDeviceSummary
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; set; }

    [JsonPropertyName("lastSeenAt")]
    public DateTime LastSeenAt { get; set; }
}

public sealed class PushRegistrationKeys
{
    [JsonPropertyName("p256dh")]
    public string P256dh { get; set; } = string.Empty;

    [JsonPropertyName("auth")]
    public string Auth { get; set; } = string.Empty;
}
