using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// The public storefront booking API — the one a customer can use without an account, without a
/// token, and without proving they own anything.
///
/// <para><b>Why it is separate from <c>POST /v1/appointments</c>.</b> That endpoint is the
/// signed-in customer app's: it derives the customer from the token and requires the Customer
/// role. A storefront has visitors of every kind — anonymous, a customer of another business, and
/// the business's own owner browsing their own page — and none of those should be turned away or
/// asked to verify a number first. This endpoint therefore takes the customer's details, resolves
/// the business-specific customer record behind them, and books.</para>
///
/// <para><b>What a caller cannot do here.</b> Name a customer (there is no customer id in the
/// request), book at a business other than the one in the route, attach a confirmation to another
/// business's appointment, or reach any administrative endpoint: verification, rejection and the
/// payments module all stay behind owner/staff policies.</para>
///
/// <para><b>Identity is optional, never required.</b> A bearer token, when present and valid, is
/// used to reuse a signed-in <b>customer's</b> identity. Any other caller — no token, an owner's
/// token, a staff token — is served as the visitor their details describe.</para>
/// </summary>
[ApiController]
[Route("v1/businesses")]
[Produces("application/json")]
public class PublicBookingController : ControllerBase
{
    private readonly IPublicBookingService _publicBooking;
    private readonly BookingHoldService _holds;
    private readonly ITenantContext? _tenant;
    private readonly IConfiguration _configuration;

    public PublicBookingController(
        IPublicBookingService publicBooking,
        BookingHoldService holds,
        IConfiguration configuration,
        ITenantContext? tenant = null)
    {
        _publicBooking = publicBooking;
        _holds = holds;
        _configuration = configuration;
        _tenant = tenant;
    }

    /// <summary>
    /// Holds the chosen slot while the customer pays the booking fee.
    ///
    /// <para>This is the step that stops two customers paying for the same time: the interval is
    /// claimed for a bounded window and released by itself if the payment never arrives. The
    /// response carries the deadline and the seconds remaining, so the page's countdown is the
    /// backend's own clock rather than a guess.</para>
    /// </summary>
    [HttpPost("{businessId:guid}/public-bookings/holds")]
    [AllowAnonymous]
    [EnableRateLimiting("public-booking")]
    [ProducesResponseType(typeof(ApiResponse<BookingHoldResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<BookingHoldResponse>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse<BookingHoldResponse>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Hold(
        Guid businessId, [FromBody] CreateBookingHoldRequest request, CancellationToken cancellationToken)
    {
        var result = await _holds.CreateAsync(
            businessId,
            request,
            _tenant?.BusinessId,
            _configuration.GetValue<int?>("Booking:SlotHoldMinutes"),
            cancellationToken);

        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// The hold's live state — what the page reads to resynchronise its countdown with the server.
    /// </summary>
    [HttpGet("{businessId:guid}/public-bookings/holds/{holdId:guid}")]
    [AllowAnonymous]
    [EnableRateLimiting("public-booking")]
    [ProducesResponseType(typeof(ApiResponse<BookingHoldResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHold(
        Guid businessId, Guid holdId, CancellationToken cancellationToken)
    {
        var result = await _holds.GetAsync(businessId, holdId, cancellationToken);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Gives a hold up, so the slot is free the moment the customer leaves the flow.</summary>
    [HttpDelete("{businessId:guid}/public-bookings/holds/{holdId:guid}")]
    [AllowAnonymous]
    [EnableRateLimiting("public-booking")]
    [ProducesResponseType(typeof(ApiResponse<BookingHoldResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReleaseHold(
        Guid businessId, Guid holdId, CancellationToken cancellationToken)
    {
        var result = await _holds.ReleaseAsync(businessId, holdId, cancellationToken);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Books an appointment from the storefront. Anonymous, and never dependent on a payment being
    /// verified: the appointment is created and booked, and any M-PESA confirmation supplied with
    /// it is recorded as evidence awaiting the business's review.
    /// </summary>
    [HttpPost("{businessId:guid}/public-bookings")]
    [AllowAnonymous]
    [EnableRateLimiting("public-booking")]
    [ProducesResponseType(typeof(ApiResponse<PublicBookingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<PublicBookingResponse>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse<PublicBookingResponse>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Book(
        Guid businessId, [FromBody] PublicBookingRequest request, CancellationToken cancellationToken)
    {
        // The route owns the business. A body that names a different one is not trusted to
        // redirect the booking: it is overwritten from the route, and the service additionally
        // refuses a business that is not the active tenant.
        request.BusinessId = businessId;

        var result = await _publicBooking.BookAsync(
            CallerUserId(), CallerRole(), _tenant?.BusinessId, request, cancellationToken);

        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Attaches a late M-PESA confirmation to a booking already made from the storefront. The
    /// appointment is never rebuilt or re-created: only the payment's verification state moves.
    /// </summary>
    [HttpPost("{businessId:guid}/public-bookings/{appointmentId:guid}/payment-proof")]
    [AllowAnonymous]
    [EnableRateLimiting("public-booking")]
    [ProducesResponseType(typeof(ApiResponse<PublicBookingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<PublicBookingResponse>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SubmitPaymentProof(
        Guid businessId, Guid appointmentId, [FromBody] SubmitPublicPaymentProofRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _publicBooking.SubmitPaymentProofAsync(
            businessId, appointmentId, request, cancellationToken);

        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>The authenticated caller's id, or null for an anonymous visitor.</summary>
    private Guid? CallerUserId() =>
        Guid.TryParse(User.FindFirst("userId")?.Value, out var id) ? id : null;

    /// <summary>The authenticated caller's role, or null when there is no token.</summary>
    private string? CallerRole() =>
        User.FindFirst(ClaimTypes.Role)?.Value ?? User.FindFirst("role")?.Value;

    private IActionResult MapFailure<T>(ApiResponse<T> result)
        => result.Error?.Code switch
        {
            "NOT_FOUND" or "BUSINESS_NOT_FOUND" => NotFound(result),
            "FORBIDDEN" or "MODULE_DISABLED" => StatusCode(StatusCodes.Status403Forbidden, result),
            // A slot taken while the customer was choosing is a conflict; the customer has to pick
            // another time. Anything else they can fix (details, payment option) is a 422.
            "STAFF_TIME_UNAVAILABLE" or "OVERBOOKING" or "SLOT_UNAVAILABLE"
                or "CONCURRENT_MODIFICATION" or "RESERVATION_EXPIRED" => Conflict(result),
            "PAYMENT_OPTION_DISABLED" or "PAYMENT_OPTION_REQUIRED" or "BOOKING_UNAVAILABLE"
                or "VALIDATION_ERROR" or "RESERVATION_REQUIRED" or "RESERVATION_MISMATCH"
                or "PAYMENT_PROOF_REQUIRED" => StatusCode(StatusCodes.Status422UnprocessableEntity, result),
            _ => BadRequest(result),
        };
}
