using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Business-scoped service categories.
///
/// <para><b>Tenant isolation.</b> The business is resolved server-side from the
/// caller active membership (or the active tenant host); a category id from
/// another business resolves to "not found" and is never distinguishable from a
/// missing one.</para>
///
/// <para><b>Deactivation vs deletion.</b> Deactivating keeps every service
/// assignment intact and only removes the group from public grouping; deleting
/// is permitted only while nothing references the category, and the database FK
/// (Restrict) is the race-safe backstop behind the service check.</para>
///
/// <para><b>Reordering</b> rewrites the whole order in one transaction from the
/// supplied id list, so the result is deterministic and cannot interleave with a
/// concurrent reorder.</para>
/// </summary>
public partial class ServiceCategoryService : IServiceCategoryService
{
    /// <summary>Module key the whole surface is gated on (mirrors the catalog).</summary>
    private const string ModuleKey = "serviceCatalog";

    private const int MaxNameLength = 80;
    private const int MaxDescriptionLength = 300;
    private const int MaxCategoriesPerBusiness = 100;

    private readonly ApplicationDbContext _context;
    private readonly IBusinessScopeResolver _scopeResolver;
    private readonly IPermissionService _permissions;
    private readonly IModuleEntitlementService _entitlements;
    private readonly ILogger<ServiceCategoryService> _logger;

    /// <summary>Active tenant (null on the platform root / in unit tests) - scoping only, never a grant.</summary>
    private readonly ITenantContext? _tenant;

    public ServiceCategoryService(
        ApplicationDbContext context,
        IBusinessScopeResolver scopeResolver,
        IPermissionService permissions,
        IModuleEntitlementService entitlements,
        ILogger<ServiceCategoryService> logger,
        ITenantContext? tenant = null)
    {
        _context = context;
        _scopeResolver = scopeResolver;
        _permissions = permissions;
        _entitlements = entitlements;
        _logger = logger;
        _tenant = tenant;
    }

    private Guid? TenantBusinessId => _tenant?.IsActive == true ? _tenant.BusinessId : null;

    private static bool IsRole(string value, string expected) =>
        string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The business the caller may administer. Owners resolve through their
    /// Owner membership, staff through their active membership; the active
    /// tenant always narrows the answer.
    /// </summary>
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
}

/// <summary>Owner read surface.</summary>
public partial class ServiceCategoryService
{
    /// <inheritdoc />
    public async Task<ApiResponse<List<ServiceCategoryResponse>>> GetCategoriesAsync(
        Guid callerUserId, string role, bool includeInactive = true)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<List<ServiceCategoryResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "serviceCategories.view"))
            return ApiResponse<List<ServiceCategoryResponse>>.Fail("FORBIDDEN", "You are not allowed to view service categories.");

        var query = _context.ServiceCategories.AsNoTracking().Where(c => c.BusinessId == businessId.Value);
        if (!includeInactive) query = query.Where(c => c.IsActive);

        var categories = await query
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync();

        return ApiResponse<List<ServiceCategoryResponse>>.Ok(await MapManyAsync(categories, businessId.Value));
    }

    /// <inheritdoc />
    public async Task<ApiResponse<ServiceCategoryResponse>> GetCategoryAsync(
        Guid callerUserId, string role, Guid categoryId)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<ServiceCategoryResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "serviceCategories.view"))
            return ApiResponse<ServiceCategoryResponse>.Fail("FORBIDDEN", "You are not allowed to view service categories.");

        var category = await _context.ServiceCategories.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == categoryId && c.BusinessId == businessId.Value);
        if (category == null)
            return ApiResponse<ServiceCategoryResponse>.Fail("NOT_FOUND", "Category not found.");

        var mapped = await MapManyAsync(new List<ServiceCategory> { category }, businessId.Value);
        return ApiResponse<ServiceCategoryResponse>.Ok(mapped[0]);
    }
}

