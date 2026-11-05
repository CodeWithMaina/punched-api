using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// <see cref="IAdminBusinessService"/> implementation. Read queries are
/// projection-only (no entity tracking); every mutation is audited through
/// <see cref="ISubscriptionAuditService"/> and invalidates the business's
/// entitlement cache where module access could change.
/// </summary>
public class AdminBusinessService : IAdminBusinessService
{
    private const string ErrNotFound = "BUSINESS_NOT_FOUND";
    private const string ErrNotFoundMessage = "No business exists with the given id.";
    private const string ErrInvalidStatus = "INVALID_BUSINESS_STATUS";

    private readonly ApplicationDbContext _context;
    private readonly IModuleEntitlementService _entitlements;
    private readonly ISubscriptionAuditService _audit;
    private readonly ITenantUrlBuilder _tenantUrls;
    private readonly ILogger<AdminBusinessService> _logger;

    public AdminBusinessService(
        ApplicationDbContext context,
        IModuleEntitlementService entitlements,
        ISubscriptionAuditService audit,
        ITenantUrlBuilder tenantUrls,
        ILogger<AdminBusinessService> logger)
    {
        _context = context;
        _entitlements = entitlements;
        _audit = audit;
        _tenantUrls = tenantUrls;
        _logger = logger;
    }

