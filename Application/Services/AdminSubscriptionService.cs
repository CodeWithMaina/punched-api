using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// <see cref="IAdminSubscriptionService"/> implementation. Read-oriented
/// aggregate views for the subscription admin module.
/// </summary>
public class AdminSubscriptionService : IAdminSubscriptionService
{
    private const string ErrTierNotFound = "TIER_NOT_FOUND";
    private const string ErrBusinessNotFound = "BUSINESS_NOT_FOUND";
    private const string ErrBusinessNotFoundMessage = "No business exists with the given id.";

    private readonly ApplicationDbContext _context;
    private readonly IModuleEntitlementService _entitlements;
    private readonly ILogger<AdminSubscriptionService> _logger;

    public AdminSubscriptionService(
        ApplicationDbContext context,
        IModuleEntitlementService entitlements,
        ILogger<AdminSubscriptionService> logger)
    {
        _context = context;
        _entitlements = entitlements;
        _logger = logger;
    }

    public async Task<ApiResponse<PaginatedResponse<AdminTierBusinessItem>>> GetTierBusinessesAsync(
        Guid tierId, int page, int pageSize, string? search)
    {
        try
        {
            var tierExists = await _context.SubscriptionPlans.AsNoTracking().AnyAsync(p => p.Id == tierId);
            if (!tierExists)
                return ApiResponse<PaginatedResponse<AdminTierBusinessItem>>.Fail(ErrTierNotFound,
                    "No subscription tier exists with the given id.");

            // One query resolves the tier's module count (shared by all its businesses).
            var tierModuleCount = await _context.PlanModules.AsNoTracking().CountAsync(pm => pm.PlanId == tierId);

            var query = _context.BusinessSubscriptions
                .AsNoTracking()
                .Where(s => s.PlanId == tierId)
                .Join(
                    _context.Businesses.AsNoTracking(),
                    s => s.BusinessId,
                    b => b.Id,
                    (s, b) => new { s, b });

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(x => x.b.Name.Contains(term) || (x.b.Category != null && x.b.Category.Contains(term)));
            }

            var totalCount = await query.CountAsync();
            var items = await query
                .OrderBy(x => x.b.Name)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new AdminTierBusinessItem
                {
                    Id = x.b.Id,
                    Name = x.b.Name,
                    Category = x.b.Category,
                    Location = x.b.Location,
                    Status = x.s.Status,
                    StartsAt = x.s.StartsAt,
                    EndsAt = x.s.EndsAt,
                    ModuleCount = tierModuleCount
                })
                .ToListAsync();

            return ApiResponse<PaginatedResponse<AdminTierBusinessItem>>.Ok(new PaginatedResponse<AdminTierBusinessItem>
            {
                Items = items,
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list businesses for tier {TierId}.", tierId);
            return ApiResponse<PaginatedResponse<AdminTierBusinessItem>>.Fail("LIST_FAILED", "Failed to list businesses on the tier.");
        }
    }
public async Task<ApiResponse<BusinessSubscriptionDetail>> GetBusinessSubscriptionAsync(Guid businessId)
    {
        try
        {
            var business = await _context.Businesses.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == businessId);
            if (business == null)
                return ApiResponse<BusinessSubscriptionDetail>.Fail(ErrBusinessNotFound, ErrBusinessNotFoundMessage);

            var subscription = await _context.BusinessSubscriptions.AsNoTracking()
                .Include(s => s.Plan)
                .FirstOrDefaultAsync(s => s.BusinessId == businessId);

            var entitlements = await _entitlements.GetBusinessModulesAsync(businessId);

            var history = await _context.SubscriptionAuditLogs.AsNoTracking()
                .Where(a => a.TargetBusinessId == businessId)
                .OrderByDescending(a => a.CreatedAt)
                .Take(25)
                .Select(a => ToDto(a))
                .ToListAsync();

            var detail = new BusinessSubscriptionDetail
            {
                BusinessId = business.Id,
                BusinessName = business.Name,
                SubscriptionStatus = subscription?.Status,
                StartsAt = subscription?.StartsAt,
                EndsAt = subscription?.EndsAt,
                EffectiveModules = entitlements.Modules.Select(m => new BusinessEffectiveModule
                {
                    Key = m.Key,
                    Name = m.Name,
                    Description = m.Description,
                    IsCore = m.IsCore,
                    IsEnabled = m.IsEnabled,
                    HasAccess = m.HasAccess,
                    Source = m.Source,
                    Reason = m.Reason,
                    Dependencies = m.Dependencies
                }).ToList(),
                AuditHistory = history
            };

            if (subscription?.Plan != null)
            {
                detail.CurrentTier = new AdminTierSummary
                {
                    Id = subscription.Plan.Id,
                    Key = subscription.Plan.Key,
                    Name = subscription.Plan.Name,
                    Description = subscription.Plan.Description,
                    Price = subscription.Plan.Price,
                    BillingInterval = subscription.Plan.BillingInterval,
                    Lifecycle = subscription.Plan.LifecycleState.ToString(),
                    IsDefault = subscription.Plan.IsDefault
                };
            }

            return ApiResponse<BusinessSubscriptionDetail>.Ok(detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load subscription detail for business {BusinessId}.", businessId);
            return ApiResponse<BusinessSubscriptionDetail>.Fail("GET_FAILED", "Failed to load the business subscription detail.");
        }
    }

