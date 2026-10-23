using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using PunchedApi.Application.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Modules;
using PunchedApi.API.Filters;
using PunchedApi.Domain.Interfaces;
using Microsoft.AspNetCore.RateLimiting;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Business management controller.
/// Base route: /v1/businesses
/// </summary>
[ApiController]
[Route("v1/businesses")]
[Produces("application/json")]
[EnableRateLimiting("general")]
public partial class BusinessController : ControllerBase
{
        private readonly IBusinessService _businessService;
    private readonly IStampService _stampService;
    private readonly INotificationsService _notificationsService;
    private readonly IAppointmentService _appointmentService;
    private readonly IAttendanceLocationService _attendanceLocationService;
    private readonly ILogger<BusinessController> _logger;

    public BusinessController(
        IBusinessService businessService,
        IStampService stampService,
        INotificationsService notificationsService,
        IAppointmentService appointmentService,
        IAttendanceLocationService attendanceLocationService,
        ILogger<BusinessController> logger)
    {
        _businessService = businessService;
        _stampService = stampService;
        _notificationsService = notificationsService;
        _appointmentService = appointmentService;
        _attendanceLocationService = attendanceLocationService;
        _logger = logger;
    }

    /// <summary>
    /// List all businesses (directory). Paginated directory projection —
    /// same sanitized shape as the public profile, so directory reads never
    /// expose private business information.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<List<BusinessResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListBusinesses(
        [FromQuery] string? category,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);
        page = Math.Max(page, 1);
        var result = await _businessService.ListBusinessesAsync(category, search, page, pageSize);
        return Ok(result);
    }

    /// <summary>
    /// Legacy business-by-ID read. Authenticated-only: returns the full
    /// <see cref="BusinessResponse"/> (contact + operational fields) for
    /// signed-in callers. Anonymous storefront callers must use
    /// GET public/{businessId}, which returns the sanitized allow-listed
    /// <see cref="PublicBusinessProfileResponse"/> instead.
    /// </summary>
    [HttpGet("{businessId:guid}")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<BusinessResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBusiness(Guid businessId)
    {
        var result = await _businessService.GetBusinessByIdAsync(businessId);
        if (!result.Success) return NotFound(result);
        return Ok(result);
    }

    /// <summary>
    /// Get a business's public, customer-safe profile — used by the business
    /// share link and QR discovery flow. Excludes internal data (M-Pesa number,
    /// owner, subscription) and only advertises enabled features.
    /// </summary>
    [HttpGet("public/{businessId:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<PublicBusinessProfileResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPublicProfile(Guid businessId)
    {
        var result = await _businessService.GetPublicProfileAsync(businessId);
        if (!result.Success) return NotFound(result);
        return Ok(result);
    }

    /// <summary>
    /// Canonical owner provisioning: create a business for the authenticated
    /// identity. Any signed-in identity that does not already hold a tenant
    /// persona may call this (Customer → Business promotion happens atomically
    /// with the insert); Staff and Admin identities are refused, as is a second
    /// business for an existing owner.
    ///
    /// The access token still carries the previous persona, so the client must
    /// refresh its session (POST /v1/auth/refresh-token) after a 201 to receive a
    /// Business-role token.
    /// </summary>
    /// <response code="201">Business created; caller promoted to owner.</response>
    /// <response code="400">Validation failure or ineligible persona.</response>
    /// <response code="409">The caller already owns a business.</response>
    [HttpPost]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<BusinessResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateBusiness([FromBody] CreateBusinessRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _businessService.CreateBusinessAsync(userId.Value, request);
        if (!result.Success)
            return result.Error?.Code == "BUSINESS_EXISTS" ? Conflict(result) : BadRequest(result);

        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>
    /// Get the authenticated business owner's own business.
    /// </summary>
    [HttpGet("me")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<BusinessResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMyBusiness()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _businessService.GetMyBusinessAsync(userId.Value);
        if (!result.Success) return NotFound(result);
        return Ok(result);
    }

    /// <summary>
    /// Update the authenticated business owner's business details.
    /// </summary>
    [HttpPatch("me")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<BusinessResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateMyBusiness([FromBody] UpdateBusinessRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _businessService.UpdateMyBusinessAsync(userId.Value, request);
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// Update the business's reminder policy (enabled, reminder offsets,
    /// delivery channels). Creates a policy row if none exists.
    /// </summary>
    [HttpPut("me/reminder-policy")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<BusinessResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateMyReminderPolicy([FromBody] UpdateBusinessReminderPolicyRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _businessService.UpdateReminderPolicyAsync(userId.Value, request);
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// Change the authenticated owner's business subdomain slug (settings →
    /// Business URL). Validates format/reserved/availability server-side;
    /// previous slugs are archived and keep redirecting to the new address.
    /// </summary>
    [HttpPut("me/slug")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<BusinessResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateMyBusinessSlug([FromBody] UpdateBusinessSlugRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _businessService.UpdateMyBusinessSlugAsync(userId.Value, request);
        if (!result.Success) return MapFailure(result);
        return Ok(result);
    }

    /// <summary>
    /// Resolve a subdomain address to its business — the host → tenant
    /// mapping used by the frontend. Matches current slugs first, then
    /// superseded slugs (moved=true + canonical slug → redirect). Public:
    /// only exposes the business id, canonical slug and name, which the
    /// public business directory already lists.
    /// </summary>
    [HttpGet("by-slug/{slug}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<TenantResolutionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResolveBySlug(string slug)
    {
        var result = await _businessService.ResolveBySlugAsync(slug);
        if (!result.Success) return NotFound(result);
        return Ok(result);
    }

    /// <summary>
    /// Public storefront bootstrap — the single call that renders the initial
    /// storefront (slug → identity + public profile + landing page).
    /// Unknown slugs are 404 NOT_FOUND; superseded slugs return moved=true +
    /// the canonical slug (still 200 + full payload so the client redirects
    /// without a second fetch). Malformed/reserved slugs are 400.
    /// Output-cached (30 s, per slug) — public + immutable per config version.
    /// </summary>
    [HttpGet("by-slug/{slug}/bootstrap")]
    [AllowAnonymous]
    [OutputCache(PolicyName = "storefront-bootstrap")]
    [ProducesResponseType(typeof(ApiResponse<StorefrontBootstrapResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStorefrontBootstrap(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return BadRequest(ApiResponse<StorefrontBootstrapResponse>.Fail("SLUG_INVALID", "A business address is required."));
        var result = await _businessService.GetStorefrontBootstrapBySlugAsync(slug);
        if (!result.Success)
            return result.Error?.Code == "NOT_FOUND" ? NotFound(result) : BadRequest(result);
        return Ok(result);
    }





    /// <summary>
    /// Business activity feed: recent stamps (newest-first), optionally filtered to a staff member.
    /// Business-scoped — caller must be the owner or a linked staff member.
    /// </summary>
    [RequireModule("stamps")]
    [HttpGet("{businessId:guid}/activity/recent")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [ProducesResponseType(typeof(ApiResponse<List<StampDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetRecentActivity(
        Guid businessId,
        [FromQuery] Guid? staffUserId,
        [FromQuery] int limit = 20)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (!await _businessService.CanAccessBusinessAsync(userId.Value, businessId))
            return StatusCode(StatusCodes.Status403Forbidden,
                ApiResponse<List<StampDto>>.Fail("FORBIDDEN", "You are not authorized to view this business's activity."));

        limit = Math.Clamp(limit, 1, 50);
        var result = await _stampService.GetRecentStampsAsync(businessId, staffUserId, limit);
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// Get the authenticated staff member's in-app notifications.
    /// </summary>
    [RequireModule("notifications")]
    [HttpGet("me/notifications")]
    [Authorize(Policy = MembershipPolicies.Staff)]
    [ProducesResponseType(typeof(ApiResponse<List<NotificationDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyNotifications([FromQuery] bool unreadOnly = false)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var notifications = await _notificationsService.GetAsync(userId.Value, unreadOnly, 50);
        return Ok(ApiResponse<List<NotificationDto>>.Ok(notifications));
    }

    /// <summary>
    /// Mark the authenticated staff member's notification(s) as read.
    /// Optionally pass a notificationId; omit to mark all as read.
    /// </summary>
    [RequireModule("notifications")]
    [HttpPost("me/notifications/read")]
    [Authorize(Policy = MembershipPolicies.Staff)]
    [ProducesResponseType(typeof(ApiResponse<MessageResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkNotificationsRead([FromBody] MarkNotificationReadRequest? request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (request?.NotificationId is Guid notificationId)
            await _notificationsService.MarkReadByIdAsync(userId.Value, notificationId);
        else
            await _notificationsService.MarkAllReadAsync(userId.Value);
        return Ok(ApiResponse<MessageResponse>.Ok(new MessageResponse { Message = "Notifications marked as read." }));
    }


    /// <summary>
    /// Maps an ApiResponse failure to the HTTP status dictated by backend.md §9.
    /// Success always returns 200 (Ok) via the callers.
    /// </summary>
    private IActionResult MapFailure<T>(ApiResponse<T> result)
        => result.Error?.Code switch
        {
            "NOT_FOUND" or "SERVICE_NOT_FOUND" or "STAFF_NOT_FOUND" or "CUSTOMER_NOT_FOUND" => NotFound(result),
            "FORBIDDEN" or "MODULE_DISABLED" => StatusCode(StatusCodes.Status403Forbidden, result),
            "OVERBOOKING" or "SLOT_UNAVAILABLE" or "INVALID_STATUS_TRANSITION" or
            // Slug change lost the unique-index race (another owner claimed it).
            "SLUG_TAKEN" => Conflict(result),
            // Attendance owner surface (§9.6): state/availability conflicts map to 409 so the
            // declarative client (one primary action, §5.4) can explain them, not retry them.
            "ALREADY_CLOCKED_IN" or "NOT_CLOCKED_IN" or "ATTENDANCE_DISABLED" or
            "ATTENDANCE_NOT_CONFIGURED" or "VERIFICATION_METHOD_UNAVAILABLE" or
            "LOCATION_HAS_HISTORY" or "LOCATION_NAME_EXISTS" => Conflict(result),
            _ => BadRequest(result)   // STAFF_NOT_AVAILABLE, VALIDATION_ERROR, BUSINESS_NOT_FOUND, fallback
        };


    /// <summary>
    /// Database-first: every business the authenticated customer is associated with.
    /// Single query UNIONs loyalty_cards + appointments + referral_links for the
    /// caller and joins the business. Replaces client-side multi-fetch merging.
    /// </summary>
    [HttpGet("me/associated")]
    [Authorize(Roles = "Customer")]
    [ProducesResponseType(typeof(ApiResponse<List<CustomerAssociatedBusinessResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyAssociatedBusinesses()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _businessService.GetAssociatedBusinessesAsync(userId.Value);
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}
