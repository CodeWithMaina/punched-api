using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Controlled business landing-page configuration.
/// Owner: GET/PUT me/landing-page (own business only, version-guarded).
/// Public: GET public/{id}/landing-page (sanitized, capability-filtered).
/// </summary>
[ApiController]
[Route("v1/businesses")]
[Produces("application/json")]
[EnableRateLimiting("general")]
public class LandingPageController : ControllerBase
{
    private readonly ILandingPageService _landingPages;

    public LandingPageController(ILandingPageService landingPages)
    {
        _landingPages = landingPages;
    }

    [HttpGet("me/landing-page")]
    [Authorize(Roles = "Business")]
    [ProducesResponseType(typeof(ApiResponse<LandingPageConfigResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyLandingPage()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _landingPages.GetForOwnerAsync(userId.Value);
        if (!result.Success) return NotFound(result);
        return Ok(result);
    }

    [HttpPut("me/landing-page")]
    [Authorize(Roles = "Business")]
    [ProducesResponseType(typeof(ApiResponse<LandingPageConfigResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateMyLandingPage([FromBody] UpdateLandingPageRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _landingPages.UpdateForOwnerAsync(userId.Value, request);
        if (!result.Success) return MapFailure(result);
        return Ok(result);
    }

    [HttpGet("public/{businessId:guid}/landing-page")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<PublicLandingPageResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPublicLandingPage(Guid businessId)
    {
        var result = await _landingPages.GetPublicAsync(businessId);
        if (!result.Success) return NotFound(result);
        return Ok(result);
    }

    private IActionResult MapFailure<T>(ApiResponse<T> result)
        => result.Error?.Code switch
        {
            "NOT_FOUND" => NotFound(result),
            "STALE_VERSION" => Conflict(result),
            "MEDIA_NOT_FOUND" => NotFound(result),
            _ => BadRequest(result),
        };

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}