    public async Task<ApiResponse<PaginatedResponse<SubscriptionAuditLogDto>>> GetAuditLogsAsync(
        int page, int pageSize, Guid? businessId, Guid? planId)
    {
        try
        {
            var query = _context.SubscriptionAuditLogs.AsNoTracking();
            if (businessId.HasValue)
                query = query.Where(a => a.TargetBusinessId == businessId.Value);
            if (planId.HasValue)
                query = query.Where(a => a.TargetPlanId == planId.Value);

            var totalCount = await query.CountAsync();
            var items = await query
                .OrderByDescending(a => a.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(a => ToDto(a))
                .ToListAsync();

            return ApiResponse<PaginatedResponse<SubscriptionAuditLogDto>>.Ok(new PaginatedResponse<SubscriptionAuditLogDto>
            {
                Items = items,
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list subscription audit logs.");
            return ApiResponse<PaginatedResponse<SubscriptionAuditLogDto>>.Fail("LIST_FAILED", "Failed to list subscription audit logs.");
        }
    }

    private static SubscriptionAuditLogDto ToDto(SubscriptionAuditLog a)
    {
        object? payload = TryParsePayload(a.PayloadJson);
        return new SubscriptionAuditLogDto
        {
            Id = a.Id,
            Action = a.Action,
            ActorUserId = a.ActorUserId,
            TargetBusinessId = a.TargetBusinessId,
            TargetPlanId = a.TargetPlanId,
            Payload = payload,
            Reason = a.Reason,
            CreatedAt = a.CreatedAt
        };
    }

    private static object? TryParsePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<object>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
    // ═════════════════════════════════════════════════════════
    //  SUBSCRIPTION LIST + STATS
    // ═════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<ApiResponse<PaginatedResponse<AdminSubscriptionListItem>>> ListSubscriptionsAsync(
        AdminSubscriptionListQuery query, int page, int pageSize)
    {
        try
        {
            var q = _context.BusinessSubscriptions
                .AsNoTracking()
                .Join(_context.Businesses.AsNoTracking(),
                    s => s.BusinessId, b => b.Id, (s, b) => new { s, b })
                .Join(_context.SubscriptionPlans.AsNoTracking(),
                    x => x.s.PlanId, p => p.Id, (x, p) => new { x.s, x.b, p });

            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var pattern = $"%{query.Search.Trim()}%";
                q = q.Where(x =>
                    EF.Functions.ILike(x.b.Name, pattern) ||
                    EF.Functions.ILike(x.p.Name, pattern) ||
                    EF.Functions.ILike(x.p.Key, pattern));
            }

            if (query.BusinessId.HasValue)
                q = q.Where(x => x.b.Id == query.BusinessId.Value);

            if (!string.IsNullOrWhiteSpace(query.Status))
                q = q.Where(x => x.s.Status == query.Status);

            if (!string.IsNullOrWhiteSpace(query.PlanKey))
                q = q.Where(x => x.p.Key == query.PlanKey);

            if (!string.IsNullOrWhiteSpace(query.BillingInterval))
                q = q.Where(x => x.p.BillingInterval == query.BillingInterval);

            if (query.RenewingBefore.HasValue)
                q = q.Where(x => x.s.EndsAt != null && x.s.EndsAt <= query.RenewingBefore.Value);

            if (query.ExpiringBefore.HasValue)
                q = q.Where(x => x.s.EndsAt != null && x.s.EndsAt <= query.ExpiringBefore.Value);

            var totalCount = await q.CountAsync();

            var ascending = string.Equals(query.Direction, "asc", StringComparison.OrdinalIgnoreCase);
            q = (query.Sort ?? "created").ToLowerInvariant() switch
            {
                "business" => ascending ? q.OrderBy(x => x.b.Name) : q.OrderByDescending(x => x.b.Name),
                "status" => ascending ? q.OrderBy(x => x.s.Status) : q.OrderByDescending(x => x.s.Status),
                "expiry" => ascending ? q.OrderBy(x => x.s.EndsAt) : q.OrderByDescending(x => x.s.EndsAt),
                _ => ascending ? q.OrderBy(x => x.s.CreatedAt) : q.OrderByDescending(x => x.s.CreatedAt),
            };

            var items = await q
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new AdminSubscriptionListItem
                {
                    SubscriptionId = x.s.Id,
                    BusinessId = x.b.Id,
                    BusinessName = x.b.Name,
                    BusinessStatus = x.b.Status,
                    PlanId = x.p.Id,
                    PlanKey = x.p.Key,
                    PlanName = x.p.Name,
                    BillingInterval = x.p.BillingInterval,
                    Price = x.p.Price,
                    Status = x.s.Status,
                    StartsAt = x.s.StartsAt,
                    EndsAt = x.s.EndsAt,
                    PausedAt = x.s.PausedAt,
                    CanceledAt = x.s.CanceledAt,
                    CreatedAt = x.s.CreatedAt,
                    EnabledModuleCount = x.p.PlanModules.Count,
                })
                .ToListAsync();

            return ApiResponse<PaginatedResponse<AdminSubscriptionListItem>>.Ok(
                new PaginatedResponse<AdminSubscriptionListItem>
                {
                    Items = items,
                    TotalCount = totalCount,
                    Page = page,
                    PageSize = pageSize,
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list subscriptions for the platform administrator.");
            return ApiResponse<PaginatedResponse<AdminSubscriptionListItem>>.Fail(
                "LIST_FAILED", "Failed to list subscriptions.");
        }
    }

    /// <inheritdoc />
    public async Task<ApiResponse<AdminSubscriptionStats>> GetStatsAsync()
    {
        try
        {
            var soon = DateTime.UtcNow.AddDays(7);

            var grouped = await _context.BusinessSubscriptions.AsNoTracking()
                .GroupBy(s => s.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync();
            int Count(string status) => grouped.FirstOrDefault(g => g.Status == status)?.Count ?? 0;

            var renewingIn7Days = await _context.BusinessSubscriptions.AsNoTracking()
                .CountAsync(s => (s.Status == "active" || s.Status == "trial") &&
                                 s.EndsAt != null && s.EndsAt <= soon);

            // Recurring revenue from access-granting subscriptions. Yearly plans are
            // normalised to a monthly figure (price / 12) for MRR; ARR is MRR × 12.
            var monthly = await _context.BusinessSubscriptions.AsNoTracking()
                .Where(s => (s.Status == "active" || s.Status == "trial") && s.Plan.BillingInterval == "monthly")
                .SumAsync(s => (decimal?)s.Plan.Price) ?? 0m;
            var yearly = await _context.BusinessSubscriptions.AsNoTracking()
                .Where(s => (s.Status == "active" || s.Status == "trial") && s.Plan.BillingInterval == "yearly")
                .SumAsync(s => (decimal?)s.Plan.Price) ?? 0m;

            var mrr = monthly + Math.Round(yearly / 12m, 2);

            return ApiResponse<AdminSubscriptionStats>.Ok(new AdminSubscriptionStats
            {
                Total = grouped.Sum(g => g.Count),
                Active = Count("active"),
                Trial = Count("trial"),
                Paused = Count("paused"),
                Canceled = Count("canceled"),
                Expired = Count("expired"),
                PastDue = Count("past_due"),
                Deactivated = Count("deactivated"),
                RenewingIn7Days = renewingIn7Days,
                MonthlyRecurringRevenue = mrr,
                AnnualRecurringRevenue = Math.Round(mrr * 12m, 2),
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute subscription statistics.");
            return ApiResponse<AdminSubscriptionStats>.Fail(
                "STATS_FAILED", "Failed to compute subscription statistics.");
        }
    }
}
