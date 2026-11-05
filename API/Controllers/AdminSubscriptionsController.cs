using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Platform-administration subscription management. Every endpoint requires the
/// <see cref="MembershipPolicies.PlatformAdmin"/> policy.
///
/// <para>All lifecycle mutations flow through
/// <see cref="ISubscriptionLifecycleService"/>, which enforces the
/// one-subscription-per-business invariant and invalidates the entitlement cache
/// so module access reflects the new state immediately. Each action is audited.</para>
///
/// Base route: /v1/admin/subscriptions
/// </summary>
[ApiController]
[Route("v1/admin/subscriptions")]
[Authorize(Policy = MembershipPolicies.PlatformAdmin)]
[Produces("application/json")]
[EnableRateLimiting("general")]
public class AdminSubscriptionsController : ControllerBase
{
    private readonly IAdminSubscriptionService _subscriptions;
    private readonly ISubscriptionLifecycleService _lifecycle;
    private readonly ISubscriptionAuditService _audit;
    private readonly IStorefrontCacheInvalidator _storefrontCache;
    private readonly ApplicationDbContext _context;

    public AdminSubscriptionsController(
        IAdminSubscriptionService subscriptions,
        ISubscriptionLifecycleService lifecycle,
        ISubscriptionAuditService audit,
        IStorefrontCacheInvalidator storefrontCache,
        ApplicationDbContext context)
    {
        _subscriptions = subscriptions;
        _lifecycle = lifecycle;
        _audit = audit;
        _storefrontCache = storefrontCache;
        _context = context;
    }

    // ── List + stats ────────────────────────────────────────

    /// <summary>The subscription-management list with server-side filters.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PaginatedResponse<AdminSubscriptionListItem>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] Guid? businessId,
        [FromQuery] string? status,
        [FromQuery] string? planKey,
        [FromQuery] string? billingInterval,
        [FromQuery] DateTime? renewingBefore,
        [FromQuery] DateTime? expiringBefore,
        [FromQuery] string? sort,
        [FromQuery] string? direction,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(page, 1);

        var query = new AdminSubscriptionListQuery
        {
            Search = search,
            BusinessId = businessId,
            Status = status,
            PlanKey = planKey,
            BillingInterval = billingInterval,
            RenewingBefore = renewingBefore,
            ExpiringBefore = expiringBefore,
            Sort = sort,
            Direction = direction,
        };

