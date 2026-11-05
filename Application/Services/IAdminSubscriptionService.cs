using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Services;

/// <summary>
/// Read-oriented aggregate views for the subscription admin module:
/// tier → businesses, business → subscription, and the audit feed.
/// All queries are efficient (AsNoTracking, GroupBy, pagination).
/// </summary>
public interface IAdminSubscriptionService
{
    /// <summary>Paginated businesses subscribed to a tier, with optional search.</summary>
    Task<ApiResponse<PaginatedResponse<AdminTierBusinessItem>>> GetTierBusinessesAsync(Guid tierId, int page, int pageSize, string? search);

    /// <summary>Aggregate business → subscription view (tier, dates, effective modules, audit).</summary>
    Task<ApiResponse<BusinessSubscriptionDetail>> GetBusinessSubscriptionAsync(Guid businessId);

    /// <summary>Paginated subscription audit feed (optionally filtered by business/tier).</summary>
    Task<ApiResponse<PaginatedResponse<SubscriptionAuditLogDto>>> GetAuditLogsAsync(int page, int pageSize, Guid? businessId, Guid? planId);

    /// <summary>
    /// Paginated list of every business subscription with the filters the admin
    /// subscription console offers (status, plan, billing interval, business,
    /// free-text search, renewal/expiry window).
    /// </summary>
    Task<ApiResponse<PaginatedResponse<AdminSubscriptionListItem>>> ListSubscriptionsAsync(
        AdminSubscriptionListQuery query, int page, int pageSize);

    /// <summary>Roll-up counters (by status) and recurring revenue for the header.</summary>
    Task<ApiResponse<AdminSubscriptionStats>> GetStatsAsync();
}

/// <summary>Filter set for <see cref="IAdminSubscriptionService.ListSubscriptionsAsync"/>.</summary>
public class AdminSubscriptionListQuery
{
    /// <summary>Free-text search across business name, plan name and plan key.</summary>
    public string? Search { get; set; }

    /// <summary>Restrict to one business.</summary>
    public Guid? BusinessId { get; set; }

    /// <summary>Subscription status (active | trial | paused | canceled | expired | past_due | deactivated).</summary>
    public string? Status { get; set; }

    /// <summary>Subscription plan key.</summary>
    public string? PlanKey { get; set; }

    /// <summary>Billing interval: monthly | yearly.</summary>
    public string? BillingInterval { get; set; }

    /// <summary>Only subscriptions renewing on or before this date.</summary>
    public DateTime? RenewingBefore { get; set; }

    /// <summary>Only subscriptions whose period ends on or before this date.</summary>
    public DateTime? ExpiringBefore { get; set; }

    /// <summary>Sort column: business | created | expiry | status.</summary>
    public string? Sort { get; set; }

    /// <summary>Sort direction: asc | desc.</summary>
    public string? Direction { get; set; }
}