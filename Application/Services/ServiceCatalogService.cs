using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Media;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Backs the ServiceCatalog endpoints and the public per-business active list.
/// Owner-scoped methods resolve the business from the ownerUserId and assert ownership.
/// </summary>
public class ServiceCatalogService : IServiceCatalogService
{
    private sealed record ServiceImageProjection(Guid Id, Guid? BusinessId, string Purpose, MediaStatus Status, MediaVisibility Visibility, string VariantsJson, int? Width, int? Height);

    /// <summary>
    /// Module key the whole controller is gated on. Repeated here (rather than
    /// referenced through a constant) so the PUBLIC path below can enforce the
    /// same gate <c>[RequireModule]</c> enforces on the owner paths — see
    /// <see cref="GetServicesForBusinessAsync"/>.
    /// </summary>
    private const string ModuleKey = "serviceCatalog";

    /// <summary>
    /// The module that owns staff PROFILE data (name, job title, bio,
    /// specialities, photo, public/bookable flags).
    ///
    /// <para>The public roster is staff profile data, so it is gated by this
    /// module as well as by <see cref="ModuleKey"/> — the same pair
    /// <c>StorefrontSearchService</c> already uses for its staff results. Without
    /// it, switching the <c>staff</c> module off in the platform admin console
    /// would hide the owner's staff console while leaving every staff member's
    /// profile publicly readable at <c>GET /v1/services/{id}/staff</c>.</para>
    /// </summary>
    private const string StaffModuleKey = "staff";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ServiceCatalogService> _logger;
    private readonly IModuleEntitlementService _moduleEntitlementService;

    /// <summary>
    /// Direct context access for DB-first filtered/paged reads. The repository
    /// abstraction has no queryable surface, and materialising the whole
    /// catalogue to filter in memory would defeat pagination.
    /// </summary>
    private readonly ApplicationDbContext _context;

    /// <summary>Active tenant (null on the platform root) — scoping only, never a grant.</summary>
    private readonly ITenantContext? _tenant;

    /// <summary>
    /// Service-image ceiling (<c>MediaStorage:MaxServiceImages</c>). The same
    /// option the media module enforces on a single attach, so the ordered
    /// image-set write cannot be used to exceed the cap.
    /// </summary>
    private readonly MediaStorageOptions _mediaOptions;

    /// <summary>
    /// The single provider-eligibility resolver. Optional so existing
    /// construction sites (tests) keep compiling; when absent a resolver over
    /// the same DbContext is used, so behaviour — category eligibility, owner
    /// fallback, solo mode — is identical.
    /// </summary>
    private readonly IProviderEligibilityService _eligibility;

    public ServiceCatalogService(
        IUnitOfWork unitOfWork,
        ILogger<ServiceCatalogService> logger,
        IModuleEntitlementService moduleEntitlementService,
        ApplicationDbContext context,
        ITenantContext? tenant = null,
        IOptions<MediaStorageOptions>? mediaOptions = null,
        IProviderEligibilityService? providerEligibility = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _moduleEntitlementService = moduleEntitlementService;
        _context = context;
        _tenant = tenant;
        _mediaOptions = mediaOptions?.Value ?? new MediaStorageOptions();
        _eligibility = providerEligibility ?? new ProviderEligibilityService(context, moduleEntitlementService);
    }

    /// <summary>Active tenant business id, or null on the platform root (legacy scoping).</summary>
    private Guid? TenantBusinessId => _tenant?.IsActive == true ? _tenant.BusinessId : null;

    private Task<Business?> ResolveOwnerBusinessAsync(Guid ownerUserId) =>
        _unitOfWork.Businesses.FirstOrDefaultNoTrackingAsync(b => b.OwnerId == ownerUserId && !b.IsDeleted &&
            (TenantBusinessId == null || b.Id == TenantBusinessId));

    public async Task<ApiResponse<List<ServiceCatalogItemResponse>>> GetAdminServicesAsync(Guid? businessId)
    {
        var businesses = await _unitOfWork.Businesses.FindNoTrackingAsync(b => !b.IsDeleted &&
            (!businessId.HasValue || b.Id == businessId.Value) &&
            (TenantBusinessId == null || b.Id == TenantBusinessId));
        var names = businesses.ToDictionary(b => b.Id, b => b.Name);
        var businessIds = names.Keys.ToArray();
        var services = await _unitOfWork.ServiceCatalogItems.FindNoTrackingAsync(s => businessIds.Contains(s.BusinessId));
        var responses = await MapManyAsync(services.OrderBy(s => s.BusinessId).ThenBy(s => s.Name));
        foreach (var response in responses) response.BusinessName = names[response.BusinessId];
        return ApiResponse<List<ServiceCatalogItemResponse>>.Ok(responses);
    }

