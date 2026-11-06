using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Public storefront reads for a single business: its published gallery, its
/// opening hours and a server-side search across its published catalogue and
/// public roster.
///
/// <para>Base route: <c>/v1/businesses</c> (shared with the owner-facing
/// <c>BusinessController</c> and the landing-page configuration controller —
/// the action templates below do not collide with either).</para>
///
/// <para><b>Every action is anonymous and read-only</b>, so the module
/// entitlement gate is applied inside each service against the business the
/// request named (an anonymous caller has no token for <c>[RequireModule]</c>
/// to resolve). Tenant narrowing happens in the same place, so a subdomain can
/// never read another business's data.</para>
/// </summary>
[ApiController]
[Route("v1/businesses")]
[Produces("application/json")]
[EnableRateLimiting("general")]
public class StorefrontController : ControllerBase
{
    private readonly IStorefrontGalleryService _gallery;
    private readonly IStorefrontHoursService _hours;
    private readonly IStorefrontSearchService _search;
    private readonly IPaymentService _payments;

    public StorefrontController(
        IStorefrontGalleryService gallery,
        IStorefrontHoursService hours,
        IStorefrontSearchService search,
        IPaymentService payments)
    {
        _gallery = gallery;
        _hours = hours;
        _search = search;
        _payments = payments;
    }

    /// <summary>
    /// How this business wants to be paid: its manual M-PESA PayBill/Till details and
    /// the booking-fee percentage.
    ///
    /// <para>Anonymous and read-only, because a visitor must be able to see the payment
    /// instructions before they commit to a booking. It returns only what a payer needs
    /// to make a transfer — never Daraja credentials, never the shortcode's secret
    /// material. A business with the channel switched off gets <c>enabled: false</c>
    /// and no numbers, so the storefront cannot render a half-configured instruction.</para>
    /// </summary>
    [HttpGet("{businessId:guid}/payment-details")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<PublicPaymentDetailsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPublicPaymentDetails(Guid businessId, CancellationToken cancellationToken)
    {
        var result = await _payments.GetPublicPaymentDetailsAsync(businessId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// The business's published photo gallery, ordered for display. Images the
    /// owner uploaded but never attached, and images still processing, are not
    /// returned.
    /// </summary>
    [HttpGet("{businessId:guid}/gallery")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<PublicGalleryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPublicGallery(Guid businessId, CancellationToken cancellationToken)
    {
        var result = await _gallery.GetPublicGalleryAsync(businessId, cancellationToken);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// The business's opening hours, business-local, derived from the same
    /// scheduling data the availability engine reads, plus dated exceptions.
    /// </summary>
    [HttpGet("{businessId:guid}/opening-hours")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<PublicOpeningHoursResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPublicOpeningHours(Guid businessId, CancellationToken cancellationToken)
    {
        var result = await _hours.GetPublicOpeningHoursAsync(businessId, cancellationToken);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Searches the business's published services and publicly visible staff.
    /// <paramref name="type"/> narrows the search to `services` or `team`.
    /// </summary>
    [HttpGet("{businessId:guid}/search")]
    [AllowAnonymous]
    [EnableRateLimiting("storefront-search")]
    [ProducesResponseType(typeof(ApiResponse<StorefrontSearchResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Search(
        Guid businessId,
        [FromQuery] string? q,
        [FromQuery] string? type,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var result = await _search.SearchAsync(businessId, q, type, page, pageSize, cancellationToken);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Maps an ApiResponse failure to the HTTP status dictated by backend.md §9.
    /// Mirrors <c>ServiceCatalogController</c> so the storefront reads a blocked
    /// module or an unknown business the same way on every public endpoint.
    /// </summary>
    private IActionResult MapFailure<T>(ApiResponse<T> result)
        => result.Error?.Code switch
        {
            "NOT_FOUND" or "BUSINESS_NOT_FOUND" => NotFound(result),
            // A module the business never bought is surfaced as 403, exactly as
            // the public catalogue endpoint does.
            "MODULE_DISABLED" => StatusCode(StatusCodes.Status403Forbidden, result),
            "LOAD_FAILED" or "SEARCH_FAILED" => StatusCode(StatusCodes.Status500InternalServerError, result),
            _ => BadRequest(result)
        };
}
