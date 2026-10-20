using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Customer dashboard — single bootstrap endpoint for the customer dashboard shell.
/// GET /v1/customers/me/dashboard/bootstrap — returns business context, customer
/// relationship, capabilities, navigation and homepage summary in one round trip,
/// replacing the fragmented /businesses/by-slug/{slug} + /customers/me/businesses +
/// /me/modules requests that previously raced and got canceled on remount.
/// </summary>
[ApiController]
[Route("v1/customers/me")]
[Produces("application/json")]
[Authorize(Roles = "Customer")]
public class CustomerDashboardController : ControllerBase
{
    private readonly BusinessService _businessService;

    public CustomerDashboardController(BusinessService businessService)
    {
        _businessService = businessService;
    }

    /// <summary>
    /// Customer-dashboard bootstrap: business identity + customer relationship +
    /// effective capabilities + navigation + homepage summary in ONE call.
    /// Resolved server-side from the authenticated customer — no client-supplied
    /// business id. Disabled modules are an expected state, not an error.
    /// </summary>
    [HttpGet("dashboard/bootstrap")]
    [ProducesResponseType(typeof(ApiResponse<CustomerDashboardBootstrapResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCustomerDashboardBootstrap()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _businessService.GetCustomerDashboardBootstrapAsync(userId.Value);
        if (!result.Success)
        {
            return result.Error?.Code switch
            {
                "NOT_FOUND" or "NO_BUSINESS" => NotFound(result),
                "FORBIDDEN" => StatusCode(StatusCodes.Status403Forbidden, result),
                _ => BadRequest(result),
            };
        }
        return Ok(result);
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}