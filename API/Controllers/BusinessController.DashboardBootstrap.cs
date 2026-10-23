using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Business controller — dashboard bootstrap endpoint.
/// Split from BusinessController.cs (plugin module architecture, Phase 5).
/// GET /v1/businesses/me/dashboard/bootstrap — the single call the owner
/// dashboard shell makes on cold load (identity + entitlements + permissions
/// + plan + navigation + reminder settings), replacing independent
/// /businesses/me and /me/modules round trips. Deliberately NOT output-cached:
/// the payload is per-authenticated-caller (role permissions + entitlements).
/// </summary>
public partial class BusinessController
{
    /// <summary>
    /// Owner-dashboard bootstrap: business identity + program flags, effective
    /// module entitlements, owner-role permission codes, plan status,
    /// navigation tabs and reminder settings in ONE round trip. Resolved from
    /// the JWT only — no client-supplied business id.
    /// </summary>
    [HttpGet("me/dashboard/bootstrap")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<DashboardBootstrapResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDashboardBootstrap()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _businessService.GetDashboardBootstrapAsync(userId.Value);
        if (!result.Success) return NotFound(result);
        return Ok(result);
    }
}