/// <summary>Owner create surface.</summary>
public partial class ServiceCategoryService
{
    /// <inheritdoc />
    public async Task<ApiResponse<ServiceCategoryResponse>> CreateCategoryAsync(
        Guid callerUserId, string role, CreateServiceCategoryRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<ServiceCategoryResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "serviceCategories.manage"))
            return ApiResponse<ServiceCategoryResponse>.Fail("FORBIDDEN", "You are not allowed to manage service categories.");

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            return ApiResponse<ServiceCategoryResponse>.Fail("VALIDATION_ERROR", "A category name is required.");
        if (name.Length > MaxNameLength)
            return ApiResponse<ServiceCategoryResponse>.Fail("VALIDATION_ERROR", $"A category name may be at most {MaxNameLength} characters.");

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description != null && description.Length > MaxDescriptionLength)
            return ApiResponse<ServiceCategoryResponse>.Fail("VALIDATION_ERROR", $"A description may be at most {MaxDescriptionLength} characters.");

        var existing = await _context.ServiceCategories
            .Where(c => c.BusinessId == businessId.Value)
            .Select(c => new { c.Name, c.SortOrder })
            .ToListAsync();

        // Deliberate duplicate handling: names are unique per business,
        // case-insensitively, with a clear error rather than a silent second row.
        if (existing.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            return ApiResponse<ServiceCategoryResponse>.Fail(
                "CATEGORY_NAME_EXISTS", "A category with this name already exists.");

        if (existing.Count >= MaxCategoriesPerBusiness)
            return ApiResponse<ServiceCategoryResponse>.Fail(
                "CATEGORY_LIMIT_REACHED", $"A business may have at most {MaxCategoriesPerBusiness} categories.");

        var now = DateTime.UtcNow;
        var category = new ServiceCategory
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId.Value,
            Name = name,
            Description = description,
            // An omitted sort order appends to the end, which is what an owner
            // adding a category expects.
            SortOrder = request.SortOrder ?? (existing.Count == 0 ? 0 : existing.Max(c => c.SortOrder) + 1),
            IsActive = request.IsActive ?? true,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.ServiceCategories.Add(category);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // The unique (business_id, name) index lost a race against a
            // concurrent create with the same name.
            _context.Entry(category).State = EntityState.Detached;
            return ApiResponse<ServiceCategoryResponse>.Fail(
                "CATEGORY_NAME_EXISTS", "A category with this name already exists.");
        }

        _logger.LogInformation(
            "Service category {CategoryId} created for business {BusinessId} by {UserId}.",
            category.Id, businessId.Value, callerUserId);

        return ApiResponse<ServiceCategoryResponse>.Ok(new ServiceCategoryResponse
        {
            Id = category.Id,
            BusinessId = category.BusinessId,
            Name = category.Name,
            Description = category.Description,
            SortOrder = category.SortOrder,
            IsActive = category.IsActive,
            ServiceCount = 0,
            CanDelete = true,
            CreatedAt = category.CreatedAt,
            UpdatedAt = category.UpdatedAt
        });
    }
}

/// <summary>Owner update surface (including activation/deactivation).</summary>
public partial class ServiceCategoryService
{
    /// <inheritdoc />
    public async Task<ApiResponse<ServiceCategoryResponse>> UpdateCategoryAsync(
        Guid callerUserId, string role, Guid categoryId, UpdateServiceCategoryRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<ServiceCategoryResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "serviceCategories.manage"))
            return ApiResponse<ServiceCategoryResponse>.Fail("FORBIDDEN", "You are not allowed to manage service categories.");

        var category = await _context.ServiceCategories
            .FirstOrDefaultAsync(c => c.Id == categoryId && c.BusinessId == businessId.Value);
        if (category == null)
            return ApiResponse<ServiceCategoryResponse>.Fail("NOT_FOUND", "Category not found.");

        if (request.Name != null)
        {
            var name = request.Name.Trim();
            if (name.Length == 0)
                return ApiResponse<ServiceCategoryResponse>.Fail("VALIDATION_ERROR", "A category name cannot be blank.");
            if (name.Length > MaxNameLength)
                return ApiResponse<ServiceCategoryResponse>.Fail("VALIDATION_ERROR", $"A category name may be at most {MaxNameLength} characters.");

            if (!string.Equals(name, category.Name, StringComparison.Ordinal))
            {
                var clash = await _context.ServiceCategories.AsNoTracking().AnyAsync(c =>
                    c.BusinessId == businessId.Value &&
                    c.Id != category.Id &&
                    c.Name.ToLower() == name.ToLower());
                if (clash)
                    return ApiResponse<ServiceCategoryResponse>.Fail(
                        "CATEGORY_NAME_EXISTS", "A category with this name already exists.");

                category.Name = name;
            }
        }

        if (request.Description != null)
        {
            var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            if (description != null && description.Length > MaxDescriptionLength)
                return ApiResponse<ServiceCategoryResponse>.Fail("VALIDATION_ERROR", $"A description may be at most {MaxDescriptionLength} characters.");
            category.Description = description;
        }

        // Deactivation never touches service assignments: the group is simply
        // hidden from public grouping while every service stays bookable.
        if (request.IsActive.HasValue) category.IsActive = request.IsActive.Value;

        category.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return ApiResponse<ServiceCategoryResponse>.Fail(
                "CATEGORY_NAME_EXISTS", "A category with this name already exists.");
        }

        _logger.LogInformation(
            "Service category {CategoryId} updated for business {BusinessId} by {UserId}.",
            category.Id, businessId.Value, callerUserId);

        var mapped = await MapManyAsync(new List<ServiceCategory> { category }, businessId.Value);
        return ApiResponse<ServiceCategoryResponse>.Ok(mapped[0]);
    }
}

