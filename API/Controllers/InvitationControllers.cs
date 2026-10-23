using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using PunchedApi.Application.Authorization;
using Microsoft.AspNetCore.Mvc;
using PunchedApi.API.Filters;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Interfaces;
using Microsoft.AspNetCore.RateLimiting;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Staff invitation management for a business owner.
/// Base route: /v1/businesses/me/staff/invitations
/// Restricted to Business-role users. The tenant (business) is always derived from the
/// authenticated owner's id — never from a client-supplied value — enforcing tenant isolation.
/// </summary>
[ApiController]
[Route("v1/businesses/me/staff/invitations")]
[Produces("application/json")]
[Authorize(Policy = MembershipPolicies.Owner)]
[RequireModule("staff")]
[EnableRateLimiting("general")]
public class StaffInvitationsController : ControllerBase
{
    private readonly IInvitationService _invitationService;
    private readonly ILogger<StaffInvitationsController> _logger;

    public StaffInvitationsController(IInvitationService invitationService, ILogger<StaffInvitationsController> logger)
    {
        _invitationService = invitationService;
        _logger = logger;
    }

    /// <summary>
    /// Create and email a staff invitation.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<StaffInvitationResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateInvitation([FromBody] CreateStaffInvitationRequest request)
    {
        var ownerId = GetOwnerId();
        if (ownerId == null) return Unauthorized();

        var result = await _invitationService.CreateStaffInvitationAsync(ownerId.Value, request);
        if (!result.Success)
        {
            return result.Error?.Code switch
            {
                "DUPLICATE_INVITATION" => Conflict(result),
                _ => BadRequest(result)
            };
        }

        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>
    /// List all staff invitations for the caller's business.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<List<StaffInvitationResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListInvitations()
    {
        var ownerId = GetOwnerId();
        if (ownerId == null) return Unauthorized();

        var result = await _invitationService.ListStaffInvitationsAsync(ownerId.Value);
        return Ok(result);
    }

    /// <summary>
    /// Re-send a pending invitation (rotates its token and refreshes expiry).
    /// </summary>
    [HttpPost("{invitationId:guid}/resend")]
    [ProducesResponseType(typeof(ApiResponse<StaffInvitationResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResendInvitation(Guid invitationId)
    {
        var ownerId = GetOwnerId();
        if (ownerId == null) return Unauthorized();

        var result = await _invitationService.ResendStaffInvitationAsync(ownerId.Value, invitationId);
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// Revoke a pending invitation.
    /// </summary>
    [HttpPost("{invitationId:guid}/revoke")]
    [ProducesResponseType(typeof(ApiResponse<MessageResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RevokeInvitation(Guid invitationId)
    {
        var ownerId = GetOwnerId();
        if (ownerId == null) return Unauthorized();

        var result = await _invitationService.RevokeStaffInvitationAsync(ownerId.Value, invitationId);
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    private Guid? GetOwnerId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}

/// <summary>
/// Public invitation acceptance endpoints (no auth required — validation is token-based).
/// Used by the staff invitation acceptance page.
///
/// EXPLICIT [AllowAnonymous]: these are reached from an email link by someone
/// who is not signed in yet. Under the deny-by-default fallback policy the
/// attribute is mandatory — without it every invitation link would 401.
/// </summary>
[ApiController]
[Route("v1/invitations")]
[Produces("application/json")]
[AllowAnonymous]
public class InvitationsController : ControllerBase
{
    private readonly IInvitationService _invitationService;
    private readonly ISessionCookieService _sessionCookie;

    public InvitationsController(IInvitationService invitationService, ISessionCookieService sessionCookie)
    {
        _invitationService = invitationService;
        _sessionCookie = sessionCookie;
    }

    /// <summary>
    /// Validate an invitation token and return safe info for the accept page.
    /// </summary>
    [HttpGet("{token}")]
    [ProducesResponseType(typeof(ApiResponse<StaffInvitationValidationResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Validate(string token)
    {
        // Always 200 with valid=false + error code for unusable invitations (avoids token-enumeration).
        var result = await _invitationService.ValidateStaffInvitationAsync(token);
        return Ok(result);
    }

    /// <summary>
    /// Accept an invitation: creates the staff account, links them to the business, and authenticates.
    /// </summary>
    /// <remarks>
    /// This is the "new account" path only. An invitee who already has an identity is
    /// answered with <c>EMAIL_IN_USE</c> and must sign in, then accept through
    /// POST /v1/invitations/{token}/accept-existing (the session proves ownership of
    /// the invited mailbox).
    /// </remarks>
    [HttpPost("{token}/accept")]
    [ProducesResponseType(typeof(ApiResponse<AuthResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Accept(string token, [FromBody] AcceptStaffInvitationRequest request)
    {
        var result = await _invitationService.AcceptStaffInvitationAsync(token, request);
        if (!result.Success)
        {
            return result.Error?.Code switch
            {
                "EMAIL_MISMATCH" or "REVOKED" or "ALREADY_USED" or "EXPIRED" or "EMAIL_IN_USE" or "ALREADY_STAFF" or "INVALID_TOKEN" or "BUSINESS_NOT_FOUND" => BadRequest(result),
                _ => BadRequest(result)
            };
        }

        // Same shared session as any other sign-in, so the new staff member lands
        // on the business subdomain already hydrated.
        if (result.Data is { RefreshToken.Length: > 0 } auth)
            _sessionCookie.Write(Request, Response, auth.RefreshToken);

        return Ok(result);
    }
}

/// <summary>
/// Authenticated invitation acceptance for invitees who ALREADY have an identity
/// (the "I already have an account" path on the acceptance page).
///
/// Route: POST /v1/invitations/{token}/accept-existing
///
/// EXPLICIT [Authorize]: unlike the public endpoints above, this one deliberately
/// requires a session — the signed-in email must equal the invited email, which
/// is the ownership proof the public token-only endpoint cannot provide.
/// </summary>
[ApiController]
[Route("v1/invitations")]
[Produces("application/json")]
[Authorize]
public class InvitationAcceptanceController : ControllerBase
{
    private readonly IInvitationService _invitationService;
    private readonly ISessionCookieService _sessionCookie;
    private readonly ILogger<InvitationAcceptanceController> _logger;

    public InvitationAcceptanceController(
        IInvitationService invitationService,
        ISessionCookieService sessionCookie,
        ILogger<InvitationAcceptanceController> logger)
    {
        _invitationService = invitationService;
        _sessionCookie = sessionCookie;
        _logger = logger;
    }

    /// <summary>
    /// Link the signed-in identity to the inviting business as staff.
    /// </summary>
    /// <response code="200">Joined; tenant-aware tokens issued.</response>
    /// <response code="400">Invalid, expired, revoked or already-used token.</response>
    /// <response code="403">The session's email is not the invited address.</response>
    /// <response code="409">A conflicting tenant persona already exists.</response>
    [HttpPost("{token}/accept-existing")]
    [ProducesResponseType(typeof(ApiResponse<AuthResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AcceptForExistingAccount(string token)
    {
        var userAuthId = GetUserAuthId();
        if (userAuthId == null)
            return Unauthorized(ApiResponse<AuthResponse>.Fail("UNAUTHORIZED", "Invalid token."));

        var result = await _invitationService.AcceptStaffInvitationForExistingAccountAsync(userAuthId.Value, token);
        if (!result.Success)
        {
            _logger.LogWarning("Existing-account invitation acceptance failed: {Code}", result.Error?.Code);
            return result.Error?.Code switch
            {
                "EMAIL_MISMATCH" => StatusCode(StatusCodes.Status403Forbidden, result),
                "ALREADY_STAFF" or "ALREADY_MEMBER" or "OWNS_BUSINESS" or
                    "ROLE_NOT_ELIGIBLE" or "REVOKED" or "ALREADY_USED" or "EXPIRED" or "BUSINESS_NOT_FOUND"
                    => Conflict(result),
                "UNAUTHORIZED" => Unauthorized(result),
                _ => BadRequest(result)
            };
        }

        if (result.Data is { RefreshToken.Length: > 0 } auth)
            _sessionCookie.Write(Request, Response, auth.RefreshToken);

        return Ok(result);
    }

    private Guid? GetUserAuthId()
    {
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
