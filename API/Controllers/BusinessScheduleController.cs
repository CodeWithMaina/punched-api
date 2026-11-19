using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Business operating schedule. Owner-only: the schedule decides when the whole
/// business can be booked, so it is a management action, not a staff preference.
/// Base route: /v1/businesses/{businessId}/schedule.
/// </summary>
[ApiController]
[Route("v1/businesses/{businessId:guid}/schedule")]
[Produces("application/json")]
[Authorize(Policy = MembershipPolicies.Owner)]
public class BusinessScheduleController : ControllerBase
{
    private readonly IBusinessScheduleService _schedule;

    public BusinessScheduleController(IBusinessScheduleService schedule) => _schedule = schedule;

    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<BusinessScheduleResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid businessId)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();
        var result = await _schedule.GetAsync(businessId, userId.Value, role);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    [HttpPut]
    [ProducesResponseType(typeof(ApiResponse<BusinessScheduleResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Save(Guid businessId, [FromBody] SaveBusinessScheduleRequest request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();
        var result = await _schedule.SaveAsync(businessId, request, userId.Value, role);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    [HttpPost("exceptions")]
    [ProducesResponseType(typeof(ApiResponse<BusinessScheduleExceptionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpsertException(Guid businessId, [FromBody] SaveScheduleExceptionRequest request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();
        var result = await _schedule.UpsertExceptionAsync(businessId, request, userId.Value, role);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    [HttpDelete("exceptions/{exceptionId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteException(Guid businessId, Guid exceptionId)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();
        var result = await _schedule.DeleteExceptionAsync(businessId, exceptionId, userId.Value, role);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    private (Guid? UserId, string? Role) GetIdentity()
    {
        // Mirrors PaymentsController/BusinessController: the JWT's `userId` claim is the
        // platform User id the membership table references (NameIdentifier is not it).
        var role = User.FindFirstValue(ClaimTypes.Role) ?? User.FindFirstValue("role");
        var claim = User.FindFirstValue("userId");
        return (Guid.TryParse(claim, out var userId) ? userId : null, role);
    }

    private IActionResult MapFailure<T>(ApiResponse<T> result) =>
        result.Error?.Code switch
        {
            "NOT_FOUND" => NotFound(result),
            "FORBIDDEN" => StatusCode(StatusCodes.Status403Forbidden, result),
            _ => BadRequest(result)
        };
}
