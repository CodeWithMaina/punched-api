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
}