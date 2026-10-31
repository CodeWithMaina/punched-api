using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Filters;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Staff position (job title) management. Base route: /v1/businesses/me/staff/positions
///
/// <para>A position is descriptive metadata. It grants no permission and no
/// service eligibility, so these endpoints never touch RBAC or
/// <c>staff_services</c>.</para>
///
/// <para>Gated by the staff module entitlement plus a business-scoped permission,
/// and re-checked inside the service.</para>
/// </summary>
[ApiController]
[Route("v1/businesses/me/staff/positions")]
[Produces("application/json")]
[Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
[RequireModule("staff")]
[EnableRateLimiting("general")]
public partial class StaffPositionController : ControllerBase
{
    private readonly IStaffPositionService _positions;

    public StaffPositionController(IStaffPositionService positions)
    {
        _positions = positions;
    }

    /// <summary>All positions of the caller business, in display order.</summary>
    [HttpGet]
    [RequirePermission("staff.view")]
    [ProducesResponseType(typeof(ApiResponse<List<StaffPositionResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPositions([FromQuery] bool includeInactive = true)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _positions.GetPositionsAsync(userId.Value, GetTenantRole(), includeInactive);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>A single position of the caller business.</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission("staff.view")]
    [ProducesResponseType(typeof(ApiResponse<StaffPositionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPosition(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _positions.GetPositionAsync(userId.Value, GetTenantRole(), id);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Position assignment history. Omit <c>staffUserId</c> for the whole
    /// business; a staff caller is always narrowed to their own history.
    /// </summary>
    [HttpGet("history")]
    [RequirePermission("staff.view")]
    [ProducesResponseType(typeof(ApiResponse<List<StaffPositionHistoryResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistory([FromQuery] Guid? staffUserId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _positions.GetPositionHistoryAsync(userId.Value, GetTenantRole(), staffUserId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Creates a position. Names are unique per business (case-insensitive); a
    /// duplicate is answered with 409 POSITION_NAME_EXISTS.
    /// </summary>
    [HttpPost]
    [RequirePermission("staff.positions.manage")]
    [ProducesResponseType(typeof(ApiResponse<StaffPositionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreatePosition([FromBody] CreateStaffPositionRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _positions.CreatePositionAsync(userId.Value, GetTenantRole(), request);
        return result.Success ? Ok(result) : MapFailure(result);
    }
}

/// <summary>Position writes and staff assignment.</summary>
public partial class StaffPositionController
{
    /// <summary>Partially updates a position (rename, description, activate/deactivate).</summary>
    [HttpPatch("{id:guid}")]
    [RequirePermission("staff.positions.manage")]
    [ProducesResponseType(typeof(ApiResponse<StaffPositionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdatePosition(Guid id, [FromBody] UpdateStaffPositionRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _positions.UpdatePositionAsync(userId.Value, GetTenantRole(), id, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Rewrites the display order deterministically from the supplied id list.</summary>
    [HttpPut("order")]
    [RequirePermission("staff.positions.manage")]
    [ProducesResponseType(typeof(ApiResponse<List<StaffPositionResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReorderPositions([FromBody] ReorderStaffPositionsRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _positions.ReorderPositionsAsync(userId.Value, GetTenantRole(), request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Deletes a position nobody currently holds. A position with holders is
    /// refused (409 POSITION_IN_USE) and should be deactivated instead; history
    /// rows survive either way because they snapshot the label.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission("staff.positions.manage")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeletePosition(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _positions.DeletePositionAsync(userId.Value, GetTenantRole(), id);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>The current position of one staff member of this business.</summary>
    [HttpGet("~/v1/businesses/me/staff/{staffUserId:guid}/position")]
    [RequirePermission("staff.view")]
    [ProducesResponseType(typeof(ApiResponse<StaffPositionAssignmentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStaffPosition(Guid staffUserId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _positions.GetStaffPositionAsync(userId.Value, GetTenantRole(), staffUserId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Assigns, changes or clears a staff member position. Every real change
    /// appends a history row; re-sending the same title is an idempotent no-op.
    /// </summary>
    [HttpPut("~/v1/businesses/me/staff/{staffUserId:guid}/position")]
    [RequirePermission("staff.positions.manage")]
    [ProducesResponseType(typeof(ApiResponse<StaffPositionAssignmentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignStaffPosition(Guid staffUserId, [FromBody] AssignStaffPositionRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _positions.AssignStaffPositionAsync(userId.Value, GetTenantRole(), staffUserId, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }
}

/// <summary>Shared controller helpers.</summary>
public partial class StaffPositionController
{
    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private string GetTenantRole()
    {
        var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
                   ?? User.FindFirst("role")?.Value;

        return string.Equals(role?.Trim(), "Business", StringComparison.OrdinalIgnoreCase)
            ? "Business"
            : "Staff";
    }

    private IActionResult MapFailure<T>(ApiResponse<T> result)
        => result.Error?.Code switch
        {
            "NOT_FOUND" or "STAFF_NOT_FOUND" or "POSITION_NOT_FOUND" => NotFound(result),
            "FORBIDDEN" or "MODULE_DISABLED" => StatusCode(StatusCodes.Status403Forbidden, result),
            "POSITION_NAME_EXISTS" or "POSITION_IN_USE" or "POSITION_LIMIT_REACHED" => Conflict(result),
            _ => BadRequest(result)
        };
}
