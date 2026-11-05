using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Services;

/// <summary>
/// Platform-administration business management. Every method is scoped to a
/// single business (or an explicit filter set) and returns
/// <see cref="ApiResponse{T}"/> rather than throwing for expected domain
/// failures. All mutations are audited and invalidate the business's
/// entitlement cache where module access could change.
/// </summary>
public interface IAdminBusinessService
{
    /// <summary>Filter set for the admin business list (all optional).</summary>
    Task<ApiResponse<PaginatedResponse<AdminBusinessListItem>>> ListAsync(
        AdminBusinessListQuery query, int page, int pageSize);

    /// <summary>Roll-up counters for the business-management header.</summary>
    Task<ApiResponse<AdminBusinessStats>> GetStatsAsync();

    /// <summary>Full business detail (profile, owner, subscription, counters).</summary>
    Task<ApiResponse<AdminBusinessDetailResponse>> GetDetailAsync(Guid businessId);

    /// <summary>Edits the supported business profile fields. Audited.</summary>
    Task<ApiResponse<AdminBusinessDetailResponse>> UpdateAsync(
        Guid businessId, AdminBusinessUpdateRequest request, Guid? actorUserId);

    /// <summary>The business's members (owner/staff/customers), paginated.</summary>
    Task<ApiResponse<PaginatedResponse<AdminBusinessUserItem>>> GetUsersAsync(
        Guid businessId, string? role, int page, int pageSize);

    /// <summary>
    /// Sets the business's platform-administration status
    /// (<c>active</c> | <c>suspended</c> | <c>deactivated</c>). Audited.
    /// </summary>
    Task<ApiResponse<AdminBusinessDetailResponse>> SetStatusAsync(
        Guid businessId, string status, string? reason, Guid? actorUserId);
}

/// <summary>Filter set for <see cref="IAdminBusinessService.ListAsync"/>.</summary>
public class AdminBusinessListQuery
{
    /// <summary>Free-text search across name, slug, owner name/email and phone.</summary>
    public string? Search { get; set; }

    /// <summary>Platform status: active | suspended | deactivated.</summary>
    public string? Status { get; set; }

    /// <summary>Subscription plan key (e.g. "growth").</summary>
    public string? PlanKey { get; set; }

    /// <summary>Subscription status: active | trial | paused | canceled | expired | past_due | deactivated.</summary>
    public string? SubscriptionStatus { get; set; }

    /// <summary>Business category.</summary>
    public string? Category { get; set; }

    /// <summary>Only businesses with an effective entitlement to this module key.</summary>
    public string? ModuleKey { get; set; }

    /// <summary>Inclusive lower bound on the registration date (UTC).</summary>
    public DateTime? RegisteredFrom { get; set; }

    /// <summary>Inclusive upper bound on the registration date (UTC).</summary>
    public DateTime? RegisteredTo { get; set; }

    /// <summary>Only businesses whose active subscription ends on or before this date.</summary>
    public DateTime? ExpiringBefore { get; set; }

    /// <summary>Sort column: name | created | status | expiry.</summary>
    public string? Sort { get; set; }

    /// <summary>Sort direction: asc | desc.</summary>
    public string? Direction { get; set; }
}
