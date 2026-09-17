using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Filters;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Manual stamping, the stamp ledger and the loyalty activity/audit log.
/// Base route: /v1/loyalty/stamps
///
/// The ledger is append-only: corrections are new transactions carrying actor,
/// reason and timestamp, never edits to history.
/// </summary>
[ApiController]
[Route("v1/loyalty/stamps")]
[Produces("application/json")]
[Authorize(Roles = "Business,Staff")]
[RequireModule("loyalty")]
[EnableRateLimiting("general")]
public class LoyaltyStampingController : ControllerBase
{
    private readonly ILoyaltyStampingService _stampingService;

    public LoyaltyStampingController(ILoyaltyStampingService stampingService)
    {
        _stampingService = stampingService;
    }

    /// <summary>
    /// Awards or corrects stamps on a customer's card. A negative amount records a
    /// correction; every entry keeps the actor, reason and timestamp.
    /// </summary>
    [HttpPost("manual")]
    [ProducesResponseType(typeof(ApiResponse<ManualStampResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AwardManualStamps([FromBody] ManualStampRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _stampingService.AwardManualStampsAsync(userId.Value, request);

        if (result.Success) return Ok(result);
        return result.Error?.Code == "MODULE_DISABLED"
            ? StatusCode(StatusCodes.Status403Forbidden, result)
            : BadRequest(result);
    }

    /// <summary>Paged loyalty activity/audit log for the caller's business.</summary>
    [HttpGet("activity")]
    [ProducesResponseType(typeof(ApiResponse<LoyaltyActivityPage>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActivity([FromQuery] LoyaltyActivityQuery query)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _stampingService.GetActivityAsync(userId.Value, query);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>A single card's stamp transaction history.</summary>
    [HttpGet("cards/{cardId:guid}/history")]
    [ProducesResponseType(typeof(ApiResponse<LoyaltyActivityPage>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCardHistory(
        Guid cardId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _stampingService.GetCardHistoryAsync(userId.Value, cardId, page, pageSize);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}