/// <summary>Owner reordering.</summary>
public partial class ServiceCategoryService
{
    /// <inheritdoc />
    public async Task<ApiResponse<List<ServiceCategoryResponse>>> ReorderCategoriesAsync(
        Guid callerUserId, string role, ReorderServiceCategoriesRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<List<ServiceCategoryResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "serviceCategories.manage"))
            return ApiResponse<List<ServiceCategoryResponse>>.Fail("FORBIDDEN", "You are not allowed to manage service categories.");

        var orderedIds = request.OrderedIds ?? Array.Empty<Guid>();
        if (orderedIds.Length == 0)
            return ApiResponse<List<ServiceCategoryResponse>>.Fail("VALIDATION_ERROR", "orderedIds must not be empty.");
        if (orderedIds.Distinct().Count() != orderedIds.Length)
            return ApiResponse<List<ServiceCategoryResponse>>.Fail("VALIDATION_ERROR", "orderedIds must not contain duplicates.");

        var categories = await _context.ServiceCategories
            .Where(c => c.BusinessId == businessId.Value)
            .ToListAsync();

        var byId = categories.ToDictionary(c => c.Id);

        // Every id must belong to THIS business: an unknown or foreign id is a
        // hard error rather than a silently ignored entry, so a client can never
        // believe it reordered something it did not.
        if (orderedIds.Any(id => !byId.ContainsKey(id)))
            return ApiResponse<List<ServiceCategoryResponse>>.Fail(
                "NOT_FOUND", "One or more categories do not belong to this business.");

        // Reordering may be partial; unspecified categories keep their relative
        // order AFTER the explicit ones, so nothing is dropped or given a
        // colliding position.
        var now = DateTime.UtcNow;
        var position = 0;

        foreach (var id in orderedIds)
        {
            var category = byId[id];
            if (category.SortOrder != position)
            {
                category.SortOrder = position;
                category.UpdatedAt = now;
            }
            position++;
        }

        foreach (var category in categories
                     .Where(c => !orderedIds.Contains(c.Id))
                     .OrderBy(c => c.SortOrder).ThenBy(c => c.Name))
        {
            if (category.SortOrder != position)
            {
                category.SortOrder = position;
                category.UpdatedAt = now;
            }
            position++;
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Service categories reordered for business {BusinessId} by {UserId} ({Count} categories).",
            businessId.Value, callerUserId, categories.Count);

        return await GetCategoriesAsync(callerUserId, role);
    }
}

