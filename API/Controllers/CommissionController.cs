using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Filters;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Commission management — configuration, the ledger, and the lifecycle.
/// Base route: /v1/commissions
///
/// <para>Every action is gated twice: by the module entitlement
/// (<c>[RequireModule("commissions")]</c>) and by a business-scoped permission
/// (<see cref="RequirePermissionAttribute"/>). The service re-checks the
/// permission internally as well, because commission operations move money.</para>
///
/// <para>Staff callers reach the read endpoints only, and the service
/// hard-scopes them to their own entries. Owners and managers with the
/// <c>commissions.payout</c> permission are the only callers who can record a
/// payout.</para>
/// </summary>
[ApiController]
[Route("v1/commissions")]
[Produces("application/json")]
[Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
[RequireModule("commissions")]
[EnableRateLimiting("general")]
public partial class CommissionController : ControllerBase
{
    private readonly ICommissionService _commissions;

    public CommissionController(ICommissionService commissions)
    {
        _commissions = commissions;
    }

    // ═══════════════════════════════════════════════════════════
    //  CONFIGURATION
    // ═══════════════════════════════════════════════════════════

    /// <summary>The business's commission configuration.</summary>
    [HttpGet("config")]
    [RequirePermission("commissions.view")]
    [ProducesResponseType(typeof(ApiResponse<CommissionConfigResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConfig()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _commissions.GetConfigAsync(userId.Value, GetTenantRole());
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Creates or replaces the business's commission configuration.</summary>
    [HttpPut("config")]
    [RequirePermission("commissions.manage")]
    [ProducesResponseType(typeof(ApiResponse<CommissionConfigResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateConfig([FromBody] UpdateCommissionConfigRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _commissions.UpdateConfigAsync(userId.Value, GetTenantRole(), request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Scoped overrides (staff / service / staff+service).</summary>
    [HttpGet("rates")]
    [RequirePermission("commissions.view")]
    [ProducesResponseType(typeof(ApiResponse<List<CommissionRateResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRates()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _commissions.GetRatesAsync(userId.Value, GetTenantRole());
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Creates or updates the override for one scope. Conflicting scopes are rejected.</summary>
    [HttpPut("rates")]
    [RequirePermission("commissions.manage")]
    [ProducesResponseType(typeof(ApiResponse<CommissionRateResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpsertRate([FromBody] UpsertCommissionRateRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _commissions.UpsertRateAsync(userId.Value, GetTenantRole(), request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Removes an override. Already-accrued entries keep their original calculation.</summary>
    [HttpDelete("rates/{rateId:guid}")]
    [RequirePermission("commissions.manage")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteRate(Guid rateId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _commissions.DeleteRateAsync(userId.Value, GetTenantRole(), rateId);
        return result.Success ? Ok(result) : MapFailure(result);
    }
}

// ===============================================================
//  COMMISSION LEDGER + LIFECYCLE
//  Split into a partial so the configuration surface above stays readable.
// ===============================================================
public partial class CommissionController
{
    /// <summary>
    /// The commission ledger with DB-first filtering (staff, service,
    /// appointment, customer, status, entry type, date range), sorting and
    /// pagination. A staff caller only ever sees their own entries.
    /// </summary>
    [HttpGet("entries")]
    [RequirePermission("commissions.view")]
    [ProducesResponseType(typeof(ApiResponse<PaginatedResponse<CommissionEntryResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEntries(
        [FromQuery] Guid? staffUserId,
        [FromQuery] Guid? serviceId,
        [FromQuery] Guid? appointmentId,
        [FromQuery] Guid? customerId,
        [FromQuery] string? status,
        [FromQuery] string? entryType,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDirection,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _commissions.GetEntriesAsync(userId.Value, GetTenantRole(), new CommissionEntriesQueryRequest
        {
            StaffUserId = staffUserId,
            ServiceId = serviceId,
            AppointmentId = appointmentId,
            CustomerId = customerId,
            Status = status,
            EntryType = entryType,
            From = from,
            To = to,
            SortBy = sortBy,
            SortDirection = sortDirection,
            Page = page,
            PageSize = pageSize
        });

        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Balances for every staff member of the business. Staff see only their own.</summary>
    [HttpGet("balances")]
    [RequirePermission("commissions.view")]
    [ProducesResponseType(typeof(ApiResponse<List<CommissionStaffBalanceResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBalances()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _commissions.GetBalancesAsync(userId.Value, GetTenantRole());
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>One staff member's balance. Staff may only request their own.</summary>
    [HttpGet("balances/{staffUserId:guid}")]
    [RequirePermission("commissions.view")]
    [ProducesResponseType(typeof(ApiResponse<CommissionStaffBalanceResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStaffBalance(Guid staffUserId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _commissions.GetStaffBalanceAsync(userId.Value, GetTenantRole(), staffUserId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Business-level commission report for a period.</summary>
    [HttpGet("report")]
    [RequirePermission("commissions.view")]
    [ProducesResponseType(typeof(ApiResponse<CommissionReportResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReport([FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _commissions.GetReportAsync(userId.Value, GetTenantRole(), from, to);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Recorded payout batches.</summary>
    [HttpGet("payouts")]
    [RequirePermission("commissions.view")]
    [ProducesResponseType(typeof(ApiResponse<List<CommissionPayoutResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPayouts([FromQuery] Guid? staffUserId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _commissions.GetPayoutsAsync(userId.Value, GetTenantRole(), staffUserId);
        return result.Success ? Ok(result) : MapFailure(result);
    }
}

// ===============================================================
//  COMMISSION LIFECYCLE ACTIONS
// ===============================================================
public partial class CommissionController
{
    /// <summary>Releases settled commissions (the approval step when the business requires one).</summary>
    [HttpPost("entries/approve")]
    [RequirePermission("commissions.approve")]
    [ProducesResponseType(typeof(ApiResponse<List<CommissionEntryResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Approve([FromBody] ApproveCommissionEntriesRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _commissions.ApproveAsync(userId.Value, GetTenantRole(), request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Records that payable commission was paid to a staff member, against a real
    /// payment reference. This is attribution, not a money transfer: the platform
    /// has no staff payout provider.
    /// </summary>
    [HttpPost("payouts")]
    [RequirePermission("commissions.payout")]
    [ProducesResponseType(typeof(ApiResponse<CommissionPayoutResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RecordPayout([FromBody] RecordCommissionPayoutRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _commissions.RecordPayoutAsync(userId.Value, GetTenantRole(), request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Appends an audited adjustment. A reason is mandatory.</summary>
    [HttpPost("adjustments")]
    [RequirePermission("commissions.manage")]
    [ProducesResponseType(typeof(ApiResponse<CommissionEntryResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Adjust([FromBody] CommissionAdjustmentRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _commissions.AdjustAsync(userId.Value, GetTenantRole(), request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Reverses one ledger entry with a mandatory reason.</summary>
    [HttpPost("entries/{entryId:guid}/reverse")]
    [RequirePermission("commissions.manage")]
    [ProducesResponseType(typeof(ApiResponse<CommissionEntryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reverse(Guid entryId, [FromBody] CommissionReversalRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _commissions.ReverseAsync(userId.Value, GetTenantRole(), entryId, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Raises a dispute against an entry, holding it out of the payable balance.</summary>
    [HttpPost("entries/{entryId:guid}/dispute")]
    [RequirePermission("commissions.manage")]
    [ProducesResponseType(typeof(ApiResponse<CommissionEntryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Dispute(Guid entryId, [FromBody] CommissionDisputeRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _commissions.DisputeAsync(userId.Value, GetTenantRole(), entryId, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Resolves a dispute: release the commission or void it.</summary>
    [HttpPost("entries/{entryId:guid}/dispute/resolve")]
    [RequirePermission("commissions.approve")]
    [ProducesResponseType(typeof(ApiResponse<CommissionEntryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResolveDispute(Guid entryId, [FromBody] ResolveCommissionDisputeRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _commissions.ResolveDisputeAsync(userId.Value, GetTenantRole(), entryId, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }
}

// ===============================================================
//  SHARED HELPERS
// ===============================================================
public partial class CommissionController
{
    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    /// <summary>
    /// The caller tenant persona for endpoints that admit both owners and staff.
    /// The service then applies the persona own scoping, so a staff caller stays
    /// restricted to their own ledger. An absent claim is treated as the more
    /// restrictive Staff persona.
    /// </summary>
    private string GetTenantRole()
    {
        var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
                   ?? User.FindFirst("role")?.Value;

        return string.Equals(role?.Trim(), "Business", StringComparison.OrdinalIgnoreCase)
            ? "Business"
            : "Staff";
    }

    /// <summary>
    /// Maps an ApiResponse failure onto the HTTP status the rest of the API uses,
    /// so the frontend can treat commission errors exactly like every other
    /// module error.
    /// </summary>
    private IActionResult MapFailure<T>(ApiResponse<T> result)
        => result.Error?.Code switch
        {
            "NOT_FOUND" or "STAFF_NOT_FOUND" or "SERVICE_NOT_FOUND" => NotFound(result),
            "FORBIDDEN" => StatusCode(StatusCodes.Status403Forbidden, result),
            "PAYOUT_REFERENCE_EXISTS" or "INVALID_STATUS_TRANSITION"
                or "CONCURRENT_MODIFICATION" or "RESCHEDULE_EXPIRED" => Conflict(result),
            _ => BadRequest(result)
        };
}
