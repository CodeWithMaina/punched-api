using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Filters;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Staff management: one member's profile, services, working week, time off and
/// active/suspended state. Base route: /v1/businesses/me/staff
///
/// <para>Reads live alongside the staff list in <c>BusinessController.Staff.cs</c>;
/// this controller owns the per-member management surface. The business is always
/// resolved server-side from the caller's active membership, so a staff id from
/// another business resolves to 404 rather than being acted on.</para>
///
/// <para>Gated by the <c>staff</c> module entitlement plus a business-scoped
/// permission, and re-checked inside the service — the attribute is the fast
/// rejection, the service is the authority.</para>
/// </summary>
[ApiController]
[Route("v1/businesses/me/staff")]
[Produces("application/json")]
[Authorize(Policy = MembershipPolicies.Owner)]
[RequireModule("staff")]
[EnableRateLimiting("general")]
public partial class StaffController : ControllerBase
{
    private readonly IStaffManagementService _staff;

    public StaffController(IStaffManagementService staff)
    {
        _staff = staff;
    }

    /// <summary>One staff member's full management profile.</summary>
    [HttpGet("{staffId:guid}")]
    [RequirePermission("staff.view")]
    [ProducesResponseType(typeof(ApiResponse<StaffDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStaff(Guid staffId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _staff.GetStaffDetailAsync(userId.Value, GetTenantRole(), staffId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Partially updates a staff member (name, phone, bio, storefront flags,
    /// position, goals, photo). An omitted field is never touched; email is not
    /// editable here because it is the sign-in identity.
    /// </summary>
    [HttpPatch("{staffId:guid}")]
    [RequirePermission("staff.manage")]
    [ProducesResponseType(typeof(ApiResponse<StaffDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStaff(Guid staffId, [FromBody] UpdateStaffRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _staff.UpdateStaffAsync(userId.Value, GetTenantRole(), staffId, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Reactivates a suspended member. Nothing is recreated: the same membership,
    /// assignments and history are used again.
    /// </summary>
    [HttpPost("{staffId:guid}/activate")]
    [RequirePermission("staff.manage")]
    [ProducesResponseType(typeof(ApiResponse<StaffStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActivateStaff(Guid staffId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _staff.SetStaffActiveAsync(userId.Value, GetTenantRole(), staffId, active: true);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Suspends a member: they stop being offered for new bookings and new
    /// attendance, while every historical record stays readable. There is no
    /// delete — the row is the audit trail.
    /// </summary>
    [HttpPost("{staffId:guid}/deactivate")]
    [RequirePermission("staff.manage")]
    [ProducesResponseType(typeof(ApiResponse<StaffStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateStaff(Guid staffId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _staff.SetStaffActiveAsync(userId.Value, GetTenantRole(), staffId, active: false);
        return result.Success ? Ok(result) : MapFailure(result);
    }
}

/// <summary>Service assignments, working week and time off.</summary>
public partial class StaffController
{
    /// <summary>The services a staff member is assigned to deliver.</summary>
    [HttpGet("{staffId:guid}/services")]
    [RequirePermission("staff.view")]
    [ProducesResponseType(typeof(ApiResponse<List<StaffServiceLinkResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStaffServices(Guid staffId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _staff.GetStaffServicesAsync(userId.Value, GetTenantRole(), staffId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Replaces the set of services a staff member delivers. The array is the whole
    /// set, so adding and removing are one atomic write.
    /// </summary>
    [HttpPut("{staffId:guid}/services")]
    [RequirePermission("staff.manage")]
    [ProducesResponseType(typeof(ApiResponse<List<StaffServiceLinkResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetStaffServices(Guid staffId, [FromBody] SetStaffServicesRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _staff.SetStaffServicesAsync(userId.Value, GetTenantRole(), staffId, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>The recurring working week (always seven weekdays).</summary>
    [HttpGet("{staffId:guid}/availability")]
    [RequirePermission("staff.view")]
    [ProducesResponseType(typeof(ApiResponse<List<StaffAvailabilityDayResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStaffAvailability(Guid staffId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _staff.GetStaffAvailabilityAsync(userId.Value, GetTenantRole(), staffId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Saves the recurring working week. What is saved here is what the booking
    /// engine offers customers, breaks included.
    /// </summary>
    [HttpPut("{staffId:guid}/availability")]
    [RequirePermission("staff.manage")]
    [ProducesResponseType(typeof(ApiResponse<List<StaffAvailabilityDayResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetStaffAvailability(Guid staffId, [FromBody] SetStaffAvailabilityRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _staff.SetStaffAvailabilityAsync(userId.Value, GetTenantRole(), staffId, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Clears the recurring working week. The member's hours then follow their dated
    /// roster, and failing that the business's own booking window — the state every
    /// member starts in.
    /// </summary>
    [HttpDelete("{staffId:guid}/availability")]
    [RequirePermission("staff.manage")]
    [ProducesResponseType(typeof(ApiResponse<List<StaffAvailabilityDayResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ClearStaffAvailability(Guid staffId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _staff.ClearStaffAvailabilityAsync(userId.Value, GetTenantRole(), staffId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Adds dated time off, which overrides the recurring week for those dates.</summary>
    [HttpPost("{staffId:guid}/time-off")]
    [RequirePermission("staff.manage")]
    [ProducesResponseType(typeof(ApiResponse<List<StaffTimeOffResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AddStaffTimeOff(Guid staffId, [FromBody] AddStaffTimeOffRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _staff.AddStaffTimeOffAsync(userId.Value, GetTenantRole(), staffId, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Removes a dated override, returning the day to the recurring week.</summary>
    [HttpDelete("{staffId:guid}/time-off/{date}")]
    [RequirePermission("staff.manage")]
    [ProducesResponseType(typeof(ApiResponse<List<StaffTimeOffResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveStaffTimeOff(Guid staffId, string date)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (!DateOnly.TryParse(date, out var parsed))
            return BadRequest(ApiResponse<List<StaffTimeOffResponse>>.Fail("VALIDATION_ERROR", "Use an ISO date (yyyy-MM-dd)."));

        var result = await _staff.RemoveStaffTimeOffAsync(userId.Value, GetTenantRole(), staffId, parsed);
        return result.Success ? Ok(result) : MapFailure(result);
    }
}

/// <summary>Shared controller helpers.</summary>
public partial class StaffController
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
            "NOT_FOUND" or "STAFF_NOT_FOUND" => NotFound(result),
            "FORBIDDEN" or "MODULE_DISABLED" => StatusCode(StatusCodes.Status403Forbidden, result),
            "CONFLICT" => Conflict(result),
            _ => BadRequest(result)
        };
}
