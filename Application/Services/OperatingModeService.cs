using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Owns the two pieces of provider configuration that drive the whole
/// application: the persisted <b>operating mode</b> and the
/// <b>provider ↔ category</b> assignments.
///
/// <para>Both live here so a single switch can update the mode and seed the
/// starter categories in one atomic save, and so the effective provider model is
/// resolved through the one <see cref="IProviderEligibilityService"/>.</para>
/// </summary>
public interface IOperatingModeService
{
    /// <summary>The caller business's operating mode + effective provider model.</summary>
    Task<ApiResponse<OperatingModeResponse>> GetAsync(Guid callerUserId, string role);

    /// <summary>One-action switch between Solo and Multi-Staff.</summary>
    Task<ApiResponse<OperatingModeResponse>> UpdateAsync(
        Guid callerUserId, string role, UpdateOperatingModeRequest request);

    /// <summary>A provider's category specialisations.</summary>
    Task<ApiResponse<ProviderCategoriesResponse>> GetProviderCategoriesAsync(
        Guid callerUserId, string role, Guid providerUserId);

    /// <summary>Replaces a provider's category specialisations (the array is the whole set).</summary>
    Task<ApiResponse<ProviderCategoriesResponse>> SetProviderCategoriesAsync(
        Guid callerUserId, string role, Guid providerUserId, SetProviderCategoriesRequest request);

    /// <summary>
    /// Seeds the default categories for a business that has none. Idempotent and
    /// cheap: a business with any category is left completely untouched.
    /// </summary>
    Task SeedDefaultCategoriesAsync(Guid businessId);

    /// <summary>Startup backfill: gives every category-less business the default set.</summary>
    Task<int> BackfillDefaultCategoriesAsync();
}

/// <inheritdoc />
public sealed class OperatingModeService : IOperatingModeService
{
    private readonly ApplicationDbContext _context;
    private readonly IBusinessScopeResolver _scopeResolver;
    private readonly IPermissionService _permissions;
    private readonly IModuleEntitlementService _entitlements;
    private readonly IProviderEligibilityService _eligibility;
    private readonly ILogger<OperatingModeService> _logger;
    private readonly ITenantContext? _tenant;

    public OperatingModeService(
        ApplicationDbContext context,
        IBusinessScopeResolver scopeResolver,
        IPermissionService permissions,
        IModuleEntitlementService entitlements,
        IProviderEligibilityService eligibility,
        ILogger<OperatingModeService> logger,
        ITenantContext? tenant = null)
    {
        _context = context;
        _scopeResolver = scopeResolver;
        _permissions = permissions;
        _entitlements = entitlements;
        _eligibility = eligibility;
        _logger = logger;
        _tenant = tenant;
    }

    private Guid? TenantBusinessId => _tenant?.IsActive == true ? _tenant.BusinessId : null;

    private static bool IsRole(string value, string expected) =>
        string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private async Task<Guid?> ResolveCallerBusinessIdAsync(Guid callerUserId, string role)
    {
        if (TenantBusinessId is Guid tenantId) return tenantId;
        if (callerUserId == Guid.Empty) return null;

        if (IsRole(role, "Business"))
        {
            var owned = await _scopeResolver.GetOwnedBusinessIdAsync(callerUserId);
            if (owned.HasValue) return owned;
        }

        return await _scopeResolver.GetMemberBusinessIdAsync(callerUserId);
    }

    private async Task<bool> HasPermissionAsync(Guid userId, Guid businessId, string permissionCode) =>
        await _permissions.HasBusinessPermissionAsync(userId, businessId, permissionCode);