/// <summary>Owner deletion and category-scoped service reads.</summary>
public partial class ServiceCategoryService
{
    /// <inheritdoc />
    public async Task<ApiResponse<bool>> DeleteCategoryAsync(Guid callerUserId, string role, Guid categoryId)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "serviceCategories.manage"))
            return ApiResponse<bool>.Fail("FORBIDDEN", "You are not allowed to manage service categories.");

        var category = await _context.ServiceCategories
            .FirstOrDefaultAsync(c => c.Id == categoryId && c.BusinessId == businessId.Value);
        if (category == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "Category not found.");

        var inUse = await _context.ServiceCatalogItems.AsNoTracking()
            .CountAsync(s => s.BusinessId == businessId.Value && s.CategoryId == categoryId);
        if (inUse > 0)
            return ApiResponse<bool>.Fail(
                "CATEGORY_IN_USE",
                $"This category is used by {inUse} service(s). Move or unassign them, or deactivate the category instead.");

        _context.ServiceCategories.Remove(category);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // A service was filed under the category between the check and the
            // delete; the Restrict FK is the authoritative guard.
            return ApiResponse<bool>.Fail(
                "CATEGORY_IN_USE", "This category is still referenced by a service and cannot be deleted.");
        }

        _logger.LogInformation(
            "Service category {CategoryId} deleted for business {BusinessId} by {UserId}.",
            categoryId, businessId.Value, callerUserId);

        return ApiResponse<bool>.Ok(true);
    }

    /// <inheritdoc />
    public async Task<ApiResponse<List<ServiceCatalogItemResponse>>> GetCategoryServicesAsync(
        Guid callerUserId, string role, Guid categoryId)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<List<ServiceCatalogItemResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "serviceCategories.view"))
            return ApiResponse<List<ServiceCatalogItemResponse>>.Fail("FORBIDDEN", "You are not allowed to view service categories.");

        var exists = await _context.ServiceCategories.AsNoTracking()
            .AnyAsync(c => c.Id == categoryId && c.BusinessId == businessId.Value);
        if (!exists)
            return ApiResponse<List<ServiceCatalogItemResponse>>.Fail("NOT_FOUND", "Category not found.");

        var services = await _context.ServiceCatalogItems.AsNoTracking()
            .Where(s => s.BusinessId == businessId.Value && s.CategoryId == categoryId)
            .OrderBy(s => s.Name)
            .ToListAsync();

        return ApiResponse<List<ServiceCatalogItemResponse>>.Ok(services.Select(MapService).ToList());
    }
}

