using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Admin subscription-tier management: CRUD, lifecycle transitions, tier
/// module sets, tier → businesses, business → subscription aggregate, and
/// the subscription audit feed. All endpoints require Admin role. Business
/// logic lives in <see cref="IAdminTierService"/> / <see cref="IAdminSubscriptionService"/>.
/// Base route: /v1/admin
/// </summary>
[ApiController]
[Route("v1/admin")]
[Authorize(Roles = "Admin")]
[Produces("application/json")]
[EnableRateLimiting("general")]
public class AdminSubscriptionTiersController : ControllerBase
{
    private readonly IAdminTierService _tierService;
    private readonly IAdminSubscriptionService _subscriptionService;

    public AdminSubscriptionTiersController(
        IAdminTierService tierService,
        IAdminSubscriptionService subscriptionService)
    {
        _tierService = tierService;
        _subscriptionService = subscriptionService;
    }

    // ── Tier CRUD ───────────────────────────────────────────

    /// <summary>Lists all subscription tiers with module/business counts.</summary>
    [HttpGet("subscription-tiers")]
    [ProducesResponseType(typeof(ApiResponse<List<AdminTierSummary>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListTiers()
    {
        var result = await _tierService.ListAsync();
        return Ok(result);
    }

    /// <summary>KPI summary for the tier list header.</summary>
    [HttpGet("subscription-tiers/summary")]
    [ProducesResponseType(typeof(ApiResponse<AdminSubscriptionSummary>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary()
    {
        var result = await _tierService.GetSummaryAsync();
        return Ok(result);
    }

    /// <summary>Single tier detail including its module set.</summary>
    [HttpGet("subscription-tiers/{tierId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AdminTierDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTier(Guid tierId)
    {
        var result = await _tierService.GetAsync(tierId);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>Creates a new Draft tier.</summary>
    [HttpPost("subscription-tiers")]
    [ProducesResponseType(typeof(ApiResponse<AdminTierDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateTier([FromBody] AdminTierCreateRequest request)
    {
        var result = await _tierService.CreateAsync(request, CurrentUserId());
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Edits a Draft/Inactive tier's metadata (key immutable after publish).</summary>
    [HttpPut("subscription-tiers/{tierId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AdminTierDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateTier(Guid tierId, [FromBody] AdminTierUpdateRequest request)
    {
        var result = await _tierService.UpdateAsync(tierId, request, CurrentUserId());
        return result.Success ? Ok(result) : StatusCode(FeatureErrorStatus(result.Error), result);
    }

    /// <summary>DELETE semantics — archives the tier (never a physical delete).</summary>
    [HttpDelete("subscription-tiers/{tierId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<MessageResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTier(Guid tierId)
    {
        var result = await _tierService.DeleteAsync(tierId, CurrentUserId());
        return result.Success ? Ok(result) : StatusCode(FeatureErrorStatus(result.Error), result);
    }

    // ── Lifecycle ───────────────────────────────────────────

    /// <summary>Publishes a Draft/Inactive tier (requires a core module + valid dependencies).</summary>
    [HttpPost("subscription-tiers/{tierId:guid}/publish")]
    [ProducesResponseType(typeof(ApiResponse<AdminTierDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PublishTier(Guid tierId)
    {
        var result = await _tierService.PublishAsync(tierId, CurrentUserId());
        return result.Success ? Ok(result) : StatusCode(FeatureErrorStatus(result.Error), result);
    }

    /// <summary>Deactivates a Draft/Active tier.</summary>
    [HttpPost("subscription-tiers/{tierId:guid}/deactivate")]
    [ProducesResponseType(typeof(ApiResponse<AdminTierDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateTier(Guid tierId)
    {
        var result = await _tierService.DeactivateAsync(tierId, CurrentUserId());
        return result.Success ? Ok(result) : StatusCode(FeatureErrorStatus(result.Error), result);
    }

    /// <summary>Archives a Draft/Inactive tier (terminal; rejects active subscribers).</summary>
    [HttpPost("subscription-tiers/{tierId:guid}/archive")]
    [ProducesResponseType(typeof(ApiResponse<AdminTierDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ArchiveTier(Guid tierId)
    {
        var result = await _tierService.ArchiveAsync(tierId, CurrentUserId());
        return result.Success ? Ok(result) : StatusCode(FeatureErrorStatus(result.Error), result);
    }

    // ── Tier modules / catalog ──────────────────────────────

    /// <summary>The tier's full module view (catalog + membership flags).</summary>
    [HttpGet("subscription-tiers/{tierId:guid}/modules")]
    [ProducesResponseType(typeof(ApiResponse<AdminTierModulesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTierModules(Guid tierId)
    {
        var result = await _tierService.GetModulesAsync(tierId);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>Transactionally replaces the tier's module set (Draft/Inactive only).</summary>
    [HttpPut("subscription-tiers/{tierId:guid}/modules")]
    [ProducesResponseType(typeof(ApiResponse<AdminTierModulesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetTierModules(Guid tierId, [FromBody] AdminTierModulesRequest request)
    {
        var result = await _tierService.SetModulesAsync(tierId, request, CurrentUserId());
        return result.Success ? Ok(result) : StatusCode(FeatureErrorStatus(result.Error), result);
    }

    /// <summary>The admin module catalog (runtime manifest enriched with DB metadata).</summary>
    [HttpGet("modules")]
    [ProducesResponseType(typeof(ApiResponse<AdminModulesCatalogResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetModuleCatalog()
    {
        var result = await _tierService.GetCatalogModulesAsync();
        return Ok(result);
    }

    // ── Tier → businesses ───────────────────────────────────

    /// <summary>Paginated businesses subscribed to a tier.</summary>
    [HttpGet("subscription-tiers/{tierId:guid}/businesses")]
    [ProducesResponseType(typeof(ApiResponse<PaginatedResponse<AdminTierBusinessItem>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTierBusinesses(
        Guid tierId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null)
    {
        var result = await _subscriptionService.GetTierBusinessesAsync(tierId, page, pageSize, search);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>Aggregate business subscription view (tier, dates, effective modules, audit).</summary>
    [HttpGet("businesses/{businessId:guid}/subscription")]
    [ProducesResponseType(typeof(ApiResponse<BusinessSubscriptionDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBusinessSubscription(Guid businessId)
    {
        var result = await _subscriptionService.GetBusinessSubscriptionAsync(businessId);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>Paginated subscription audit feed (optionally filtered by business/tier).</summary>
    [HttpGet("subscription-audit-logs")]
    [ProducesResponseType(typeof(ApiResponse<PaginatedResponse<SubscriptionAuditLogDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuditLogs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] Guid? businessId = null,
        [FromQuery] Guid? tierId = null)
    {
        var result = await _subscriptionService.GetAuditLogsAsync(page, pageSize, businessId, tierId);
        return Ok(result);
    }

    /// <summary>Maps well-known feature error codes to HTTP statuses; 400 otherwise.</summary>
    private static int FeatureErrorStatus(ApiError? error) =>
        error?.Code == "TIER_NOT_FOUND" ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest;

    /// <summary>The authenticated admin's user id from the JWT, or null.</summary>
    private Guid? CurrentUserId() =>
        Guid.TryParse(User.FindFirst("userId")?.Value, out var id) ? id : null;
}
