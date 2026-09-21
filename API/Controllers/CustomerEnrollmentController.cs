using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Customer self-service enrollment: the authoritative Customer-Business and
/// Customer-StampCard relationships. CustomerId always comes from the JWT.
/// </summary>
[ApiController]
[Route("v1/customers/me")]
[Produces("application/json")]
[Authorize(Roles = "Customer")]
[EnableRateLimiting("general")]
public class CustomerEnrollmentController : ControllerBase
{
    private readonly ICustomerEnrollmentService _enrollments;

    public CustomerEnrollmentController(ICustomerEnrollmentService enrollments)
    { _enrollments = enrollments; }

    [HttpGet("businesses")]
    public async Task<IActionResult> GetMyBusinesses()
    {
        var id = GetUserId();
        if (id == null) return Unauthorized();
        return Ok(await _enrollments.GetMyBusinessesAsync(id.Value));
    }

    [HttpPost("businesses/{businessId:guid}/enroll")]
    public async Task<IActionResult> Enroll(Guid businessId, [FromBody] EnrollBusinessRequest? request)
    {
        var id = GetUserId();
        if (id == null) return Unauthorized();
        var result = await _enrollments.EnrollAsync(id.Value, businessId, request?.Source);
        if (!result.Success) return result.Error?.Code == "NOT_FOUND" ? NotFound(result) : BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("businesses/{businessId:guid}")]
    public async Task<IActionResult> Leave(Guid businessId)
    {
        var id = GetUserId();
        if (id == null) return Unauthorized();
        return Ok(await _enrollments.LeaveAsync(id.Value, businessId));
    }

    [HttpGet("stamp-cards")]
    public async Task<IActionResult> GetMyStampCards([FromQuery] Guid? businessId)
    {
        var id = GetUserId();
        if (id == null) return Unauthorized();
        return Ok(await _enrollments.GetMyStampCardsAsync(id.Value, businessId));
    }

    [HttpPost("stamp-cards/{stampCardId:guid}/join")]
    public async Task<IActionResult> JoinStampCard(Guid stampCardId)
    {
        var id = GetUserId();
        if (id == null) return Unauthorized();
        var result = await _enrollments.JoinStampCardAsync(id.Value, stampCardId);
        if (!result.Success) return result.Error?.Code switch
        {
            "NOT_FOUND" => NotFound(result),
            "NOT_ENROLLED" => StatusCode(StatusCodes.Status403Forbidden, result),
            _ => BadRequest(result)
        };
        return Ok(result);
    }

    [HttpDelete("stamp-cards/{stampCardId:guid}")]
    public async Task<IActionResult> LeaveStampCard(Guid stampCardId)
    {
        var id = GetUserId();
        if (id == null) return Unauthorized();
        return Ok(await _enrollments.LeaveStampCardAsync(id.Value, stampCardId));
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}
