using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Business operating-mode and provider-category configuration.
/// Base route: /v1/businesses/me
///
/// <para>These are the owner console's controls for "how does my business
/// operate?" (Solo vs Multi-Staff) and "which categories does each provider
/// specialise in?". They delegate entirely to
/// <see cref="IOperatingModeService"/>, which is the single writer of the
/// persisted operating mode and the provider ↔ category links.</para>
/// </summary>
[ApiController]
[Route("v1/businesses/me")]
[Produces("application/json")]
[Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
[EnableRateLimiting("general")]
public class BusinessOperatingModeController : ControllerBase
{
    private readonly IOperatingModeService _operatingMode;

    public BusinessOperatingModeController(IOperatingModeService operatingMode)
    {
        _operatingMode = operatingMode;
    }

    /// <summary>
    /// The business's current operating mode, its effective provider model, and
    /// the counts the settings screen needs. Owner or staff may read this (the
    /// dashboard badge uses the same shape).
    /// </summary>
    [HttpGet("operating-mode")]
    [ProducesResponseType(typeof(ApiResponse<OperatingModeResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOperatingMode()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _operatingMode.GetAsync(userId.Value, GetTenantRole());
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Switches the business between Solo and Multi-Staff in one action. Owner
    /// only. The switch never deletes staff, services, categories or history.
    /// </summary>
    [HttpPut("operating-mode")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<OperatingModeResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateOperatingMode([FromBody] UpdateOperatingModeRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _operatingMode.UpdateAsync(userId.Value, GetTenantRole(), request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>A provider's (staff member's or owner's) category specialisations.</summary>
    [HttpGet("providers/{providerUserId:guid}/categories")]
    [ProducesResponseType(typeof(ApiResponse<ProviderCategoriesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProviderCategories(Guid providerUserId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _operatingMode.GetProviderCategoriesAsync(userId.Value, GetTenantRole(), providerUserId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Replaces a provider's category specialisations. The supplied list is the
    /// whole set — assigning makes the provider eligible for every active service
    /// in those categories; removing withdraws that eligibility.
    /// </summary>
    [HttpPut("providers/{providerUserId:guid}/categories")]
    [ProducesResponseType(typeof(ApiResponse<ProviderCategoriesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetProviderCategories(
        Guid providerUserId, [FromBody] SetProviderCategoriesRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _operatingMode.SetProviderCategoriesAsync(
            userId.Value, GetTenantRole(), providerUserId, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value
                    ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    /// <summary>
    /// The caller tenant persona. The service applies its own scoping; an absent
    /// claim is treated as the more restrictive Staff persona.
    /// </summary>
    private string GetTenantRole()
    {
        var role = User.FindFirst(ClaimTypes.Role)?.Value ?? User.FindFirst("role")?.Value;

        return string.Equals(role?.Trim(), "Business", StringComparison.OrdinalIgnoreCase)
            ? "Business"
            : "Staff";
    }

    private IActionResult MapFailure<T>(ApiResponse<T> result)
        => result.Error?.Code switch
        {
            "NOT_FOUND" or "CATEGORY_NOT_FOUND" => NotFound(result),
            "FORBIDDEN" or "MODULE_DISABLED" => StatusCode(StatusCodes.Status403Forbidden, result),
            "VALIDATION_ERROR" => BadRequest(result),
            _ => BadRequest(result)
        };
}
