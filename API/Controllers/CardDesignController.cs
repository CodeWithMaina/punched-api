using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PunchedApi.API.Filters;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;
using Microsoft.AspNetCore.RateLimiting;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Reusable card design management — businesses create HTML templates and assign
/// them to stamp cards. Base route: /v1/card-designs
/// </summary>
[ApiController]
[Route("v1/card-designs")]
[Authorize(Roles = "Business")]
[RequireModule("loyalty")]
[EnableRateLimiting("general")]
public class CardDesignController : ControllerBase
{
    private readonly IStampCardService _stampCardService;

    public CardDesignController(IStampCardService stampCardService)
    {
        _stampCardService = stampCardService;
    }

    /// <summary>List all card designs owned by the authenticated business.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(ApiResponse<List<CardDesignResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCardDesigns()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _stampCardService.GetCardDesignsAsync(userId.Value);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>Get one card design owned by the authenticated business.</summary>
    [HttpGet("me/{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<CardDesignResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCardDesign(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _stampCardService.GetCardDesignAsync(userId.Value, id);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>Create a card design. The HTML template is validated and sanitized server-side.</summary>
    [HttpPost("me")]
    [ProducesResponseType(typeof(ApiResponse<CardDesignResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateCardDesign([FromBody] CreateCardDesignRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _stampCardService.CreateCardDesignAsync(userId.Value, request);
        return result.Success ? StatusCode(StatusCodes.Status201Created, result) : BadRequest(result);
    }

    /// <summary>Update a card design. Updated HTML is re-sanitized server-side.</summary>
    [HttpPut("me/{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<CardDesignResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateCardDesign(Guid id, [FromBody] UpdateCardDesignRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _stampCardService.UpdateCardDesignAsync(userId.Value, id, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Delete a card design — blocked while it is assigned to stamp cards.</summary>
    [HttpDelete("me/{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteCardDesign(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _stampCardService.DeleteCardDesignAsync(userId.Value, id);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Preview a card design (unsaved HTML or an existing design) rendered with
    /// realistic sample data through the production rendering pipeline.
    /// </summary>
    [HttpPost("me/preview")]
    [ProducesResponseType(typeof(ApiResponse<PreviewCardDesignResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PreviewCardDesign([FromBody] PreviewCardDesignRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _stampCardService.PreviewCardDesignAsync(userId.Value, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}
