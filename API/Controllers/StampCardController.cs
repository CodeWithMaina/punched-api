using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PunchedApi.API.Filters;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;
using Microsoft.AspNetCore.RateLimiting;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Stamp card management — stamp cards are children of loyalty programs (campaigns).
/// Base route: /v1/programs/me/{programId}/stamp-cards + /v1/stamp-cards/me/{id}
/// </summary>
[ApiController]
[Authorize(Roles = "Business")]
[RequireModule("loyalty")]
[EnableRateLimiting("general")]
public class StampCardController : ControllerBase
{
    private readonly IStampCardService _stampCardService;

    public StampCardController(IStampCardService stampCardService)
    {
        _stampCardService = stampCardService;
    }

    /// <summary>List all stamp cards belonging to a campaign.</summary>
    [HttpGet("v1/programs/me/{programId:guid}/stamp-cards")]
    [ProducesResponseType(typeof(ApiResponse<List<StampCardResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProgramStampCards(Guid programId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _stampCardService.GetProgramStampCardsAsync(userId.Value, programId);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>Create a new stamp card under a campaign.</summary>
    [HttpPost("v1/programs/me/{programId:guid}/stamp-cards")]
    [ProducesResponseType(typeof(ApiResponse<StampCardResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateStampCard(Guid programId, [FromBody] CreateStampCardRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _stampCardService.CreateStampCardAsync(userId.Value, programId, request);
        return result.Success ? StatusCode(StatusCodes.Status201Created, result) : BadRequest(result);
    }

    /// <summary>Get one stamp card owned by the authenticated business.</summary>
    [HttpGet("v1/stamp-cards/me/{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<StampCardResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStampCard(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _stampCardService.GetStampCardAsync(userId.Value, id);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>Update a stamp card (name, goal, reward, design assignment).</summary>
    [HttpPut("v1/stamp-cards/me/{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<StampCardResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateStampCard(Guid id, [FromBody] UpdateStampCardRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _stampCardService.UpdateStampCardAsync(userId.Value, id, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Transition a stamp card's lifecycle status (activate/inactivate/archive/...).</summary>
    [HttpPatch("v1/stamp-cards/me/{id:guid}/status")]
    [ProducesResponseType(typeof(ApiResponse<StampCardResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateStampCardStatus(Guid id, [FromBody] UpdateStampCardStatusRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _stampCardService.UpdateStampCardStatusAsync(userId.Value, id, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Duplicate a stamp card as a new draft.</summary>
    [HttpPost("v1/stamp-cards/me/{id:guid}/duplicate")]
    [ProducesResponseType(typeof(ApiResponse<StampCardResponse>), StatusCodes.Status201Created)]
    public async Task<IActionResult> DuplicateStampCard(Guid id, [FromBody] DuplicateStampCardRequest? request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _stampCardService.DuplicateStampCardAsync(userId.Value, id, request);
        return result.Success ? StatusCode(StatusCodes.Status201Created, result) : BadRequest(result);
    }

    /// <summary>Delete a stamp card — only allowed when it is inactive/archived (safe to delete).</summary>
    [HttpDelete("v1/stamp-cards/me/{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteStampCard(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _stampCardService.DeleteStampCardAsync(userId.Value, id);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}
