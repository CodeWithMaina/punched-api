using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Platform-administration business management. Every endpoint requires the
/// <see cref="MembershipPolicies.PlatformAdmin"/> policy — a business owner or
/// staff token is refused server-side regardless of what the client renders.
///
/// <para>The legacy read-only <c>GET /v1/admin/businesses</c> and
/// <c>GET /v1/admin/businesses/{id}</c> remain in <see cref="AdminController"/>;
/// this controller adds the management surface (stats, filtered list, detail,
/// profile edit, members, audit history and status changes) without breaking
/// that contract.</para>
///
/// Base route: /v1/admin/businesses
/// </summary>
[ApiController]
[Route("v1/admin/businesses")]
[Authorize(Policy = MembershipPolicies.PlatformAdmin)]
[Produces("application/json")]
[EnableRateLimiting("general")]
public class AdminBusinessesController : ControllerBase
{
    private readonly IAdminBusinessService _businesses;
    private readonly IAdminSubscriptionService _subscriptions;
    private readonly IStorefrontCacheInvalidator _storefrontCache;

    public AdminBusinessesController(
        IAdminBusinessService businesses,
        IAdminSubscriptionService subscriptions,
        IStorefrontCacheInvalidator storefrontCache)
    {
        _businesses = businesses;
        _subscriptions = subscriptions;
        _storefrontCache = storefrontCache;
    }

    // ── Summary statistics ──────────────────────────────────

    /// <summary>Roll-up counters for the business-management dashboard header.</summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(ApiResponse<AdminBusinessStats>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats()
    {
        var result = await _businesses.GetStatsAsync();
        return Ok(result);
    }

    // ── Filtered list ───────────────────────────────────────

    /// <summary>
    /// The business-management list: server-side filtering, sorting and
    /// pagination across every registered business.
    /// </summary>
    [HttpGet("managed")]
    [ProducesResponseType(typeof(ApiResponse<PaginatedResponse<AdminBusinessListItem>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] string? planKey,
        [FromQuery] string? subscriptionStatus,
        [FromQuery] string? category,
        [FromQuery] string? moduleKey,
        [FromQuery] DateTime? registeredFrom,
        [FromQuery] DateTime? registeredTo,
        [FromQuery] DateTime? expiringBefore,
        [FromQuery] string? sort,
        [FromQuery] string? direction,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(page, 1);

        var query = new AdminBusinessListQuery
        {
            Search = search,
            Status = status,
            PlanKey = planKey,
            SubscriptionStatus = subscriptionStatus,
            Category = category,
            ModuleKey = moduleKey,
            RegisteredFrom = registeredFrom,
            RegisteredTo = registeredTo,
            ExpiringBefore = expiringBefore,
            Sort = sort,
            Direction = direction,
        };

        var result = await _businesses.ListAsync(query, page, pageSize);
        return Ok(result);
    }

    // ── Detail ──────────────────────────────────────────────

    /// <summary>Full business detail for the business-management detail page.</summary>
    [HttpGet("{businessId:guid}/profile")]
    [ProducesResponseType(typeof(ApiResponse<AdminBusinessDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail(Guid businessId)
    {
        var result = await _businesses.GetDetailAsync(businessId);
        return result.Success ? Ok(result) : NotFound(result);
    }

    // ── Profile edit ────────────────────────────────────────

    /// <summary>Edits the supported business profile fields (audited).</summary>
    [HttpPut("{businessId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AdminBusinessDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid businessId, [FromBody] AdminBusinessUpdateRequest request)
    {
        var result = await _businesses.UpdateAsync(businessId, request, CurrentUserId());
        return result.Success ? Ok(result) : StatusCode(StatusFor(result.Error?.Code), result);
    }

    // ── Members ─────────────────────────────────────────────

    /// <summary>The business's members (owner/staff/customers), paginated.</summary>
    [HttpGet("{businessId:guid}/users")]
    [ProducesResponseType(typeof(ApiResponse<PaginatedResponse<AdminBusinessUserItem>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUsers(
        Guid businessId,
        [FromQuery] string? role,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(page, 1);
        var result = await _businesses.GetUsersAsync(businessId, role, page, pageSize);
        return result.Success ? Ok(result) : NotFound(result);
    }

    // ── Audit history ───────────────────────────────────────

    /// <summary>
    /// The business's administrative audit trail (profile edits, status changes,
    /// subscription lifecycle, module overrides), newest first.
    /// </summary>
    [HttpGet("{businessId:guid}/audit")]
    [ProducesResponseType(typeof(ApiResponse<PaginatedResponse<SubscriptionAuditLogDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAudit(
        Guid businessId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(page, 1);
        var result = await _subscriptions.GetAuditLogsAsync(page, pageSize, businessId, null);
        return Ok(result);
    }

    // ── Status ──────────────────────────────────────────────

    /// <summary>Suspends the business (reversible; data retained). Requires a reason.</summary>
    [HttpPost("{businessId:guid}/suspend")]
    [ProducesResponseType(typeof(ApiResponse<AdminBusinessDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Suspend(Guid businessId, [FromBody] AdminBusinessStatusRequest request) =>
        SetStatus(businessId, "suspended", request?.Reason);

    /// <summary>Reactivates a suspended or deactivated business.</summary>
    [HttpPost("{businessId:guid}/reactivate")]
    [ProducesResponseType(typeof(ApiResponse<AdminBusinessDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Reactivate(Guid businessId, [FromBody] AdminBusinessStatusRequest? request) =>
        SetStatus(businessId, "active", request?.Reason);

    /// <summary>Deactivates the business (data retained; reversible by reactivation).</summary>
    [HttpPost("{businessId:guid}/deactivate")]
    [ProducesResponseType(typeof(ApiResponse<AdminBusinessDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Deactivate(Guid businessId, [FromBody] AdminBusinessStatusRequest? request) =>
        SetStatus(businessId, "deactivated", request?.Reason);

    private async Task<IActionResult> SetStatus(Guid businessId, string status, string? reason)
    {
        var result = await _businesses.SetStatusAsync(businessId, status, reason, CurrentUserId());
        if (!result.Success) return StatusCode(StatusFor(result.Error?.Code), result);

        // Suspending or deactivating a business withdraws its module access, so its
        // cached storefront payload is now wrong. Evict before answering.
        await _storefrontCache.EvictAsync(HttpContext.RequestAborted);
        return Ok(result);
    }

    /// <summary>Maps well-known error codes to HTTP statuses; 400 otherwise.</summary>
    private static int StatusFor(string? code) => code switch
    {
        "BUSINESS_NOT_FOUND" => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status400BadRequest,
    };

    /// <summary>The authenticated administrator's user id from the JWT, or null.</summary>
    private Guid? CurrentUserId() =>
        Guid.TryParse(User.FindFirst("userId")?.Value, out var id) ? id : null;
}
