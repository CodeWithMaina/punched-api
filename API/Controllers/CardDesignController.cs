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
    /// the default card. Raw HTML is sanitized before rendering; an unsaved
    /// structured <c>config</c> is validated + rendered identically (live
    /// designer preview).
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

    // ── Business config-based design authoring (§5, §6, §16) ────

    /// <summary>
    /// The caller business's own custom designs (metadata + config). Reads stay
    /// available even when the customCardDesign module is off — a downgrade must
    /// never hide existing designs; writes are module-gated server-side.
    /// </summary>
    [HttpGet("me/designs")]
    [ProducesResponseType(typeof(ApiResponse<List<CardDesignResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyDesigns()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _cardDesignService.GetMyDesignsAsync(userId.Value);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Creates a business-owned design from a validated structured config.
    /// The server generates + sanitizes the template; raw HTML is never accepted
    /// on this route.
    /// </summary>
    [HttpPost("me/designs")]
    [ProducesResponseType(typeof(ApiResponse<CardDesignResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateMyDesign([FromBody] CreateBusinessCardDesignRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _cardDesignService.CreateBusinessDesignAsync(userId.Value, request);
        if (result.Success) return StatusCode(StatusCodes.Status201Created, result);
        return MapFailure(result);
    }

    /// <summary>Updates one of the caller business's designs (append-only versioning).</summary>
    [HttpPut("me/designs/{designId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<CardDesignResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateMyDesign(Guid designId, [FromBody] UpdateBusinessCardDesignRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _cardDesignService.UpdateBusinessDesignAsync(userId.Value, designId, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Append-only presentation history of one of the caller business's designs.</summary>
    [HttpGet("me/designs/{designId:guid}/versions")]
    [ProducesResponseType(typeof(ApiResponse<List<CardDesignVersionResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyDesignVersions(Guid designId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _cardDesignService.GetVersionsForBusinessAsync(userId.Value, designId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Stable failure mapping that never distinguishes tenants (§20).</summary>
    private IActionResult MapFailure<T>(ApiResponse<T> result) => result.Error?.Code switch
    {
        "NOT_FOUND" => NotFound(result),
        "MODULE_DISABLED" or "FORBIDDEN" => StatusCode(StatusCodes.Status403Forbidden, result),
        "DESIGN_LIMIT_REACHED" => StatusCode(StatusCodes.Status429TooManyRequests, result),
        "CONFLICT" => StatusCode(StatusCodes.Status409Conflict, result),
        _ => BadRequest(result)
    };

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}