/// <summary>Business-type starter categories.</summary>
public partial class ServiceCategoryService
{
    /// <inheritdoc />
    public async Task<ApiResponse<List<ServiceCategoryResponse>>> ApplyStarterCategoriesAsync(
        Guid callerUserId, string role)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<List<ServiceCategoryResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "serviceCategories.manage"))
            return ApiResponse<List<ServiceCategoryResponse>>.Fail("FORBIDDEN", "You are not allowed to manage service categories.");

        var business = await _context.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == businessId.Value);
        if (business == null)
            return ApiResponse<List<ServiceCategoryResponse>>.Fail("NOT_FOUND", "Business not found.");

        var template = ServiceCategoryTemplates.For(business.Category);
        if (template.Count == 0)
            return ApiResponse<List<ServiceCategoryResponse>>.Fail(
                "NO_TEMPLATE",
                $"No starter categories are defined for the business type {business.Category}.");

        var created = await ApplyStarterCategoriesCoreAsync(
            _context, businessId.Value, business.Category, _logger);
        if (created > 0) await _context.SaveChangesAsync();

        return await GetCategoriesAsync(callerUserId, role);
    }

    /// <summary>
    /// Copies the starter categories for a declared business type, but ONLY when
    /// the business has no categories of its own.
    ///
    /// <para>This guard is what makes the operation safe to call from onboarding
    /// and from repeatable seed steps: a business that has curated (or renamed,
    /// or deleted) its categories is never re-seeded, so nothing is duplicated
    /// or overwritten. Returns the number of categories added to the change
    /// tracker (the caller decides when to save).</para>
    /// </summary>
    public static async Task<int> ApplyStarterCategoriesCoreAsync(
        ApplicationDbContext context,
        Guid businessId,
        string? businessType,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var template = ServiceCategoryTemplates.For(businessType);
        if (template.Count == 0) return 0;

        var alreadyHasCategories = await context.ServiceCategories
            .AsNoTracking()
            .AnyAsync(c => c.BusinessId == businessId, cancellationToken);
        if (alreadyHasCategories) return 0;

        var now = DateTime.UtcNow;
        var position = 0;
        foreach (var starter in template)
        {
            context.ServiceCategories.Add(new ServiceCategory
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                Name = starter.Name,
                Description = starter.Description,
                SortOrder = position++,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        logger?.LogInformation(
            "Seeded {Count} starter service categories for business {BusinessId} (type {BusinessType}).",
            template.Count, businessId, businessType);

        return template.Count;
    }
}

/// <summary>Public category-grouped catalogue.</summary>
public partial class ServiceCategoryService
{
    /// <inheritdoc />
    public async Task<ApiResponse<PublicServiceCatalogResponse>> GetPublicCatalogueAsync(Guid businessId)
    {
        // Tenant narrowing: on a tenant host only the active tenant catalogue is
        // public, exactly as the flat public service list already does.
        if (TenantBusinessId is Guid activeTenantId && businessId != activeTenantId)
            return ApiResponse<PublicServiceCatalogResponse>.Fail("NOT_FOUND", "Business not found.");

        var business = await _context.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == businessId && !b.IsDeleted);
        if (business == null)
            return ApiResponse<PublicServiceCatalogResponse>.Fail("NOT_FOUND", "Business not found.");

        // The public path enforces the entitlement itself: [RequireModule] cannot,
        // because this endpoint is anonymous and has no caller business to resolve.
        if (!await _entitlements.IsModuleEnabledAsync(businessId, ModuleKey))
        {
            _logger.LogInformation(
                "Public catalogue request for business {BusinessId} refused: module {ModuleKey} is not enabled.",
                businessId, ModuleKey);
            return ApiResponse<PublicServiceCatalogResponse>.Fail(
                "MODULE_DISABLED", $"The {ModuleKey} module is not enabled for this business.");
        }

        // Published services only: active AND showcased. A service filed under a
        // deactivated category is still bookable, so it lands in the
        // uncategorised bucket rather than silently disappearing from the storefront.
        var services = await _context.ServiceCatalogItems.AsNoTracking()
            .Where(s => s.BusinessId == businessId && s.IsActive && s.Showcase)
            .OrderBy(s => s.Name)
            .ToListAsync();

        var categories = await _context.ServiceCategories.AsNoTracking()
            .Where(c => c.BusinessId == businessId && c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync();

        var activeCategoryIds = categories.Select(c => c.Id).ToHashSet();

        return ApiResponse<PublicServiceCatalogResponse>.Ok(new PublicServiceCatalogResponse
        {
            BusinessId = businessId,
            BusinessName = business.Name,
            Categories = categories.Select(c => new PublicServiceCategoryGroupResponse
            {
                CategoryId = c.Id,
                Name = c.Name,
                Description = c.Description,
                SortOrder = c.SortOrder,
                Services = services.Where(s => s.CategoryId == c.Id).Select(MapService).ToList()
            }).ToList(),
            Uncategorized = services
                .Where(s => s.CategoryId == null || !activeCategoryIds.Contains(s.CategoryId.Value))
                .Select(MapService)
                .ToList()
        });
    }
}

/// <summary>Mapping helpers.</summary>
public partial class ServiceCategoryService
{
    private async Task<List<ServiceCategoryResponse>> MapManyAsync(
        List<ServiceCategory> categories, Guid businessId)
    {
        if (categories.Count == 0) return new List<ServiceCategoryResponse>();

        var ids = categories.Select(c => c.Id).ToList();

        // One grouped count query instead of one query per category (no N+1).
        var counts = (await _context.ServiceCatalogItems.AsNoTracking()
                .Where(s => s.BusinessId == businessId && s.CategoryId.HasValue && ids.Contains(s.CategoryId!.Value))
                .GroupBy(s => s.CategoryId!.Value)
                .Select(g => new { CategoryId = g.Key, Count = g.Count() })
                .ToListAsync())
            .ToDictionary(x => x.CategoryId, x => x.Count);

        return categories.Select(c =>
        {
            var count = counts.TryGetValue(c.Id, out var value) ? value : 0;
            return new ServiceCategoryResponse
            {
                Id = c.Id,
                BusinessId = c.BusinessId,
                Name = c.Name,
                Description = c.Description,
                SortOrder = c.SortOrder,
                IsActive = c.IsActive,
                ServiceCount = count,
                CanDelete = count == 0,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt
            };
        }).ToList();
    }

    /// <summary>
    /// The public projection of a catalogue service. Deliberately narrow: no
    /// internal flags beyond what a storefront needs to render and price a service.
    /// </summary>
    internal static ServiceCatalogItemResponse MapService(ServiceCatalogItem s) => new()
    {
        Id = s.Id,
        BusinessId = s.BusinessId,
        Name = s.Name,
        Description = s.Description,
        DurationMinutes = s.DurationMinutes,
        Price = s.Price ?? 0,
        IsActive = s.IsActive,
        Showcase = s.Showcase,
        CategoryId = s.CategoryId,
        CreatedAt = s.CreatedAt
    };
}
