using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Filters;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
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
    private readonly IStaffReportingService _reports;
    private readonly IStaffAuditService _audit;
    private readonly IStaffPermissionOnboardingService _onboarding;

    public StaffController(
        IStaffManagementService staff,
        IStaffReportingService reports,
        IStaffAuditService audit,
        IStaffPermissionOnboardingService onboarding)
    {
        _staff = staff;
        _reports = reports;
        _audit = audit;
        _onboarding = onboarding;
    }

    /// <summary>
    /// What staff in this business still cannot do after the new capabilities shipped,
    /// with the least-privilege recommendation per platform role. Read-only.
    /// </summary>
    [HttpGet("permissions/onboarding")]
    [RequirePermission("roles.view")]
    [ProducesResponseType(typeof(ApiResponse<StaffPermissionOnboardingResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPermissionOnboarding()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _onboarding.GetAsync(userId.Value, HttpContext.RequestAborted);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Applies only the grants the owner explicitly confirmed. An existing business is
    /// never given new staff authority without this call; the closed list of grantable
    /// codes prevents this shortcut from becoming a general permission editor.
    /// </summary>
    [HttpPost("permissions/onboarding")]
    [RequirePermission("roles.manage")]
    [ProducesResponseType(typeof(ApiResponse<StaffPermissionOnboardingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ApplyPermissionOnboarding(
        [FromBody] ApplyStaffPermissionOnboardingRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _onboarding.ApplyAsync(userId.Value, request, HttpContext.RequestAborted);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// The staff lifecycle trail: invitations, activation, profile, assignments and
    /// permission changes, newest first. Read-only — the trail has no write endpoint.
    /// </summary>
    [HttpGet("activity-log")]
    [RequirePermission("staff.view")]
    [ProducesResponseType(typeof(ApiResponse<List<StaffAuditEventResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActivityLog(
        [FromQuery] Guid? staffId = null,
        [FromQuery] int take = 100)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _audit.ListAsync(userId.Value, staffId, take, HttpContext.RequestAborted);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// The Financials tab: attributed revenue, collected money, outstanding balance
    /// and the average rate card for every staff member over the selected period.
    /// </summary>
    /// <remarks>
    /// Every figure is derived from persisted appointments, the booked service-line
    /// price snapshot and settled payments — never from the current catalogue price.
    /// The business is resolved from the caller, so a report can only describe the
    /// caller's own tenant.
    /// </remarks>
    [HttpGet("financials")]
    [RequirePermission("staff.view")]
    [RequirePermission("finances.view")]
    [ProducesResponseType(typeof(ApiResponse<StaffFinancialReportResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFinancials(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] string? range = null,
        [FromQuery] Guid? staffId = null,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDirection = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _reports.GetFinancialsAsync(userId.Value, new StaffReportQuery
        {
            From = from,
            To = to,
            Range = range,
            StaffId = staffId,
            Status = status,
            Search = search,
            SortBy = sortBy,
            SortDirection = sortDirection,
            Page = page,
            PageSize = pageSize
        }, HttpContext.RequestAborted);

        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// The Performance tab: completion, cancellation and no-show rates, repeat
    /// custom, ratings and the change against the preceding period of equal length.
    /// </summary>
    [HttpGet("performance")]
    [RequirePermission("staff.view")]
    [ProducesResponseType(typeof(ApiResponse<StaffPerformanceReportResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPerformance(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] string? range = null,
        [FromQuery] Guid? staffId = null,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDirection = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _reports.GetPerformanceAsync(userId.Value, new StaffReportQuery
        {
            From = from,
            To = to,
            Range = range,
            StaffId = staffId,
            Status = status,
            Search = search,
            SortBy = sortBy,
            SortDirection = sortDirection,
            Page = page,
            PageSize = pageSize
        }, HttpContext.RequestAborted);

        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// The lifecycle consequences of deactivating this member, resolved before the
    /// transition: future bookings, undecided payment claims, outstanding balance, an
    /// open attendance session and the colleagues qualified to take the work.
    /// </summary>
    [HttpGet("{staffId:guid}/deactivation-impact")]
    [RequirePermission("staff.view")]
    [ProducesResponseType(typeof(ApiResponse<StaffDeactivationImpactResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDeactivationImpact(Guid staffId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _staff.GetDeactivationImpactAsync(
            userId.Value, GetTenantRole(), staffId, HttpContext.RequestAborted);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Hands this member's future appointments to an eligible colleague, one batch at a
    /// time. Used before deactivating somebody and whenever a diary needs relieving;
    /// completed work is never re-attributed.
    /// </summary>
    [HttpPost("{staffId:guid}/reassign-appointments")]
    [RequirePermission("staff.manage")]
    [ProducesResponseType(typeof(ApiResponse<StaffReassignmentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReassignAppointments(
        Guid staffId, [FromBody] ReassignStaffAppointmentsRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _staff.ReassignAppointmentsAsync(
            userId.Value, GetTenantRole(), staffId, request, HttpContext.RequestAborted);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// The workforce summary behind the Staff overview: head count, appointments
    /// handled, revenue attributed and collected, outstanding balance, undecided
    /// payment claims and the quality rates — over the selected period, computed from
    /// the same definitions as the Financials and Performance tabs.
    /// </summary>
    [HttpGet("overview/summary")]
    [RequirePermission("staff.view")]
    [ProducesResponseType(typeof(ApiResponse<StaffOverviewSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetOverviewSummary(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] string? range = null)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _reports.GetOverviewSummaryAsync(userId.Value, new StaffReportQuery
        {
            From = from,
            To = to,
            Range = range
        }, HttpContext.RequestAborted);

        return result.Success ? Ok(result) : MapFailure(result);
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
