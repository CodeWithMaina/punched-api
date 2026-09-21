using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Admin card design authoring. Base routes:
///   /v1/admin/businesses/{businessId}/card-designs
///   /v1/admin/card-designs
///
/// Responsibility split (plan §15): Admin controls <b>who can create/manage
/// designs</b>; the business subscription controls <b>which businesses can use
/// custom designs</b>. Authoring therefore requires only platform admin access —
/// it is intentionally NOT gated by the <c>customCardDesign</c> module, so an
/// Admin can prepare designs for a business before (or after) it subscribes.
/// </summary>
[ApiController]
[Produces("application/json")]
[Authorize(Roles = "Admin")]
[EnableRateLimiting("general")]
public class AdminCardDesignController : ControllerBase
{
    private readonly ICardDesignService _cardDesignService;

    public AdminCardDesignController(ICardDesignService cardDesignService)
    {
        _cardDesignService = cardDesignService;
    }

    /// <summary>All card designs authored for the target business.</summary>
    [HttpGet("v1/admin/businesses/{businessId:guid}/card-designs")]
    [ProducesResponseType(typeof(ApiResponse<List<CardDesignResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBusinessCardDesigns(Guid businessId)
    {
        var result = await _cardDesignService.GetDesignsForBusinessAsync(businessId);
        if (result.Success) return Ok(result);

        return result.Error?.Code == "BUSINESS_NOT_FOUND" ? NotFound(result) : BadRequest(result);
    }

    /// <summary>
    /// Creates a card design for the target business. The HTML is validated and
    /// sanitized server-side before storage.
    /// </summary>
    [HttpPost("v1/admin/businesses/{businessId:guid}/card-designs")]
    [ProducesResponseType(typeof(ApiResponse<CardDesignResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateBusinessCardDesign(
        Guid businessId,
        [FromBody] CreateCardDesignRequest request)
    {
        var result = await _cardDesignService.CreateDesignAsync(businessId, request, GetUserId());
        if (result.Success) return StatusCode(StatusCodes.Status201Created, result);

        return result.Error?.Code switch
        {
            "BUSINESS_NOT_FOUND" => NotFound(result),
            _ => BadRequest(result)
        };
    }

    /// <summary>Updates a card design (re-sanitizes any new HTML).</summary>
    [HttpPut("v1/admin/card-designs/{designId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<CardDesignResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateCardDesign(Guid designId, [FromBody] UpdateCardDesignRequest request)
    {
        var result = await _cardDesignService.UpdateDesignAsync(designId, request, GetUserId());
        if (result.Success) return Ok(result);

        return result.Error?.Code switch
        {
            "NOT_FOUND" => NotFound(result),
            _ => BadRequest(result)
        };
    }

    /// <summary>
    /// Activates or deactivates a design without deleting it. Deactivation makes
    /// existing selections fall back to the default while preserving the data.
    /// </summary>
    [HttpPatch("v1/admin/card-designs/{designId:guid}/status")]
    [ProducesResponseType(typeof(ApiResponse<CardDesignResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetCardDesignStatus(Guid designId, [FromBody] SetCardDesignStatusRequest request)
    {
        var result = await _cardDesignService.SetDesignActiveAsync(designId, request.IsActive);
        if (result.Success) return Ok(result);

        return result.Error?.Code switch
        {
            "NOT_FOUND" => NotFound(result),
            _ => BadRequest(result)
        };
    }

    /// <summary>
    /// Deletes a card design. Refused while any loyalty program or stamp card
    /// still selects it — deactivate instead so selections remain reversible.
    /// </summary>
    [HttpDelete("v1/admin/card-designs/{designId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteCardDesign(Guid designId)
    {
        var result = await _cardDesignService.DeleteDesignAsync(designId);
        if (result.Success) return Ok(result);

        return result.Error?.Code switch
        {
            "NOT_FOUND" => NotFound(result),
            _ => BadRequest(result)
        };
    }

    /// <summary>
    /// Renders a design (raw HTML and/or a saved design) with safe sample data
    /// for the chosen business — **before** the row is persisted (plan §6, §8).
    /// Raw HTML is sanitized with exactly the same rules used at save time.
    /// </summary>
    [HttpPost("v1/admin/card-designs/preview")]
    [ProducesResponseType(typeof(ApiResponse<PreviewCardDesignResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PreviewCardDesign([FromBody] AdminPreviewCardDesignRequest request)
    {
        var result = await _cardDesignService.PreviewAsync(request);
        if (result.Success) return Ok(result);

        return result.Error?.Code switch
        {
            "BUSINESS_NOT_FOUND" or "NOT_FOUND" => NotFound(result),
            _ => BadRequest(result)
        };
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}

/// <summary>PATCH /v1/admin/card-designs/{id}/status request body.</summary>
public class SetCardDesignStatusRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}