    public async Task<ApiResponse<ServiceCatalogItemResponse>> CreateForBusinessAsync(Guid businessId, CreateServiceRequest request)
    {
        var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.Id == businessId && !b.IsDeleted &&
            (TenantBusinessId == null || b.Id == TenantBusinessId));
        return business == null
            ? ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "Business not found.")
            : await CreateForBusinessCoreAsync(business.Id, request, enforceCategory: false);
    }

    public async Task<ApiResponse<ServiceCatalogItemResponse>> UpdateForBusinessAsync(Guid businessId, Guid serviceId, UpdateServiceRequest request)
    {
        var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.Id == businessId && !b.IsDeleted &&
            (TenantBusinessId == null || b.Id == TenantBusinessId));
        return business == null
            ? ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "Business not found.")
            : await UpdateForBusinessCoreAsync(business.Id, serviceId, request, enforceCategory: false);
    }

    public async Task<ApiResponse<bool>> DeleteForBusinessAsync(Guid businessId, Guid serviceId)
    {
        var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.Id == businessId && !b.IsDeleted &&
            (TenantBusinessId == null || b.Id == TenantBusinessId));
        return business == null
            ? ApiResponse<bool>.Fail("NOT_FOUND", "Business not found.")
            : await DeleteForBusinessCoreAsync(business.Id, serviceId);
    }

    public async Task<ApiResponse<ServiceCatalogItemResponse>> GetPublicServiceAsync(Guid businessId, Guid serviceId)
    {
        if (TenantBusinessId is Guid activeTenantId && businessId != activeTenantId)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "Business not found.");

        var business = await _unitOfWork.Businesses
            .FirstOrDefaultNoTrackingAsync(b => b.Id == businessId && !b.IsDeleted);
        if (business == null)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "Business not found.");

        if (!await _moduleEntitlementService.IsModuleEnabledAsync(businessId, ModuleKey))
        {
            _logger.LogInformation(
                "Public service detail request for business {BusinessId} refused: module '{ModuleKey}' is not enabled.",
                businessId, ModuleKey);
            return ApiResponse<ServiceCatalogItemResponse>.Fail(
                "MODULE_DISABLED",
                $"The '{ModuleKey}' module is not enabled for this business.");
        }

        var service = await _unitOfWork.ServiceCatalogItems.FirstOrDefaultAsync(s =>
            s.Id == serviceId && s.BusinessId == businessId && s.IsActive && s.Showcase);
        return service == null
            ? ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "Service not found.")
            : ApiResponse<ServiceCatalogItemResponse>.Ok(await MapAsync(service));
    }

    public async Task<ApiResponse<List<ServiceCatalogItemResponse>>> GetServicesForBusinessAsync(Guid businessId)
    {
        // Tenant narrowing: on a tenant host only the active tenant's catalogue
        // is public, exactly as GetPublicProfileAsync already does. Without it a
        // tenant subdomain could enumerate every business id on the platform.
        if (TenantBusinessId is Guid activeTenantId && businessId != activeTenantId)
            return ApiResponse<List<ServiceCatalogItemResponse>>.Fail("NOT_FOUND", "Business not found.");

        var business = await _unitOfWork.Businesses
            .FirstOrDefaultAsync(b => b.Id == businessId && !b.IsDeleted);
        if (business == null)
            return ApiResponse<List<ServiceCatalogItemResponse>>.Fail("NOT_FOUND", "Business not found.");

        /* Module entitlement, enforced on the PUBLIC path too.

           [RequireModule] deliberately skips any endpoint marked
           [AllowAnonymous] — it resolves the caller's business from the token,
           which an anonymous visitor does not have — so this endpoint would
           otherwise serve a catalogue for a business that never bought the
           serviceCatalog module. It is the same effective-entitlement lookup
           the owner paths are gated by, applied to the business the request
           NAMED rather than the one the caller belongs to; that difference is
           the only reason the gate cannot simply be left to the filter.

           MODULE_DISABLED (not NOT_FOUND) so the storefront can tell "this
           business does not offer services" from "this business does not
           exist", and hide the tab instead of rendering a dead link. */
        if (!await _moduleEntitlementService.IsModuleEnabledAsync(businessId, ModuleKey))
        {
            _logger.LogInformation(
                "Public catalogue request for business {BusinessId} refused: module '{ModuleKey}' is not enabled.",
                businessId, ModuleKey);

            return ApiResponse<List<ServiceCatalogItemResponse>>.Fail(
                "MODULE_DISABLED",
                $"The '{ModuleKey}' module is not enabled for this business.");
        }

        // Active AND showcased — the two independent switches the owner
        // controls. Filtered HERE rather than client-side so a storefront
        // cannot learn about an unadvertised service by reading the response.
        var services = await _unitOfWork.ServiceCatalogItems
            .FindNoTrackingAsync(s => s.BusinessId == businessId && s.IsActive && s.Showcase);

        return ApiResponse<List<ServiceCatalogItemResponse>>.Ok(
            await MapManyAsync(services.OrderBy(s => s.CreatedAt)));
    }

    public async Task<ApiResponse<List<ServiceCatalogItemResponse>>> GetMyServicesAsync(Guid ownerUserId)
    {
        var business = await ResolveOwnerBusinessAsync(ownerUserId);
        if (business == null)
            return ApiResponse<List<ServiceCatalogItemResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        var services = await _unitOfWork.ServiceCatalogItems
            .FindNoTrackingAsync(s => s.BusinessId == business.Id);

        return ApiResponse<List<ServiceCatalogItemResponse>>.Ok(
            await MapManyAsync(services.OrderBy(s => s.CreatedAt)));
    }

    /// <inheritdoc />
    public async Task<ApiResponse<PaginatedResponse<ServiceCatalogItemResponse>>> QueryMyServicesAsync(
        Guid ownerUserId, ServiceCatalogQueryRequest request)
    {
        var business = await ResolveOwnerBusinessAsync(ownerUserId);
        if (business == null)
            return ApiResponse<PaginatedResponse<ServiceCatalogItemResponse>>.Fail(
                "NOT_FOUND", "No business found for this account.");

        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);

        var query = _context.ServiceCatalogItems.AsNoTracking()
            .Where(s => s.BusinessId == business.Id);

        if (request.Uncategorized)
            query = query.Where(s => s.CategoryId == null);
        else if (request.CategoryId.HasValue)
            query = query.Where(s => s.CategoryId == request.CategoryId.Value);

        if (request.IsActive.HasValue)
            query = query.Where(s => s.IsActive == request.IsActive.Value);

        // Status is resolved through the shared vocabulary: `all` (or an unknown
        // value) simply means "no status filter", never an empty list. Several
        // statuses may arrive comma-separated ("active,paused") so the list's
        // multi-select filter stays a single server-side request.
        var statusFlags = (request.Status ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ServiceCatalogStatuses.ToFlags)
            .Where(flags => flags.HasValue)
            .Select(flags => flags!.Value)
            .Distinct()
            .ToList();

        if (statusFlags.Count > 0)
        {
            var includeActive = statusFlags.Exists(flags => flags.IsActive && !flags.IsArchived);
            var includePaused = statusFlags.Exists(flags => !flags.IsActive && !flags.IsArchived);
            var includeArchived = statusFlags.Exists(flags => flags.IsArchived);

            if (!(includeActive && includePaused && includeArchived))
            {
                query = query.Where(service =>
                    (includeActive && service.IsActive && !service.IsArchived) ||
                    (includePaused && !service.IsActive && !service.IsArchived) ||
                    (includeArchived && service.IsArchived));
            }
        }

        if (request.MinPrice.HasValue)
            query = query.Where(s => (s.Price ?? 0) >= request.MinPrice.Value);
        if (request.MaxPrice.HasValue)
            query = query.Where(s => (s.Price ?? 0) <= request.MaxPrice.Value);

        if (request.MinDuration.HasValue)
            query = query.Where(s => s.DurationMinutes >= request.MinDuration.Value);
        if (request.MaxDuration.HasValue)
            query = query.Where(s => s.DurationMinutes <= request.MaxDuration.Value);

        if (request.Showcase.HasValue)
            query = query.Where(s => s.Showcase == request.Showcase.Value);

        if (request.HasImages == true)
            query = query.Where(s => _context.ServiceMedia.Any(link => link.ServiceCatalogItemId == s.Id));

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(s => EF.Functions.ILike(s.Name, $"%{search}%") ||
                                     (s.Description != null && EF.Functions.ILike(s.Description, $"%{search}%")));
        }

        var total = await query.CountAsync();

        // Deterministic ordering: an explicit sort, else category order then
        // name, so a paginated list never reshuffles between pages.
        var descending = string.Equals(request.SortDirection?.Trim(), "desc", StringComparison.OrdinalIgnoreCase);
        query = (request.SortBy?.Trim().ToLowerInvariant()) switch
        {
            "name" => descending ? query.OrderByDescending(s => s.Name) : query.OrderBy(s => s.Name),
            // Price and duration sorts carry a name tie-break so equal values
            // never reshuffle between pages.
            "price" => descending
                ? query.OrderByDescending(s => s.Price).ThenBy(s => s.Name)
                : query.OrderBy(s => s.Price).ThenBy(s => s.Name),
            "duration" => descending
                ? query.OrderByDescending(s => s.DurationMinutes).ThenBy(s => s.Name)
                : query.OrderBy(s => s.DurationMinutes).ThenBy(s => s.Name),
            "createdat" => descending ? query.OrderByDescending(s => s.CreatedAt) : query.OrderBy(s => s.CreatedAt),
            // "Newest first" is the list's default sort, so it is expressible
            // without also sending a direction.
            "newest" => query.OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Name),
            "oldest" => query.OrderBy(s => s.CreatedAt).ThenBy(s => s.Name),
            _ => query.OrderBy(s => s.CategoryId == null).ThenBy(s => s.Name)
        };

        var services = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return ApiResponse<PaginatedResponse<ServiceCatalogItemResponse>>.Ok(
            new PaginatedResponse<ServiceCatalogItemResponse>
            {
                Items = await MapManyAsync(services),
                TotalCount = total,
                Page = page,
                PageSize = pageSize
            });
    }

    public async Task<ApiResponse<ServiceCatalogItemResponse>> GetServiceAsync(Guid ownerUserId, Guid serviceId)
    {
        var business = await ResolveOwnerBusinessAsync(ownerUserId);
        if (business == null)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var service = await _unitOfWork.ServiceCatalogItems.FirstOrDefaultNoTrackingAsync(s => s.Id == serviceId);
        if (service == null)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "Service not found.");
        if (service.BusinessId != business.Id)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("FORBIDDEN", "Not authorized to access this service.");

        return ApiResponse<ServiceCatalogItemResponse>.Ok(await MapAsync(service));
    }

    public async Task<ApiResponse<ServiceCatalogItemResponse>> CreateServiceAsync(Guid ownerUserId, CreateServiceRequest request)
    {
        var business = await ResolveOwnerBusinessAsync(ownerUserId);
        if (business == null)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "No business found for this account.");

        return await CreateForBusinessCoreAsync(business.Id, request);
    }

    private async Task<ApiResponse<ServiceCatalogItemResponse>> CreateForBusinessCoreAsync(Guid businessId, CreateServiceRequest request, bool enforceCategory = true)
    {

        var categoryError = await ValidateCategoryAsync(businessId, request.CategoryId);
        if (categoryError != null)
            return ApiResponse<ServiceCatalogItemResponse>.Fail(categoryError.Value.Code, categoryError.Value.Message);

        var categoryRequired = await RequireCategoryAsync(businessId, request.CategoryId, enforceCategory);
        if (categoryRequired != null)
            return ApiResponse<ServiceCatalogItemResponse>.Fail(categoryRequired.Value.Code, categoryRequired.Value.Message);

        var service = new ServiceCatalogItem
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            DurationMinutes = request.DurationMinutes,
            Price = request.Price,
            IsActive = true,
            Showcase = request.Showcase ?? true,
            CategoryId = request.CategoryId,
            CreatedAt = DateTime.UtcNow
        };

        await _unitOfWork.ServiceCatalogItems.AddAsync(service);
        await _unitOfWork.SaveChangesAsync();

        return ApiResponse<ServiceCatalogItemResponse>.Ok(await MapAsync(service));
    }

    /// <summary>
    /// A service may only be filed under a category of its OWN business, so a
    /// category id from another tenant can never be attached (IDOR guard).
    /// Returns null when the assignment is valid.
    /// </summary>
    private async Task<(string Code, string Message)?> ValidateCategoryAsync(Guid businessId, Guid? categoryId)
    {
        if (categoryId is not Guid id) return null;

        var exists = await _unitOfWork.ServiceCategories
            .AnyAsync(c => c.Id == id && c.BusinessId == businessId);

        return exists
            ? null
            : ("CATEGORY_NOT_FOUND", "The category does not belong to this business.");
    }

    /// <summary>
    /// In a multi-staff business (with the Staff module enabled) every service
    /// must be filed under a category, because category membership is what makes
    /// providers eligible. Solo and staff-module-disabled businesses may still
    /// leave a service uncategorised — their provider model does not depend on it.
    /// Returns null when the assignment is acceptable.
    /// </summary>
    private async Task<(string Code, string Message)?> RequireCategoryAsync(Guid businessId, Guid? categoryId, bool enforce = true)
    {
        if (!enforce) return null;
        if (categoryId is Guid) return null;

        // Nothing to choose yet — never block a business before it has any
        // categories (the seed/backfill provides them, but a brand-new tenant
        // must not be un-creatable in the window before that runs).
        var hasCategories = await _context.ServiceCategories
            .AnyAsync(c => c.BusinessId == businessId);
        if (!hasCategories) return null;

        var operatingMode = await _context.Businesses.AsNoTracking()
            .Where(b => b.Id == businessId)
            .Select(b => b.OperatingMode)
            .FirstOrDefaultAsync();

        if (Domain.Entities.BusinessOperatingModes.IsSolo(operatingMode)) return null;

        var staffEntitled = await _moduleEntitlementService
            .IsModuleEnabledAsync(businessId, ProviderEligibilityService.StaffModuleKey);
        if (!staffEntitled) return null;

        return ("CATEGORY_REQUIRED",
            "Choose a category for this service — providers are made eligible for a service through its category.");
    }

    public async Task<ApiResponse<ServiceCatalogItemResponse>> UpdateServiceAsync(Guid ownerUserId, Guid serviceId, UpdateServiceRequest request)
    {
        var business = await ResolveOwnerBusinessAsync(ownerUserId);
        if (business == null)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "No business found for this account.");

        return await UpdateForBusinessCoreAsync(business.Id, serviceId, request);
    }

    private async Task<ApiResponse<ServiceCatalogItemResponse>> UpdateForBusinessCoreAsync(Guid businessId, Guid serviceId, UpdateServiceRequest request, bool enforceCategory = true)
    {

        var service = await _unitOfWork.ServiceCatalogItems.FirstOrDefaultAsync(s => s.Id == serviceId);
        if (service == null)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "Service not found.");
        if (service.BusinessId != businessId)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("FORBIDDEN", "Not authorized to access this service.");

        if (!string.IsNullOrWhiteSpace(request.Name))
            service.Name = request.Name.Trim();
        if (request.Description != null)
            service.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (request.DurationMinutes.HasValue)
            service.DurationMinutes = request.DurationMinutes.Value;
        if (request.Price.HasValue)
            service.Price = request.Price.Value;
        if (request.IsActive.HasValue)
            service.IsActive = request.IsActive.Value;
        if (request.Showcase.HasValue)
            service.Showcase = request.Showcase.Value;

        // Archive/restore keep the two flags consistent: a filed-away service is
        // never bookable, and restoring it puts it back on sale. Sending both
        // `isArchived` and `isActive` in one patch is allowed — the archive flag
        // wins, so the row can never end up archived AND active.
        if (request.IsArchived.HasValue)
        {
            service.IsArchived = request.IsArchived.Value;
            service.IsActive = !request.IsArchived.Value;
        }

        // Category moves are explicit: `categoryId` files the service (validated
        // against THIS business), `clearCategory` uncategorises it, and neither
        // being sent leaves the current filing untouched.
        if (request.ClearCategory == true)
        {
            var categoryRequired = await RequireCategoryAsync(businessId, null, enforceCategory);
            if (categoryRequired != null)
                return ApiResponse<ServiceCatalogItemResponse>.Fail(categoryRequired.Value.Code, categoryRequired.Value.Message);

            service.CategoryId = null;
        }
        else if (request.CategoryId.HasValue)
        {
            var categoryError = await ValidateCategoryAsync(businessId, request.CategoryId);
            if (categoryError != null)
                return ApiResponse<ServiceCatalogItemResponse>.Fail(categoryError.Value.Code, categoryError.Value.Message);

            service.CategoryId = request.CategoryId.Value;
        }

        _unitOfWork.ServiceCatalogItems.Update(service);
        await _unitOfWork.SaveChangesAsync();

        return ApiResponse<ServiceCatalogItemResponse>.Ok(await MapAsync(service));
    }

    public async Task<ApiResponse<bool>> DeleteServiceAsync(Guid ownerUserId, Guid serviceId)
    {
        var business = await ResolveOwnerBusinessAsync(ownerUserId);
        if (business == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "No business found for this account.");

        return await DeleteForBusinessCoreAsync(business.Id, serviceId);
    }

    private async Task<ApiResponse<bool>> DeleteForBusinessCoreAsync(Guid businessId, Guid serviceId)
    {

        var service = await _unitOfWork.ServiceCatalogItems.FirstOrDefaultAsync(s => s.Id == serviceId);
        if (service == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "Service not found.");
        if (service.BusinessId != businessId)
            return ApiResponse<bool>.Fail("FORBIDDEN", "Not authorized to access this service.");

        // Soft delete: ServiceCatalogItem has no IsDeleted column, so deactivate.
        service.IsActive = false;
        _unitOfWork.ServiceCatalogItems.Update(service);

        // Deleting a service is final, so its image records go with it: the
        // attachment rows are removed in the same commit rather than being left
        // pointing at a service nobody can reach again. The media rows themselves
        // survive — they are shared infrastructure with their own retention
        // lifecycle, and nothing is unlinked until this delete is committed.
        var attachments = await _context.ServiceMedia
            .Where(link => link.ServiceCatalogItemId == serviceId)
            .ToListAsync();
        if (attachments.Count > 0) _context.ServiceMedia.RemoveRange(attachments);

        await _unitOfWork.SaveChangesAsync();

        return ApiResponse<bool>.Ok(true);
    }

    /// <inheritdoc />
    public async Task<ApiResponse<ServiceCatalogItemResponse>> SetServiceStatusAsync(
        Guid ownerUserId, Guid serviceId, string status)
    {
        var business = await ResolveOwnerBusinessAsync(ownerUserId);
        if (business == null)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "No business found for this account.");

        // The status vocabulary is closed: an unknown value is rejected instead of
        // being coerced into "paused", which would silently hide a service.
        if (ServiceCatalogStatuses.ToFlags(status) is not { } flags)
            return ApiResponse<ServiceCatalogItemResponse>.Fail(
                "VALIDATION_ERROR",
                "Status must be one of: active, paused, archived.");

        var service = await _unitOfWork.ServiceCatalogItems.FirstOrDefaultAsync(s => s.Id == serviceId);
        if (service == null)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "Service not found.");
        if (service.BusinessId != business.Id)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("FORBIDDEN", "Not authorized to access this service.");

        service.IsActive = flags.IsActive;
        service.IsArchived = flags.IsArchived;

        _unitOfWork.ServiceCatalogItems.Update(service);
        await _unitOfWork.SaveChangesAsync();

        return ApiResponse<ServiceCatalogItemResponse>.Ok(await MapAsync(service));
    }

    /// <inheritdoc />
    public async Task<ApiResponse<List<ServiceImageResponse>>> GetServiceImagesAsync(Guid ownerUserId, Guid serviceId)
    {
        var resolved = await ResolveOwnedServiceAsync(ownerUserId, serviceId);
        if (!resolved.Success) return ApiResponse<List<ServiceImageResponse>>.Fail(resolved.Error!.Code, resolved.Error.Message);

        return ApiResponse<List<ServiceImageResponse>>.Ok(
            (await MapManyAsync([resolved.Data!]))[0].Images.ToList());
    }

    /// <inheritdoc />
    public async Task<ApiResponse<List<ServiceImageResponse>>> SetServiceImagesAsync(
        Guid ownerUserId, Guid serviceId, IReadOnlyList<Guid> mediaIds)
    {
        var resolved = await ResolveOwnedServiceAsync(ownerUserId, serviceId);
        if (!resolved.Success) return ApiResponse<List<ServiceImageResponse>>.Fail(resolved.Error!.Code, resolved.Error.Message);
        var service = resolved.Data!;

        if ((mediaIds ?? []).Any(id => id == Guid.Empty))
            return ApiResponse<List<ServiceImageResponse>>.Fail("VALIDATION_ERROR", "An image reference was empty.");

        // De-duplicate before the ceiling check: sending the same image twice must
        // not consume two of the three slots.
        var ordered = (mediaIds ?? []).Distinct().ToList();
        if (ordered.Count > 0 && !MediaPolicy.HasServiceImageCapacity(ordered.Count - 1, _mediaOptions.MaxServiceImages))
            return ApiResponse<List<ServiceImageResponse>>.Fail(
                "MEDIA_QUOTA_EXCEEDED",
                $"A service can have at most {_mediaOptions.MaxServiceImages} images.");

        // Every id must be a service image owned by THIS business and past the
        // upload stage. A media id from another tenant can never be attached,
        // which is what stops this endpoint being used as a cross-tenant write.
        var media = ordered.Count == 0
            ? new List<PunchedApi.Domain.Entities.Media>()
            : await _context.Media.Where(row => ordered.Contains(row.Id)).ToListAsync();
        var mediaById = media.ToDictionary(row => row.Id);

        foreach (var mediaId in ordered)
        {
            if (!mediaById.TryGetValue(mediaId, out var row))
                return ApiResponse<List<ServiceImageResponse>>.Fail("MEDIA_NOT_FOUND", "The image could not be found.");
            if (!MediaPolicy.IsServiceImageAttachable(service.BusinessId, row))
                return ApiResponse<List<ServiceImageResponse>>.Fail(
                    "MEDIA_NOT_ATTACHABLE",
                    "Every image must be an uploaded photo belonging to this business.");
        }

        // Replace the ordered set in one commit. The write is idempotent: an image
        // that stays keeps its media row — and therefore its storage object — so a
        // reorder or a swap never re-uploads and never orphans a file. Index 0 is
        // the cover, so the ordering the client sends IS the gallery order.
        var existing = await _context.ServiceMedia
            .Where(link => link.ServiceCatalogItemId == service.Id)
            .ToListAsync();
        var existingByMedia = existing.ToDictionary(link => link.MediaId);

        var stale = existing.Where(link => !ordered.Contains(link.MediaId)).ToList();
        if (stale.Count > 0) _context.ServiceMedia.RemoveRange(stale);

        for (var index = 0; index < ordered.Count; index++)
        {
            var mediaId = ordered[index];
            if (existingByMedia.TryGetValue(mediaId, out var link))
            {
                link.SortOrder = index;
                link.Role = index == 0 ? "Primary" : "Gallery";
                continue;
            }

            _context.ServiceMedia.Add(new ServiceMedia
            {
                Id = Guid.NewGuid(),
                ServiceCatalogItemId = service.Id,
                MediaId = mediaId,
                Role = index == 0 ? "Primary" : "Gallery",
                SortOrder = index,
                CreatedAt = DateTime.UtcNow
            });
        }

        await _unitOfWork.SaveChangesAsync();

        return ApiResponse<List<ServiceImageResponse>>.Ok(
            (await MapManyAsync([service]))[0].Images.ToList());
    }

    /// <summary>
    /// Owner-scoped service lookup shared by the image operations: resolves the
    /// caller's business first, so an id from another tenant can never be used to
    /// read or write its images.
    /// </summary>
    private async Task<ApiResponse<ServiceCatalogItem>> ResolveOwnedServiceAsync(Guid ownerUserId, Guid serviceId)
    {
        var business = await ResolveOwnerBusinessAsync(ownerUserId);
        if (business == null)
            return ApiResponse<ServiceCatalogItem>.Fail("NOT_FOUND", "No business found for this account.");

        var service = await _unitOfWork.ServiceCatalogItems.FirstOrDefaultAsync(s => s.Id == serviceId);
        if (service == null)
            return ApiResponse<ServiceCatalogItem>.Fail("NOT_FOUND", "Service not found.");
        if (service.BusinessId != business.Id)
            return ApiResponse<ServiceCatalogItem>.Fail("FORBIDDEN", "Not authorized to access this service.");

        return ApiResponse<ServiceCatalogItem>.Ok(service);
    }

    public async Task<ApiResponse<List<EligibleStaffResponse>>> GetEligibleStaffAsync(Guid businessId, Guid[] serviceIds)
    {
        if (TenantBusinessId is Guid tenantId && businessId != tenantId)
            return ApiResponse<List<EligibleStaffResponse>>.Fail("NOT_FOUND", "Business not found.");
        var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.Id == businessId && !b.IsDeleted);
        if (business == null)
            return ApiResponse<List<EligibleStaffResponse>>.Fail("NOT_FOUND", "Business not found.");

        /* Module entitlement, enforced on the PUBLIC path too.
         *
         * [RequireModule] deliberately skips any endpoint marked [AllowAnonymous]
         * (an anonymous caller has no token for it to resolve), and it is a
         * single-key attribute — so the class-level serviceCatalog gate never
         * applies here and could not express a second key anyway. The roster is
         * checked here instead, against the business the request named, on the
         * SAME effective-entitlement lookup [RequireModule] and the storefront
         * bootstrap use. It requires BOTH:
         *   - serviceCatalog: the roster only exists beside a bookable catalogue;
         *   - staff: the payload is staff profile data, so withdrawing the staff
         *     module must withdraw it from the storefront as well as the console.
         * MODULE_DISABLED (not NOT_FOUND) so the storefront can tell "this
         * business has withdrawn the team" from "this address is wrong".
         */
        foreach (var required in new[] { ModuleKey, StaffModuleKey })
        {
            if (await _moduleEntitlementService.IsModuleEnabledAsync(businessId, required))
                continue;

            _logger.LogWarning(
                "Public roster request for business {BusinessId} refused: module '{ModuleKey}' is not enabled.",
                businessId, required);
            return ApiResponse<List<EligibleStaffResponse>>.Fail(
                "MODULE_DISABLED",
                $"The '{required}' module is not enabled for this business.");
        }

        var distinct = (serviceIds ?? Array.Empty<Guid>()).Distinct().ToArray();
        var services = await _unitOfWork.ServiceCatalogItems.FindAsync(s => s.BusinessId == businessId &&
            s.IsActive && s.Showcase && distinct.Contains(s.Id));
        if (services.Count() != distinct.Length)
            return ApiResponse<List<EligibleStaffResponse>>.Fail("SERVICE_NOT_FOUND", "One or more services are unavailable.");

        // Resolve the candidate providers through the single eligibility
        // mechanism: category-based in a multi-staff business, the owner in a
        // solo / staff-module-disabled business (or while no team exists).
        // Members who are not PUBLIC and BOOKABLE are hidden from the storefront
        // and the booking picker; the owner is shown whenever they are eligible.
        var model = await _eligibility.ResolveModelAsync(businessId);
        var (ownerId, ownerIsProvider) = await _eligibility.ResolveOwnerProviderAsync(businessId);

        // The owner only participates when they are providing services (their
        // membership `IsBookable`); an owner who opted out is never offered.
        var ownerAsProvider = ownerId is Guid oid2 && ownerIsProvider ? oid2 : (Guid?)null;

        var bookableStaffIds = (await _unitOfWork.BusinessMemberships.SelectNoTrackingAsync(
            m => m.BusinessId == businessId &&
                 m.Role == BusinessMembershipRole.Staff &&
                 m.Status == BusinessMembershipStatus.Active &&
                 m.IsPublic &&
                 m.IsBookable,
            m => m.UserId)).ToHashSet();

        List<Guid> staffIds;
        if (model != EffectiveProviderModel.CategoryBased)
        {
            // Solo / owner-only: the owner is the sole provider, unless opted out.
            staffIds = ownerAsProvider is Guid soloOwner ? new List<Guid> { soloOwner } : new List<Guid>();
        }
        else if (distinct.Length == 0)
        {
            // No service filter: every public, bookable staff member (plus the
            // owner, who stands in while the business has no team yet).
            staffIds = bookableStaffIds.ToList();
            if (ownerAsProvider is Guid ownerCandidate && !staffIds.Contains(ownerCandidate))
                staffIds.Add(ownerCandidate);
        }
        else
        {
            var resolution = await _eligibility.ResolveForServicesAsync(businessId, distinct);
            staffIds = resolution.EligibleProviderIds
                .Where(id => bookableStaffIds.Contains(id) || (ownerAsProvider is Guid oid && id == oid))
                .ToList();

            if (staffIds.Count == 0 && resolution.SoloProviderId is Guid soloProvider)
                staffIds = new List<Guid> { soloProvider };
        }

        if (staffIds.Count == 0)
            return ApiResponse<List<EligibleStaffResponse>>.Ok(new List<EligibleStaffResponse>());

        var staff = await _unitOfWork.Users.FindAsync(u => staffIds.Contains(u.Id) && !u.IsDeleted);

        // Which published services each member performs, and the title the
        // business gave them. Both are read once for the whole roster: the
        // storefront renders "who does what" from this single response, so
        // answering it per member would be a fan-out for no reason.
        //
        // Only ACTIVE + SHOWCASE services are eligible, and only this business's
        // — the same rule the catalogue endpoint applies, so a service can never
        // be attributed to someone the customer could not actually book.
        var publicServices = await _unitOfWork.ServiceCatalogItems.SelectNoTrackingAsync(
            s => s.BusinessId == businessId && s.IsActive && s.Showcase && !s.IsArchived,
            s => s.Id);
        var publicServiceIds = publicServices.ToHashSet();

        // Which published services each provider delivers — computed from
        // CATEGORY eligibility (every service in an assigned category), with the
        // legacy explicit assignment still honoured for uncategorised services.
        var publicServicesWithCategory = publicServiceIds.Count == 0
            ? new List<(Guid Id, Guid? CategoryId)>()
            : (await _context.ServiceCatalogItems.AsNoTracking()
                .Where(s => s.BusinessId == businessId && publicServiceIds.Contains(s.Id))
                .Select(s => new { s.Id, s.CategoryId })
                .ToListAsync())
              .Select(s => (s.Id, s.CategoryId))
              .ToList();

        var providerCategoryRows = publicServiceIds.Count == 0
            ? new List<(Guid StaffUserId, Guid CategoryId)>()
            : (await _context.StaffServiceCategories.AsNoTracking()
                .Where(a => a.BusinessId == businessId && staffIds.Contains(a.StaffUserId))
                .Select(a => new { a.StaffUserId, a.ServiceCategoryId })
                .ToListAsync())
              .Select(a => (a.StaffUserId, CategoryId: a.ServiceCategoryId))
              .ToList();

        var categoriesByStaff = providerCategoryRows
            .GroupBy(x => x.StaffUserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.CategoryId).ToHashSet());

        var legacyAssignments = publicServiceIds.Count == 0
            ? new List<StaffServiceAssignment>()
            : (await _unitOfWork.StaffServiceAssignments.FindNoTrackingAsync(
                a => a.BusinessId == businessId && staffIds.Contains(a.StaffUserId))).ToList();

        var legacyByStaff = legacyAssignments
            .GroupBy(a => a.StaffUserId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.ServiceCatalogItemId).ToHashSet());

        var servicesByStaff = new Dictionary<Guid, List<Guid>>();
        foreach (var providerId in staffIds)
        {
            categoriesByStaff.TryGetValue(providerId, out var cats);
            legacyByStaff.TryGetValue(providerId, out var legacy);

            servicesByStaff[providerId] = publicServicesWithCategory
                .Where(sc =>
                    (sc.CategoryId is Guid cid && cats != null && cats.Contains(cid)) ||
                    (sc.CategoryId == null && legacy != null && legacy.Contains(sc.Id)))
                .Select(sc => sc.Id)
                .Distinct()
                .ToList();
        }

        // The current title is the most recent history row for the member. A row
        // with a null label is a *clearing* (the title was removed), so the
        // latest row decides even when it carries no name.
        var positionRows = (await _unitOfWork.StaffPositionHistory.FindNoTrackingAsync(
            h => h.BusinessId == businessId && staffIds.Contains(h.StaffUserId))).ToList();
        var positionByStaff = positionRows
            .GroupBy(h => h.StaffUserId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.ChangedAt).First().PositionName);

        // The public storefront profile of each member (biography, specialities,
        // stated experience). Read once for the whole roster, and only for
        // members who already passed the public+bookable filter above — a
        // private employee's profile never reaches this dictionary.
        var publicProfiles = (await _unitOfWork.BusinessMemberships.FindNoTrackingAsync(
                m => m.BusinessId == businessId && staffIds.Contains(m.UserId)))
            .ToDictionary(m => m.UserId);

        return ApiResponse<List<EligibleStaffResponse>>.Ok(staff
            .OrderBy(u => u.FullName)
            .Select(u =>
            {
                publicProfiles.TryGetValue(u.Id, out var profile);
                return new EligibleStaffResponse
                {
                    UserId = u.Id,
                    FullName = u.FullName,
                    AvatarUrl = u.AvatarUrl,
                    PhoneNumber = u.PhoneNumber,
                    ServiceIds = servicesByStaff.TryGetValue(u.Id, out var ids) ? ids : new List<Guid>(),
                    PositionName = positionByStaff.TryGetValue(u.Id, out var title) ? title : null,
                    Bio = profile?.Bio,
                    Specialities = profile?.Specialities,
                    YearsOfExperience = profile?.YearsOfExperience
                };
            })
            .ToList());
    }

    private async Task<List<ServiceCatalogItemResponse>> MapManyAsync(IEnumerable<ServiceCatalogItem> services)
    {
        var list = services.ToList();
        if (list.Count == 0) return new List<ServiceCatalogItemResponse>();

        // One batched query — a shared DbContext cannot run concurrent queries.
        // Every attachment row is read (not just the primary), because a service
        // can carry up to `MediaStorage:MaxServiceImages` images and the display
        // order decides both gallery position and which one is the cover.
        var ids = list.Select(s => s.Id).ToList();
        var links = await _unitOfWork.ServiceMedia
            .FindNoTrackingAsync(x => ids.Contains(x.ServiceCatalogItemId));
        var linksByService = links
            .OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt)
            .GroupBy(x => x.ServiceCatalogItemId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var mediaIds = links.Select(x => x.MediaId).Distinct().ToArray();
        var relatedMedia = mediaIds.Length == 0
            ? new List<ServiceImageProjection>()
            : await _unitOfWork.Media.SelectNoTrackingAsync(
                x => mediaIds.Contains(x.Id),
                x => new ServiceImageProjection(x.Id, x.BusinessId, x.Purpose, x.Status, x.Visibility, x.VariantsJson, x.Width, x.Height));
        var mediaById = relatedMedia.ToDictionary(x => x.Id);

        // One batched category query, keyed for the response projection.
        var categoryIds = list.Where(s => s.CategoryId.HasValue)
            .Select(s => s.CategoryId!.Value).Distinct().ToArray();
        var categories = categoryIds.Length == 0
            ? new Dictionary<Guid, ServiceCategory>()
            : (await _unitOfWork.ServiceCategories
                    .FindNoTrackingAsync(c => categoryIds.Contains(c.Id)))
                .ToDictionary(c => c.Id);

        return list.Select(s =>
        {
            var response = Map(s);

            if (s.CategoryId.HasValue && categories.TryGetValue(s.CategoryId.Value, out var category))
            {
                // A category from another business can never be rendered, even
                // if the id somehow landed on the row.
                if (category.BusinessId == s.BusinessId)
                {
                    response.CategoryName = category.Name;
                    response.CategorySortOrder = category.SortOrder;
                }
                else
                {
                    response.CategoryId = null;
                }
            }

            if (linksByService.TryGetValue(s.Id, out var serviceLinks))
            {
                response.Images = MapServiceImages(s, serviceLinks, mediaById);

                // The legacy single-image fields describe the cover, so the
                // storefront keeps rendering the first image unchanged.
                var cover = response.Images.FirstOrDefault();
                if (cover != null)
                {
                    response.ImageMediaId = cover.MediaId;
                    response.ImageStatus = cover.Status;
                    response.ImageVariants = cover.Variants;
                }
            }
            return response;
        }).ToList();
    }

    /// <summary>
    /// Projects a service's attachment rows into its ordered image list.
    ///
    /// <para>An attachment whose media belongs to another business, carries a
    /// different purpose, or is on its way out is skipped rather than rendered —
    /// the same guard the single-image projection has always applied. The cover
    /// is whatever lands at display order 0 after that filtering, which keeps the
    /// "first image is the cover" invariant true for every caller.</para>
    /// </summary>
    private static List<ServiceImageResponse> MapServiceImages(
        ServiceCatalogItem service,
        IReadOnlyList<ServiceMedia> links,
        IReadOnlyDictionary<Guid, ServiceImageProjection> mediaById)
    {
        var images = new List<ServiceImageResponse>(links.Count);

        foreach (var link in links)
        {
            if (!mediaById.TryGetValue(link.MediaId, out var media)) continue;
            if (media.BusinessId != service.BusinessId || media.Purpose != MediaPurposes.ServiceImage) continue;
            if (media.Status is MediaStatus.Deleted or MediaStatus.Deleting or MediaStatus.Failed) continue;

            var ready = media.Status == MediaStatus.Ready && media.Visibility == MediaVisibility.Public;
            var variants = ready ? MapImageVariants(media.VariantsJson) : [];

            images.Add(new ServiceImageResponse
            {
                Id = media.Id,
                MediaId = media.Id,
                Status = media.Status.ToString().ToLowerInvariant(),
                Url = ready ? CoverUrl(variants) : null,
                Width = media.Width,
                Height = media.Height,
                DisplayOrder = images.Count,
                IsCover = images.Count == 0,
                Variants = variants
            });
        }

        return images;
    }

    /// <summary>Largest ready variant: what a service card and the gallery hero render.</summary>
    private static string? CoverUrl(IReadOnlyList<ServiceImageVariantResponse> variants) =>
        variants.Count == 0 ? null : variants.OrderByDescending(variant => variant.Width).First().Url;

    private async Task<ServiceCatalogItemResponse> MapAsync(ServiceCatalogItem service)
    {
        return (await MapManyAsync([service]))[0];
    }

    private static IReadOnlyList<ServiceImageVariantResponse> MapImageVariants(string variantsJson)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<MediaVariantResponse>>(variantsJson) ?? [])
                .Where(variant => variant.Format is "webp" or "jpeg")
                .Select(variant => new ServiceImageVariantResponse
                {
                    Url = variant.Url,
                    Width = variant.Width,
                    Height = variant.Height,
                    Format = variant.Format
                })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ServiceCatalogItemResponse Map(ServiceCatalogItem s) => new()
    {
        Id = s.Id,
        BusinessId = s.BusinessId,
        Name = s.Name,
        Description = s.Description,
        DurationMinutes = s.DurationMinutes,
        Price = s.Price ?? 0,
        IsActive = s.IsActive,
        IsArchived = s.IsArchived,
        Status = ServiceCatalogStatuses.From(s.IsActive, s.IsArchived),
        Showcase = s.Showcase,
        CategoryId = s.CategoryId,
        CreatedAt = s.CreatedAt
    };
}