        var result = await _subscriptions.ListSubscriptionsAsync(query, page, pageSize);
        return Ok(result);
    }

    /// <summary>Roll-up counters and recurring revenue for the header.</summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(ApiResponse<AdminSubscriptionStats>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats()
    {
        var result = await _subscriptions.GetStatsAsync();
        return Ok(result);
    }

    /// <summary>Aggregate business subscription view (plan, dates, effective modules, audit).</summary>
    [HttpGet("{businessId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<BusinessSubscriptionDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail(Guid businessId)
    {
        var result = await _subscriptions.GetBusinessSubscriptionAsync(businessId);
        return result.Success ? Ok(result) : NotFound(result);
    }

    // ── Lifecycle actions ───────────────────────────────────

    /// <summary>Pauses the subscription (access withheld; reversible). Audited.</summary>
    [HttpPost("{businessId:guid}/pause")]
    [ProducesResponseType(typeof(ApiResponse<AdminSubscriptionActionResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Pause(Guid businessId, [FromBody] AdminSubscriptionActionRequest? request)
    {
        var reason = request?.Reason;
        var rows = await _lifecycle.PauseAsync(businessId, reason, CurrentUserId());
        await AuditAsync("BUSINESS_SUBSCRIPTION_PAUSED", businessId, reason, new { rows });
        return Ok(await CompleteAsync(businessId, rows, "Subscription paused."));
    }

    /// <summary>Resumes a paused subscription (reactivates access). Audited.</summary>
    [HttpPost("{businessId:guid}/resume")]
    [ProducesResponseType(typeof(ApiResponse<AdminSubscriptionActionResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Resume(Guid businessId, [FromBody] AdminSubscriptionActionRequest? request)
    {
        var reason = request?.Reason;
        var rows = await _lifecycle.ResumeAsync(businessId, CurrentUserId());
        await AuditAsync("BUSINESS_SUBSCRIPTION_RESUMED", businessId, reason, new { rows });
        return Ok(await CompleteAsync(businessId, rows, "Subscription resumed."));
    }

    /// <summary>Extends the subscription's period end (never shortens it). Audited.</summary>
    [HttpPost("{businessId:guid}/extend")]
    [ProducesResponseType(typeof(ApiResponse<AdminSubscriptionActionResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Extend(Guid businessId, [FromBody] AdminExtendSubscriptionRequest request)
    {
        if (request == null || request.EndsAt <= DateTime.UtcNow)
            return BadRequest(ApiResponse<AdminSubscriptionActionResult>.Fail(
                "VALIDATION", "A future 'endsAt' is required."));

        var rows = await _lifecycle.ExtendAsync(businessId, request.EndsAt, CurrentUserId(), request.Reason);
        await AuditAsync("BUSINESS_SUBSCRIPTION_EXTENDED", businessId, request.Reason,
            new { endsAt = request.EndsAt, rows });
        return Ok(await CompleteAsync(businessId, rows, "Subscription extended."));
    }

    /// <summary>Cancels the subscription (CanceledAt set; reversible via reactivate). Audited.</summary>
    [HttpPost("{businessId:guid}/cancel")]
    [ProducesResponseType(typeof(ApiResponse<AdminSubscriptionActionResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid businessId, [FromBody] AdminSubscriptionActionRequest? request)
    {
        var reason = request?.Reason;
        var rows = await _lifecycle.CancelAsync(businessId, reason);
        await AuditAsync("BUSINESS_SUBSCRIPTION_CANCELED", businessId, reason, new { rows });
        return Ok(await CompleteAsync(businessId, rows, "Subscription cancelled."));
    }

    /// <summary>Reactivates an expired/cancelled/paused/deactivated subscription. Audited.</summary>
    [HttpPost("{businessId:guid}/reactivate")]
    [ProducesResponseType(typeof(ApiResponse<AdminSubscriptionActionResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reactivate(Guid businessId, [FromBody] AdminSubscriptionActionRequest? request)
    {
        var reason = request?.Reason;
        var rows = await _lifecycle.ReactivateAsync(businessId, CurrentUserId());
        await AuditAsync("BUSINESS_SUBSCRIPTION_REACTIVATED", businessId, reason, new { rows });
        return Ok(await CompleteAsync(businessId, rows, "Subscription reactivated."));
    }

    /// <summary>Deactivates the subscription (access withheld; reversible). Audited.</summary>
    [HttpPost("{businessId:guid}/deactivate")]
    [ProducesResponseType(typeof(ApiResponse<AdminSubscriptionActionResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Deactivate(Guid businessId, [FromBody] AdminSubscriptionActionRequest? request)
    {
        var reason = request?.Reason;
        var rows = await _lifecycle.DeactivateAsync(businessId, reason, CurrentUserId());
        await AuditAsync("BUSINESS_SUBSCRIPTION_DEACTIVATED", businessId, reason, new { rows });
        return Ok(await CompleteAsync(businessId, rows, "Subscription deactivated."));
    }

    /// <summary>
    /// Changes the subscription plan (covers upgrade and downgrade). The plan is
    /// resolved by its stable key. Audited.
    /// </summary>
    [HttpPost("{businessId:guid}/change-plan")]
    [ProducesResponseType(typeof(ApiResponse<AdminSubscriptionActionResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ChangePlan(Guid businessId, [FromBody] AdminAssignPlanRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.PlanKey))
            return BadRequest(ApiResponse<AdminSubscriptionActionResult>.Fail(
                "VALIDATION", "A plan key is required."));

        var planKey = request.PlanKey.Trim();
        var plan = await _context.SubscriptionPlans.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Key == planKey);
        if (plan == null)
            return BadRequest(ApiResponse<AdminSubscriptionActionResult>.Fail(
                "PLAN_NOT_FOUND", $"No plan with key '{planKey}'."));

        if (!plan.IsAssignable)
            return BadRequest(ApiResponse<AdminSubscriptionActionResult>.Fail(
                "PLAN_NOT_ASSIGNABLE", $"Plan '{planKey}' is not active and cannot be assigned."));

        await _lifecycle.ChangePlanAsync(businessId, plan.Id, CurrentUserId(), request.Reason);
        await AuditAsync("BUSINESS_SUBSCRIPTION_CHANGED", businessId, request.Reason,
            new { planKey = plan.Key, planName = plan.Name });
        return Ok(await CompleteAsync(businessId, 1, $"Subscription changed to '{plan.Name}'."));
    }

    // ── Helpers ─────────────────────────────────────────────

    private async Task<AdminSubscriptionActionResult> CompleteAsync(
        Guid businessId, int rows, string message)
    {
        // Every lifecycle action can change this business's effective modules, so
        // drop its cached storefront payload before answering.
        await _storefrontCache.EvictAsync(HttpContext.RequestAborted);

        var current = await _context.BusinessSubscriptions.AsNoTracking()
            .Where(s => s.BusinessId == businessId)
            .Select(s => new { s.Status, s.EndsAt })
            .FirstOrDefaultAsync();

        return new AdminSubscriptionActionResult
        {
            BusinessId = businessId,
            AffectedRows = rows,
            Status = current?.Status,
            EndsAt = current?.EndsAt,
            Message = message,
        };
    }

    private Task AuditAsync(string action, Guid businessId, string? reason, object payload) =>
        _audit.RecordAsync(
            action, CurrentUserId(), targetBusinessId: businessId, targetPlanId: null,
            payloadJson: System.Text.Json.JsonSerializer.Serialize(payload),
            reason: reason ?? "Platform administrator subscription action.");

    /// <summary>The authenticated administrator's user id from the JWT, or null.</summary>
    private Guid? CurrentUserId() =>
        Guid.TryParse(User.FindFirst("userId")?.Value, out var id) ? id : null;
}
