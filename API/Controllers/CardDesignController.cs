using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Filters;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Business-facing card design availability and preview. Base route: /v1/card-designs
///
/// Scope (plan §15): a business may **see** the designs available to it and
/// **select** one for a loyalty program. It cannot author HTML — that is an
/// Admin capability (<c>AdminCardDesignController</c>), so a subscription never
/// grants design authorship.
///
/// The default Punched card is part of the core loyalty experience, which is why
/// this controller is gated by <c>loyalty</c> only: the optional
/// <c>customCardDesign</c> module decides whether the business's own designs are
/// included in the response (enforced server-side in the service).
/// </summary>
[ApiController]
[Produces("application/json")]
[Route("v1/card-designs")]
[Authorize(Roles = "Business")]
[RequireModule("loyalty")]
[EnableRateLimiting("general")]
public class CardDesignController : ControllerBase
{
    private readonly ICardDesignService _cardDesignService;

    public CardDesignController(ICardDesignService cardDesignService)
    {
        _cardDesignService = cardDesignService;
    }

    /// <summary>
    /// The designs this business may choose from: the platform default plus its
    /// own designs when the Custom Card Design module is enabled. Never exposes
    /// another business's designs (tenant isolation is enforced server-side).
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(ApiResponse<List<AvailableCardDesignResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAvailableCardDesigns()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _cardDesignService.GetAvailableDesignsAsync(userId.Value);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>
    /// Renders a card preview with safe sample data through the production
    /// pipeline. Always available — a loyalty-only business can still preview
    /// the default card. Raw HTML is sanitized before rendering.
    /// </summary>
    [HttpPost("me/preview")]
    [ProducesResponseType(typeof(ApiResponse<PreviewCardDesignResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PreviewCardDesign([FromBody] PreviewCardDesignRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _cardDesignService.PreviewForBusinessAsync(userId.Value, request);
        if (result.Success) return Ok(result);

        return result.Error?.Code switch
        {
            "NOT_FOUND" => NotFound(result),
            "MODULE_DISABLED" => StatusCode(StatusCodes.Status403Forbidden, result),
            _ => BadRequest(result)
        };
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}
