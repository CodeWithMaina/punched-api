using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Filters;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>Business-owner/staff access to a card within their resolved business scope.</summary>
[ApiController]
[Route("v1/cards/managed")]
[Produces("application/json")]
[Authorize(Roles = "Business,Staff")]
[RequireModule("stamps")]
[EnableRateLimiting("general")]
public sealed class ManagedLoyaltyCardController : ControllerBase
{
    private readonly ILoyaltyService _loyaltyService;

    public ManagedLoyaltyCardController(ILoyaltyService loyaltyService)
    {
        _loyaltyService = loyaltyService;
    }

    /// <summary>Gets one card only when the owner/staff actor belongs to its business.</summary>
    [HttpGet("{cardId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<LoyaltyCardResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid cardId)
    {
        var claim = User.FindFirst("userId")?.Value;
        if (!Guid.TryParse(claim, out var userId)) return Unauthorized();

        var result = await _loyaltyService.GetCardByIdAsync(userId, cardId);
        return result.Success ? Ok(result) : NotFound(result);
    }
}