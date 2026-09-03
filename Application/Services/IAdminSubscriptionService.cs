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
}