    // ═════════════════════════════════════════════════════════
    //  LIST
    // ═════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<ApiResponse<PaginatedResponse<AdminBusinessListItem>>> ListAsync(
        AdminBusinessListQuery query, int page, int pageSize)
    {
        try
        {
            var q = _context.Businesses
                .AsNoTracking()
                .Include(b => b.Owner)
                .Include(b => b.CurrentSubscription)
                    .ThenInclude(s => s!.Plan)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var pattern = $"%{query.Search.Trim()}%";
                q = q.Where(b =>
                    EF.Functions.ILike(b.Name, pattern) ||
                    (b.Slug != null && EF.Functions.ILike(b.Slug, pattern)) ||
                    (b.Email != null && EF.Functions.ILike(b.Email, pattern)) ||
                    (b.PhoneNumber != null && EF.Functions.ILike(b.PhoneNumber, pattern)) ||
                    (b.Owner != null &&
                        (EF.Functions.ILike(b.Owner.FullName, pattern) ||
                         EF.Functions.ILike(b.Owner.Email, pattern))));
            }

            if (!string.IsNullOrWhiteSpace(query.Status))
                q = q.Where(b => b.Status == query.Status);

            if (!string.IsNullOrWhiteSpace(query.Category))
                q = q.Where(b => b.Category == query.Category);

            if (!string.IsNullOrWhiteSpace(query.PlanKey))
                q = q.Where(b => b.CurrentSubscription != null &&
                                 b.CurrentSubscription.Plan.Key == query.PlanKey);

            if (!string.IsNullOrWhiteSpace(query.SubscriptionStatus))
                q = q.Where(b => b.CurrentSubscription != null &&
                                 b.CurrentSubscription.Status == query.SubscriptionStatus);

            if (query.RegisteredFrom.HasValue)
                q = q.Where(b => b.CreatedAt >= query.RegisteredFrom.Value);

            if (query.RegisteredTo.HasValue)
                q = q.Where(b => b.CreatedAt <= query.RegisteredTo.Value);

            if (query.ExpiringBefore.HasValue)
                q = q.Where(b => b.CurrentSubscription != null &&
                                 b.CurrentSubscription.EndsAt != null &&
                                 b.CurrentSubscription.EndsAt <= query.ExpiringBefore.Value);

            // Module filter: the business must hold an EFFECTIVE entitlement —
            // either the (active) plan grants it and no disabling override exists,
            // or an explicit override enables it.
            if (!string.IsNullOrWhiteSpace(query.ModuleKey))
            {
                var moduleKey = query.ModuleKey.Trim();
                var module = await _context.Modules.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Key == moduleKey);
                if (module == null)
                {
                    return ApiResponse<PaginatedResponse<AdminBusinessListItem>>.Ok(
                        EmptyPage(page, pageSize));
                }

                var moduleId = module.Id;
                q = q.Where(b =>
                    (_context.BusinessModules.Any(bm =>
                        bm.BusinessId == b.Id && bm.ModuleId == moduleId && bm.IsEnabled)) ||
                    (b.CurrentSubscription != null &&
                     (b.CurrentSubscription.Status == "active" || b.CurrentSubscription.Status == "trial") &&
                     b.CurrentSubscription.Plan.PlanModules.Any(pm => pm.ModuleId == moduleId) &&
                     !_context.BusinessModules.Any(bm =>
                         bm.BusinessId == b.Id && bm.ModuleId == moduleId && !bm.IsEnabled)));
            }

            var totalCount = await q.CountAsync();

            var ascending = string.Equals(query.Direction, "asc", StringComparison.OrdinalIgnoreCase);
            q = (query.Sort ?? "created").ToLowerInvariant() switch
            {
                "name" => ascending ? q.OrderBy(b => b.Name) : q.OrderByDescending(b => b.Name),
                "status" => ascending ? q.OrderBy(b => b.Status) : q.OrderByDescending(b => b.Status),
                "expiry" => ascending
                    ? q.OrderBy(b => b.CurrentSubscription!.EndsAt)
                    : q.OrderByDescending(b => b.CurrentSubscription!.EndsAt),
                _ => ascending ? q.OrderBy(b => b.CreatedAt) : q.OrderByDescending(b => b.CreatedAt),
            };

            var rows = await q
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(b => new
                {
                    b.Id,
                    b.Name,
                    b.Category,
                    b.Location,
                    b.LogoUrl,
                    b.Slug,
                    b.Email,
                    b.PhoneNumber,
                    b.Status,
                    b.OwnerId,
                    OwnerName = b.Owner != null ? b.Owner.FullName : "Unknown",
                    OwnerEmail = b.Owner != null ? b.Owner.Email : string.Empty,
                    PlanId = b.CurrentSubscription != null ? b.CurrentSubscription.PlanId : (Guid?)null,
                    PlanKey = b.CurrentSubscription != null ? b.CurrentSubscription.Plan.Key : null,
                    PlanName = b.CurrentSubscription != null ? b.CurrentSubscription.Plan.Name : null,
                    SubStatus = b.CurrentSubscription != null ? b.CurrentSubscription.Status : null,
                    SubEndsAt = b.CurrentSubscription != null ? b.CurrentSubscription.EndsAt : null,
                    b.CreatedAt,
                    TotalStaff = _context.BusinessMemberships.Count(m =>
                        m.BusinessId == b.Id && m.Role == BusinessMembershipRole.Staff &&
                        m.Status == BusinessMembershipStatus.Active),
                    TotalCustomers = _context.BusinessMemberships.Count(m =>
                        m.BusinessId == b.Id && m.Role == BusinessMembershipRole.Customer),
                    TotalStamps = _context.Stamps.Count(s => s.Card.BusinessId == b.Id),
                })
                .ToListAsync();

            var ids = rows.Select(r => r.Id).ToList();
            var planIds = rows.Where(r => r.PlanId.HasValue).Select(r => r.PlanId!.Value).Distinct().ToList();
            var enabledModules = await ResolveEnabledModulesAsync(ids, planIds);
            var lastActivity = await ResolveLastActivityAsync(ids);

            var items = rows.Select(r =>
            {
                var item = new AdminBusinessListItem
                {
                    Id = r.Id,
                    Name = r.Name,
                    Category = r.Category,
                    Location = r.Location,
                    LogoUrl = r.LogoUrl,
                    Slug = r.Slug,
                    Email = r.Email,
                    PhoneNumber = r.PhoneNumber,
                    Status = r.Status,
                    OwnerId = r.OwnerId,
                    OwnerName = r.OwnerName,
                    OwnerEmail = r.OwnerEmail,
                    PlanKey = r.PlanKey,
                    PlanName = r.PlanName,
                    SubscriptionStatus = r.SubStatus,
                    SubscriptionEndsAt = r.SubEndsAt,
                    EnabledModules = enabledModules.TryGetValue(r.Id, out var keys)
                        ? keys
                        : new List<string>(),
                    TotalStaff = r.TotalStaff,
                    TotalCustomers = r.TotalCustomers,
                    TotalStamps = r.TotalStamps,
                    LastActivityAt = lastActivity.TryGetValue(r.Id, out var at) ? at : (DateTime?)null,
                    CreatedAt = r.CreatedAt,
                };
                item.TenantUrl = _tenantUrls.BuildForSlug(item.Slug);
                return item;
            }).ToList();

            return ApiResponse<PaginatedResponse<AdminBusinessListItem>>.Ok(
                new PaginatedResponse<AdminBusinessListItem>
                {
                    Items = items,
                    TotalCount = totalCount,
                    Page = page,
                    PageSize = pageSize,
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list businesses for the platform administrator.");
            return ApiResponse<PaginatedResponse<AdminBusinessListItem>>.Fail(
                "LIST_FAILED", "Failed to list businesses.");
        }
    }

    private static PaginatedResponse<AdminBusinessListItem> EmptyPage(int page, int pageSize) => new()
    {
        Items = new List<AdminBusinessListItem>(),
        TotalCount = 0,
        Page = page,
        PageSize = pageSize,
    };

    /// <summary>
    /// Effective module keys per business for a page of rows: the plan's granted
    /// keys (for access-granting subscriptions) unioned with override-enabled
    /// keys, minus override-disabled keys. Two grouped queries, never N+1.
    /// </summary>
    private async Task<Dictionary<Guid, List<string>>> ResolveEnabledModulesAsync(
        List<Guid> businessIds, List<Guid> planIds)
    {
        var result = new Dictionary<Guid, List<string>>();
        if (businessIds.Count == 0) return result;

        var planModulesByPlan = new Dictionary<Guid, List<string>>();
        if (planIds.Count > 0)
        {
            var planModules = await _context.PlanModules.AsNoTracking()
                .Where(pm => planIds.Contains(pm.PlanId))
                .Select(pm => new { pm.PlanId, Key = pm.Module.Key })
                .ToListAsync();
            planModulesByPlan = planModules
                .GroupBy(x => x.PlanId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Key).ToList());
        }

        var overrides = await _context.BusinessModules.AsNoTracking()
            .Where(bm => businessIds.Contains(bm.BusinessId))
            .Select(bm => new { bm.BusinessId, Key = bm.Module.Key, bm.IsEnabled })
            .ToListAsync();
        var overridesByBusiness = overrides
            .GroupBy(x => x.BusinessId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Subscription status/plan per business, to decide whether the plan grants.
        var subscriptionStates = await _context.BusinessSubscriptions.AsNoTracking()
            .Where(s => businessIds.Contains(s.BusinessId))
            .Select(s => new { s.BusinessId, s.PlanId, s.Status, s.EndsAt })
            .ToListAsync();
        var subscriptionByBusiness = subscriptionStates.ToDictionary(s => s.BusinessId);

        var now = DateTime.UtcNow;
        foreach (var businessId in businessIds)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (subscriptionByBusiness.TryGetValue(businessId, out var sub) &&
                SubscriptionStatus.GrantsAccess(sub.Status) &&
                (!sub.EndsAt.HasValue || sub.EndsAt > now) &&
                planModulesByPlan.TryGetValue(sub.PlanId, out var planKeys))
            {
                foreach (var key in planKeys) keys.Add(key);
            }

            if (overridesByBusiness.TryGetValue(businessId, out var businessOverrides))
            {
                foreach (var ov in businessOverrides)
                {
                    if (ov.IsEnabled) keys.Add(ov.Key);
                    else keys.Remove(ov.Key);
                }
            }

            result[businessId] = keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }

        return result;
    }

    /// <summary>
    /// Latest activity timestamp per business, derived from the most recent stamp
    /// or redemption. Businesses with no recorded activity are absent from the map.
    /// </summary>
    private async Task<Dictionary<Guid, DateTime>> ResolveLastActivityAsync(List<Guid> businessIds)
    {
        var result = new Dictionary<Guid, DateTime>();
        if (businessIds.Count == 0) return result;

        var stampMax = await _context.Stamps.AsNoTracking()
            .Where(s => businessIds.Contains(s.Card.BusinessId))
            .GroupBy(s => s.Card.BusinessId)
            .Select(g => new { BusinessId = g.Key, Max = g.Max(s => s.CreatedAt) })
            .ToListAsync();
        foreach (var row in stampMax) result[row.BusinessId] = row.Max;

        var redemptionMax = await _context.Redemptions.AsNoTracking()
            .Where(r => businessIds.Contains(r.BusinessId))
            .GroupBy(r => r.BusinessId)
            .Select(g => new { BusinessId = g.Key, Max = g.Max(r => r.CreatedAt) })
            .ToListAsync();
        foreach (var row in redemptionMax)
        {
            if (!result.TryGetValue(row.BusinessId, out var existing) || row.Max > existing)
                result[row.BusinessId] = row.Max;
        }

        return result;
    }

    // ═════════════════════════════════════════════════════════
    //  STATS
    // ═════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<ApiResponse<AdminBusinessStats>> GetStatsAsync()
    {
        try
        {
            var now = DateTime.UtcNow;
            var soon = now.AddDays(7);

            var total = await _context.Businesses.CountAsync();
            var active = await _context.Businesses.CountAsync(b => b.Status == BusinessStatus.Active);
            var suspended = await _context.Businesses.CountAsync(b => b.Status == BusinessStatus.Suspended);
            var deactivated = await _context.Businesses.CountAsync(b => b.Status == BusinessStatus.Deactivated);

            var subs = _context.BusinessSubscriptions.AsNoTracking();
            var activeSubs = await subs.CountAsync(s => s.Status == "active");
            var trial = await subs.CountAsync(s => s.Status == "trial");
            var expired = await subs.CountAsync(s => s.Status == "expired");
            var withSub = await subs.CountAsync();
            var expiringSoon = await subs.CountAsync(s =>
                (s.Status == "active" || s.Status == "trial") &&
                s.EndsAt != null && s.EndsAt <= soon);

            return ApiResponse<AdminBusinessStats>.Ok(new AdminBusinessStats
            {
                Total = total,
                Active = active,
                Suspended = suspended,
                Deactivated = deactivated,
                Trial = trial,
                ActiveSubscriptions = activeSubs,
                ExpiredSubscriptions = expired,
                ExpiringSoon = expiringSoon,
                NoSubscription = Math.Max(0, total - withSub),
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute admin business statistics.");
            return ApiResponse<AdminBusinessStats>.Fail(
                "STATS_FAILED", "Failed to compute business statistics.");
        }
    }

    // ═════════════════════════════════════════════════════════
    //  DETAIL
    // ═════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<ApiResponse<AdminBusinessDetailResponse>> GetDetailAsync(Guid businessId)
    {
        try
        {
            var detail = await BuildDetailAsync(businessId);
            if (detail == null)
                return ApiResponse<AdminBusinessDetailResponse>.Fail(ErrNotFound, ErrNotFoundMessage);

            return ApiResponse<AdminBusinessDetailResponse>.Ok(detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load business {BusinessId} for the platform administrator.", businessId);
            return ApiResponse<AdminBusinessDetailResponse>.Fail(
                "GET_FAILED", "Failed to load the business.");
        }
    }

    /// <summary>
    /// Projects one business into the admin detail shape (profile, owner,
    /// subscription, counters, last activity). Returns null when no such
    /// (non-deleted) business exists.
    /// </summary>
    private async Task<AdminBusinessDetailResponse?> BuildDetailAsync(Guid businessId)
    {
        var b = await _context.Businesses
            .AsNoTracking()
            .Include(x => x.Owner)
            .Include(x => x.CurrentSubscription)
                .ThenInclude(s => s!.Plan)
            .FirstOrDefaultAsync(x => x.Id == businessId);

        if (b == null) return null;

        var detail = new AdminBusinessDetailResponse
        {
            Id = b.Id,
            Name = b.Name,
            Category = b.Category,
            Location = b.Location,
            Description = b.Description,
            LogoUrl = b.LogoUrl,
            Email = b.Email,
            PhoneNumber = b.PhoneNumber,
            MpesaNumber = b.MpesaNumber,
            TimeZoneId = b.TimeZoneId,
            Slug = b.Slug,
            Status = b.Status,
            SuspendedAt = b.SuspendedAt,
            SuspendedReason = b.SuspendedReason,
            DeactivatedAt = b.DeactivatedAt,
            CreatedAt = b.CreatedAt,
            Owner = new AdminBusinessOwnerInfo
            {
                Id = b.OwnerId,
                FullName = b.Owner?.FullName ?? "Unknown",
                Email = b.Owner?.Email ?? string.Empty,
                PhoneNumber = b.Owner?.PhoneNumber,
                AvatarUrl = b.Owner?.AvatarUrl,
            },
            TotalStaff = await _context.BusinessMemberships.CountAsync(m =>
                m.BusinessId == businessId && m.Role == BusinessMembershipRole.Staff &&
                m.Status == BusinessMembershipStatus.Active),
            TotalCustomers = await _context.BusinessMemberships.CountAsync(m =>
                m.BusinessId == businessId && m.Role == BusinessMembershipRole.Customer),
            TotalStamps = await _context.Stamps.CountAsync(s => s.Card.BusinessId == businessId),
            TotalRedemptions = await _context.Redemptions.CountAsync(r => r.BusinessId == businessId),
            ProgramCount = await _context.LoyaltyPrograms.CountAsync(p => p.BusinessId == businessId),
        };
        detail.TenantUrl = _tenantUrls.BuildForSlug(detail.Slug);

        var lastActivity = await ResolveLastActivityAsync(new List<Guid> { businessId });
        detail.LastActivityAt = lastActivity.TryGetValue(businessId, out var at) ? at : null;

        var sub = b.CurrentSubscription;
        if (sub != null)
        {
            detail.Subscription = new AdminBusinessSubscriptionInfo
            {
                SubscriptionId = sub.Id,
                PlanId = sub.PlanId,
                PlanKey = sub.Plan?.Key,
                PlanName = sub.Plan?.Name,
                BillingInterval = sub.Plan?.BillingInterval,
                Price = sub.Plan?.Price,
                Status = sub.Status,
                StartsAt = sub.StartsAt,
                CurrentPeriodStartsAt = sub.CurrentPeriodStartsAt,
                EndsAt = sub.EndsAt,
                CanceledAt = sub.CanceledAt,
                PausedAt = sub.PausedAt,
                PauseReason = sub.PauseReason,
                IsTrial = sub.Status == SubscriptionStatus.Trial,
                DaysUntilExpiry = sub.EndsAt.HasValue
                    ? (int)Math.Ceiling((sub.EndsAt.Value - DateTime.UtcNow).TotalDays)
                    : null,
            };
        }

        return detail;
    }

    // ═════════════════════════════════════════════════════════
    //  UPDATE
    // ═════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<ApiResponse<AdminBusinessDetailResponse>> UpdateAsync(
        Guid businessId, AdminBusinessUpdateRequest request, Guid? actorUserId)
    {
        try
        {
            var business = await _context.Businesses.FirstOrDefaultAsync(b => b.Id == businessId);
            if (business == null)
                return ApiResponse<AdminBusinessDetailResponse>.Fail(ErrNotFound, ErrNotFoundMessage);

            var before = new
            {
                business.Name,
                business.Category,
                business.Location,
                business.Description,
                business.Email,
                business.PhoneNumber,
                business.MpesaNumber,
                business.TimeZoneId,
            };

            var problems = new List<string>();
            void CheckRequired(string? value, int max, string label)
            {
                if (value == null) return;
                var length = value.Trim().Length;
                if (length == 0 || length > max)
                    problems.Add($"{label} must be between 1 and {max} characters.");
            }

            CheckRequired(request.Name, 100, "Name");
            CheckRequired(request.Category, 50, "Category");
            CheckRequired(request.Location, 100, "Location");
            CheckRequired(request.TimeZoneId, 64, "Time zone id");
            if (request.Description is { Length: > 500 }) problems.Add("Description must be 500 characters or fewer.");
            if (request.Email is { Length: > 255 }) problems.Add("Email must be 255 characters or fewer.");
            if (request.PhoneNumber is { Length: > 20 }) problems.Add("Phone number must be 20 characters or fewer.");
            if (request.MpesaNumber is { Length: > 20 }) problems.Add("M-Pesa number must be 20 characters or fewer.");
            if (request.LogoUrl is { Length: > 500 }) problems.Add("Logo URL must be 500 characters or fewer.");
            if (problems.Count > 0)
                return ApiResponse<AdminBusinessDetailResponse>.Fail(
                    "VALIDATION", string.Join(" ", problems));

            if (request.Name != null) business.Name = request.Name.Trim();
            if (request.Category != null) business.Category = request.Category.Trim();
            if (request.Location != null) business.Location = request.Location.Trim();
            if (request.Description != null) business.Description = NullIfBlank(request.Description);
            if (request.Email != null) business.Email = NullIfBlank(request.Email);
            if (request.PhoneNumber != null) business.PhoneNumber = NullIfBlank(request.PhoneNumber);
            if (request.MpesaNumber != null) business.MpesaNumber = request.MpesaNumber.Trim();
            if (request.LogoUrl != null) business.LogoUrl = NullIfBlank(request.LogoUrl);
            if (request.TimeZoneId != null) business.TimeZoneId = request.TimeZoneId.Trim();

            await _context.SaveChangesAsync();

            await _audit.RecordAsync(
                "BUSINESS_PROFILE_UPDATED", actorUserId, targetBusinessId: businessId, targetPlanId: null,
                payloadJson: JsonSerializer.Serialize(new
                {
                    before,
                    after = new
                    {
                        business.Name,
                        business.Category,
                        business.Location,
                        business.Description,
                        business.Email,
                        business.PhoneNumber,
                        business.MpesaNumber,
                        business.TimeZoneId,
                    },
                }),
                reason: request.Reason ?? "Platform administrator edited the business profile.");

            _logger.LogWarning(
                "ADMIN edited business {BusinessId} profile by {ActorUserId}. Reason: {Reason}",
                businessId, actorUserId, request.Reason ?? "(none)");

            var detail = await BuildDetailAsync(businessId);
            return detail == null
                ? ApiResponse<AdminBusinessDetailResponse>.Fail(ErrNotFound, ErrNotFoundMessage)
                : ApiResponse<AdminBusinessDetailResponse>.Ok(detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update business {BusinessId}.", businessId);
            return ApiResponse<AdminBusinessDetailResponse>.Fail(
                "UPDATE_FAILED", "Failed to update the business.");
        }
    }

    private static string? NullIfBlank(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    // ═════════════════════════════════════════════════════════
    //  USERS
    // ═════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<ApiResponse<PaginatedResponse<AdminBusinessUserItem>>> GetUsersAsync(
        Guid businessId, string? role, int page, int pageSize)
    {
        try
        {
            var exists = await _context.Businesses.AnyAsync(b => b.Id == businessId);
            if (!exists)
                return ApiResponse<PaginatedResponse<AdminBusinessUserItem>>.Fail(
                    ErrNotFound, ErrNotFoundMessage);

            var query = _context.BusinessMemberships
                .AsNoTracking()
                .Where(m => m.BusinessId == businessId)
                .Join(
                    _context.Users.AsNoTracking(),
                    m => m.UserId,
                    u => u.Id,
                    (m, u) => new { Membership = m, User = u })
                .Join(
                    _context.UserAuths.AsNoTracking(),
                    x => x.User.Email,
                    a => a.Email,
                    (x, a) => new { x.Membership, x.User, Auth = a });

            if (!string.IsNullOrWhiteSpace(role) &&
                Enum.TryParse<BusinessMembershipRole>(role, ignoreCase: true, out var parsedRole))
            {
                query = query.Where(x => x.Membership.Role == parsedRole);
            }

            var totalCount = await query.CountAsync();
            var items = await query
                .OrderBy(x => x.Membership.Role)
                .ThenBy(x => x.User.FullName)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new AdminBusinessUserItem
                {
                    UserId = x.User.Id,
                    FullName = x.User.FullName,
                    Email = x.User.Email,
                    PhoneNumber = x.User.PhoneNumber,
                    Role = x.Membership.Role == BusinessMembershipRole.Owner ? "Owner"
                        : x.Membership.Role == BusinessMembershipRole.Staff ? "Staff" : "Customer",
                    Status = x.Membership.Status == BusinessMembershipStatus.Active ? "Active"
                        : x.Membership.Status == BusinessMembershipStatus.Invited ? "Invited"
                        : x.Membership.Status == BusinessMembershipStatus.Suspended ? "Suspended" : "Left",
                    JoinedAt = x.Membership.JoinedAt,
                    EndedAt = x.Membership.EndedAt,
                    GlobalRole = x.User.Role.ToString(),
                    IsVerified = x.Auth.IsVerified,
                })
                .ToListAsync();

            return ApiResponse<PaginatedResponse<AdminBusinessUserItem>>.Ok(
                new PaginatedResponse<AdminBusinessUserItem>
                {
                    Items = items,
                    TotalCount = totalCount,
                    Page = page,
                    PageSize = pageSize,
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list members of business {BusinessId}.", businessId);
            return ApiResponse<PaginatedResponse<AdminBusinessUserItem>>.Fail(
                "LIST_FAILED", "Failed to list the business's members.");
        }
    }

    // ═════════════════════════════════════════════════════════
    //  STATUS (suspend / reactivate / deactivate)
    // ═════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<ApiResponse<AdminBusinessDetailResponse>> SetStatusAsync(
        Guid businessId, string status, string? reason, Guid? actorUserId)
    {
        try
        {
            var normalized = status?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!BusinessStatus.IsValid(normalized))
                return ApiResponse<AdminBusinessDetailResponse>.Fail(
                    ErrInvalidStatus, "Status must be 'active', 'suspended' or 'deactivated'.");

            var business = await _context.Businesses.FirstOrDefaultAsync(b => b.Id == businessId);
            if (business == null)
                return ApiResponse<AdminBusinessDetailResponse>.Fail(ErrNotFound, ErrNotFoundMessage);

            var previous = business.Status;
            var now = DateTime.UtcNow;
            business.Status = normalized;

            if (normalized == BusinessStatus.Suspended)
            {
                business.SuspendedAt = now;
                business.SuspendedReason = NullIfBlank(reason ?? string.Empty);
            }
            else
            {
                business.SuspendedAt = null;
                business.SuspendedReason = null;
                business.DeactivatedAt = normalized == BusinessStatus.Deactivated ? now : null;
            }

            await _context.SaveChangesAsync();

            // Suspension and deactivation withdraw module access immediately.
            _entitlements.Invalidate(businessId);

            var action = normalized switch
            {
                BusinessStatus.Suspended => "BUSINESS_SUSPENDED",
                BusinessStatus.Deactivated => "BUSINESS_DEACTIVATED",
                _ => "BUSINESS_REACTIVATED",
            };
            await _audit.RecordAsync(
                action, actorUserId, targetBusinessId: businessId, targetPlanId: null,
                payloadJson: JsonSerializer.Serialize(new { previous, status = normalized }),
                reason: reason ?? $"Platform administrator set the business status to '{normalized}'.");

            _logger.LogWarning(
                "ADMIN set business {BusinessId} status {Previous} -> {Status} by {ActorUserId}. Reason: {Reason}",
                businessId, previous, normalized, actorUserId, reason ?? "(none)");

            var detail = await BuildDetailAsync(businessId);
            return detail == null
                ? ApiResponse<AdminBusinessDetailResponse>.Fail(ErrNotFound, ErrNotFoundMessage)
                : ApiResponse<AdminBusinessDetailResponse>.Ok(detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set status for business {BusinessId}.", businessId);
            return ApiResponse<AdminBusinessDetailResponse>.Fail(
                "STATUS_FAILED", "Failed to change the business status.");
        }
    }
}
