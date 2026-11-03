using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Booking without an account.
///
/// <para>Two anonymous, rate-limited steps: send a one-time code to the address
/// the visitor supplied, then exchange the code for a short-lived customer token.
/// The booking itself is not made here — the token is used against the ordinary
/// customer appointment API, so every existing rule (availability, staff
/// eligibility, working hours, booking windows, payment, module entitlements)
/// applies unchanged and nothing is bypassed for guests.</para>
///
/// <para><b>Rate limiting is per IP</b>, because the identity being protected is
/// in the request body and cannot be part of an ASP.NET rate-limit partition. The
/// per-identity protection is layered underneath: a resend cooldown, a bounded
/// code lifetime, and a hard ceiling on wrong attempts, all enforced on the row
/// itself.</para>
/// </summary>
[ApiController]
[Route("v1/businesses")]
[Produces("application/json")]
public class GuestBookingController : ControllerBase
{
    private readonly IGuestBookingService _guestBooking;

    public GuestBookingController(IGuestBookingService guestBooking)
    {
        _guestBooking = guestBooking;
    }

    /// <summary>
    /// Sends a one-time verification code to the visitor's email address.
    /// </summary>
    [HttpPost("{businessId:guid}/guest-booking/start")]
    [AllowAnonymous]
    [EnableRateLimiting("guest-booking-start")]
    [ProducesResponseType(typeof(ApiResponse<StartGuestBookingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Start(
        Guid businessId, [FromBody] StartGuestBookingRequest request, CancellationToken cancellationToken)
    {
        var result = await _guestBooking.StartAsync(businessId, request, cancellationToken);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Exchanges the code for a short-lived customer token scoped to the business.
    /// </summary>
    [HttpPost("{businessId:guid}/guest-booking/verify")]
    [AllowAnonymous]
    [EnableRateLimiting("guest-booking-verify")]
    [ProducesResponseType(typeof(ApiResponse<VerifyGuestBookingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Verify(
        Guid businessId, [FromBody] VerifyGuestBookingRequest request, CancellationToken cancellationToken)
    {
        var result = await _guestBooking.VerifyAsync(businessId, request, cancellationToken);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Maps an ApiResponse failure to the HTTP status the code deserves, so a
    /// client can branch on the status without parsing prose.
    /// </summary>
    private IActionResult MapFailure<T>(ApiResponse<T> result)
        => result.Error?.Code switch
        {
            "NOT_FOUND" or "BUSINESS_NOT_FOUND" => NotFound(result),
            "MODULE_DISABLED" or "FORBIDDEN" => StatusCode(StatusCodes.Status403Forbidden, result),
            // The address already has an account: sign in instead of verifying.
            "ACCOUNT_EXISTS" => Conflict(result),
            "CODE_EXPIRED" => StatusCode(StatusCodes.Status410Gone, result),
            // Both are "slow down": one is the resend cooldown, the other the
            // attempt ceiling.
            "RESEND_TOO_SOON" or "TOO_MANY_ATTEMPTS" =>
                StatusCode(StatusCodes.Status429TooManyRequests, result),
            _ => BadRequest(result)
        };
}