    public async Task<ApiResponse<OperatingModeResponse>> GetAsync(Guid callerUserId, string role)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId is null)
            return ApiResponse<OperatingModeResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var response = await BuildResponseAsync(businessId.Value);
        return response == null
            ? ApiResponse<OperatingModeResponse>.Fail("NOT_FOUND", "No business found for this account.")
            : ApiResponse<OperatingModeResponse>.Ok(response);
    }

    private async Task<OperatingModeResponse?> BuildResponseAsync(Guid businessId)
    {
        var business = await _context.Businesses.AsNoTracking()
            .Where(b => b.Id == businessId)
            .Select(b => new { b.OperatingMode })
            .FirstOrDefaultAsync();

        if (business == null) return null;

        var staffEnabled = await _entitlements.IsModuleEnabledAsync(
            businessId, ProviderEligibilityService.StaffModuleKey);
        var effectiveModel = await _eligibility.ResolveModelAsync(businessId);

        var categoryCount = await _context.ServiceCategories.AsNoTracking()
            .CountAsync(c => c.BusinessId == businessId);
        var activeServiceCount = await _context.ServiceCatalogItems.AsNoTracking()
            .CountAsync(s => s.BusinessId == businessId && s.IsActive && !s.IsArchived);
        var staffCount = await _context.BusinessMemberships.AsNoTracking()
            .CountAsync(m => m.BusinessId == businessId &&
                             m.Role == BusinessMembershipRole.Staff &&
                             m.Status == BusinessMembershipStatus.Active);

        var (ownerId, ownerProvides) = await _eligibility.ResolveOwnerProviderAsync(businessId);
        var ownerCategoryIds = ownerId is Guid owner
            ? (await _eligibility.ResolveCategoryIdsForProviderAsync(businessId, owner)).ToList()
            : new List<Guid>();

        return new OperatingModeResponse
        {
            OperatingMode = BusinessOperatingModes.Normalize(business.OperatingMode),
            OperatingModeLabel = BusinessOperatingModes.Label(business.OperatingMode),
            EffectiveProviderModel = effectiveModel,
            StaffModuleEnabled = staffEnabled,
            RequiresProviderSelection = effectiveModel == EffectiveProviderModel.CategoryBased,
            Description = Describe(effectiveModel),
            CategoryCount = categoryCount,
            ActiveServiceCount = activeServiceCount,
            StaffCount = staffCount,
            OwnerIsProvider = ownerProvides,
            OwnerUserId = ownerId,
            OwnerCategoryIds = ownerCategoryIds
        };
    }

    private static string Describe(string effectiveModel) => effectiveModel switch
    {
        EffectiveProviderModel.Solo =>
            "You work alone and provide every service yourself. Customers book directly with you — no staff selection is shown.",
        EffectiveProviderModel.OwnerOnly =>
            "The Staff module is not enabled on your plan, so you are the provider for every service. Your business stays fully operational without staff.",
        _ =>
            "You have a team. Each provider is eligible for the services in the categories they are assigned to — assign the owner and staff to the categories they specialise in."
    };

    public async Task<ApiResponse<OperatingModeResponse>> UpdateAsync(
        Guid callerUserId, string role, UpdateOperatingModeRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId is null)
            return ApiResponse<OperatingModeResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var requested = request?.OperatingMode?.Trim();
        if (!BusinessOperatingModes.IsValid(requested))
            return ApiResponse<OperatingModeResponse>.Fail(
                "VALIDATION_ERROR", "Choose either a Solo Business or a Multi-Staff Business.");

        var target = BusinessOperatingModes.Normalize(requested);

        var business = await _context.Businesses.FirstOrDefaultAsync(b => b.Id == businessId.Value);
        if (business == null)
            return ApiResponse<OperatingModeResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (string.Equals(BusinessOperatingModes.Normalize(business.OperatingMode), target, StringComparison.Ordinal))
            return ApiResponse<OperatingModeResponse>.Ok((await BuildResponseAsync(businessId.Value))!);

        // One atomic save covers the mode flip AND (for multi-staff) the
        // idempotent category seed, so the two can never diverge. Switching never
        // deletes staff, services, categories or history — it only changes which
        // provider model the application resolves.
        business.OperatingMode = target;

        if (target == BusinessOperatingModes.MultiStaff)
            await SeedDefaultCategoriesCoreAsync(businessId.Value);

        await _context.SaveChangesAsync();

        // The mode is not part of a cached entitlement, but the resolved provider
        // model is derived from entitlement + mode together; dropping the business's
        // cached configuration guarantees the next read re-resolves from the DB.
        _entitlements.Invalidate(businessId.Value);

        _logger.LogInformation(
            "Business {BusinessId} operating mode switched to {Mode} by {UserId}",
            businessId.Value, target, callerUserId);

        return ApiResponse<OperatingModeResponse>.Ok((await BuildResponseAsync(businessId.Value))!);
    }

    public async Task<ApiResponse<ProviderCategoriesResponse>> GetProviderCategoriesAsync(
        Guid callerUserId, string role, Guid providerUserId)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId is null)
            return ApiResponse<ProviderCategoriesResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "staff.view"))
            return ApiResponse<ProviderCategoriesResponse>.Fail("FORBIDDEN", "You are not allowed to view staff.");

        var result = await BuildProviderCategoriesAsync(businessId.Value, providerUserId);
        return result == null
            ? ApiResponse<ProviderCategoriesResponse>.Fail("NOT_FOUND", "Provider not found in your business.")
            : ApiResponse<ProviderCategoriesResponse>.Ok(result);
    }

    public async Task<ApiResponse<ProviderCategoriesResponse>> SetProviderCategoriesAsync(
        Guid callerUserId, string role, Guid providerUserId, SetProviderCategoriesRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId is null)
            return ApiResponse<ProviderCategoriesResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "staff.manage"))
            return ApiResponse<ProviderCategoriesResponse>.Fail("FORBIDDEN", "You are not allowed to manage staff.");

        // The provider must be an owner or (active or inactive) staff member of
        // THIS business — a user id from elsewhere is indistinguishable from a
        // missing one.
        var membership = await _context.BusinessMemberships
            .FirstOrDefaultAsync(m => m.BusinessId == businessId.Value &&
                                      m.UserId == providerUserId &&
                                      (m.Role == BusinessMembershipRole.Owner || m.Role == BusinessMembershipRole.Staff));

        if (membership == null)
            return ApiResponse<ProviderCategoriesResponse>.Fail("NOT_FOUND", "Provider not found in your business.");

        // The "providing services" switch. Owners default to true; turning it off
        // removes the provider from the booking pool without touching categories
        // or history, and it can be turned back on at any time.
        if (request?.IsProvider is bool isProvider && membership.IsBookable != isProvider)
        {
            membership.IsBookable = isProvider;
            membership.UpdatedAt = DateTime.UtcNow;
        }

        // A null CategoryIds leaves the current set untouched (used when only the
        // provider switch is changing); an explicit array is the whole set.
        if (request?.CategoryIds is not null)
        {
            var requested = request.CategoryIds
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            if (requested.Count > 0)
            {
                var owned = await _context.ServiceCategories.AsNoTracking()
                    .Where(c => c.BusinessId == businessId.Value && requested.Contains(c.Id))
                    .Select(c => c.Id)
                    .ToListAsync();

                if (owned.Count != requested.Count)
                    return ApiResponse<ProviderCategoriesResponse>.Fail(
                        "CATEGORY_NOT_FOUND", "One or more categories do not belong to this business.");
            }

            var existing = await _context.StaffServiceCategories
                .Where(a => a.BusinessId == businessId.Value && a.StaffUserId == providerUserId)
                .ToListAsync();

            var existingIds = existing.Select(a => a.ServiceCategoryId).ToHashSet();

            // Removing a category withdraws eligibility for every service in it.
            var toRemove = existing.Where(a => !requested.Contains(a.ServiceCategoryId)).ToList();
            if (toRemove.Count > 0)
                _context.StaffServiceCategories.RemoveRange(toRemove);

            // Adding a category makes the provider eligible for every active service
            // in it — including ones created later — so no per-service rows are written.
            foreach (var categoryId in requested.Where(id => !existingIds.Contains(id)))
            {
                _context.StaffServiceCategories.Add(new StaffServiceCategory
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId.Value,
                    StaffUserId = providerUserId,
                    ServiceCategoryId = categoryId,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await _context.SaveChangesAsync();

        var result = await BuildProviderCategoriesAsync(businessId.Value, providerUserId);
        return ApiResponse<ProviderCategoriesResponse>.Ok(result!);
    }

    private async Task<ProviderCategoriesResponse?> BuildProviderCategoriesAsync(Guid businessId, Guid providerUserId)
    {
        var membership = await _context.BusinessMemberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.BusinessId == businessId &&
                                      m.UserId == providerUserId &&
                                      (m.Role == BusinessMembershipRole.Owner || m.Role == BusinessMembershipRole.Staff));

        if (membership == null) return null;

        var providerName = await _context.Users.AsNoTracking()
            .Where(u => u.Id == providerUserId)
            .Select(u => u.FullName)
            .FirstOrDefaultAsync() ?? "Provider";

        var assignedIds = await _context.StaffServiceCategories.AsNoTracking()
            .Where(a => a.BusinessId == businessId && a.StaffUserId == providerUserId)
            .Select(a => a.ServiceCategoryId)
            .ToListAsync();

        var categories = await _context.ServiceCategories.AsNoTracking()
            .Where(c => c.BusinessId == businessId)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.Description, c.IsActive })
            .ToListAsync();

        var serviceCounts = (await _context.ServiceCatalogItems.AsNoTracking()
                .Where(s => s.BusinessId == businessId && s.CategoryId != null)
                .GroupBy(s => s.CategoryId!.Value)
                .Select(g => new { CategoryId = g.Key, Count = g.Count() })
                .ToListAsync())
            .ToDictionary(x => x.CategoryId, x => x.Count);

        var assignedSet = assignedIds.ToHashSet();

        return new ProviderCategoriesResponse
        {
            ProviderUserId = providerUserId,
            ProviderName = providerName,
            IsOwner = membership.Role == BusinessMembershipRole.Owner,
            IsProvider = membership.IsBookable,
            EffectiveProviderModel = await _eligibility.ResolveModelAsync(businessId),
            Categories = categories
                .Where(c => c.IsActive || assignedSet.Contains(c.Id))
                .Select(c => new ProviderCategoryResponse
                {
                    Id = c.Id,
                    Name = c.Name,
                    Description = c.Description,
                    IsActive = c.IsActive,
                    ServiceCount = serviceCounts.TryGetValue(c.Id, out var count) ? count : 0
                })
                .ToList()
        };
    }

    public Task SeedDefaultCategoriesAsync(Guid businessId)
    {
        if (businessId == Guid.Empty) return Task.CompletedTask;
        return SeedDefaultCategoriesCoreAsync(businessId);
    }

    /// <summary>
    /// Adds the default categories when — and only when — the business has none.
    /// Never saves on its own so it can ride the caller's transaction.
    /// </summary>
    private async Task SeedDefaultCategoriesCoreAsync(Guid businessId)
    {
        var alreadyHasCategories = await _context.ServiceCategories
            .AnyAsync(c => c.BusinessId == businessId);
        if (alreadyHasCategories) return;

        var now = DateTime.UtcNow;
        var sortOrder = 0;
        foreach (var (name, description) in ServiceCategoryDefaults.All)
        {
            _context.ServiceCategories.Add(new ServiceCategory
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                Name = name,
                Description = description,
                SortOrder = sortOrder++,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
    }

    public async Task<int> BackfillDefaultCategoriesAsync()
    {
        var businessIds = await _context.Businesses
            .IgnoreQueryFilters()
            .Where(b => !b.IsDeleted && !_context.ServiceCategories.Any(c => c.BusinessId == b.Id))
            .Select(b => b.Id)
            .ToListAsync();

        if (businessIds.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var businessId in businessIds)
        {
            var sortOrder = 0;
            foreach (var (name, description) in ServiceCategoryDefaults.All)
            {
                _context.ServiceCategories.Add(new ServiceCategory
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    Name = name,
                    Description = description,
                    SortOrder = sortOrder++,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Seeded default service categories for {Count} business(es).", businessIds.Count);

        return businessIds.Count;
    }
}
