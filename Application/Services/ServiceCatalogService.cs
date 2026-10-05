using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

/// <summary>
/// Backs the ServiceCatalog endpoints and the public per-business active list.
/// Owner-scoped methods resolve the business from the ownerUserId and assert ownership.
/// </summary>
public class ServiceCatalogService : IServiceCatalogService
{
    private sealed record ServiceImageProjection(Guid Id, Guid? BusinessId, string Purpose, MediaStatus Status, MediaVisibility Visibility, string VariantsJson);

    /// <summary>
    /// Module key the whole controller is gated on. Repeated here (rather than
    /// referenced through a constant) so the PUBLIC path below can enforce the
    /// same gate <c>[RequireModule]</c> enforces on the owner paths — see
    /// <see cref="GetServicesForBusinessAsync"/>.
    /// </summary>
    private const string ModuleKey = "serviceCatalog";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ServiceCatalogService> _logger;
    private readonly IModuleEntitlementService _moduleEntitlementService;

    /// <summary>Active tenant (null on the platform root) — scoping only, never a grant.</summary>
    private readonly ITenantContext? _tenant;

    public ServiceCatalogService(
        IUnitOfWork unitOfWork,
        ILogger<ServiceCatalogService> logger,
        IModuleEntitlementService moduleEntitlementService,
        ITenantContext? tenant = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _moduleEntitlementService = moduleEntitlementService;
        _tenant = tenant;
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
            : await CreateForBusinessCoreAsync(business.Id, request);
    }

    public async Task<ApiResponse<ServiceCatalogItemResponse>> UpdateForBusinessAsync(Guid businessId, Guid serviceId, UpdateServiceRequest request)
    {
        var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.Id == businessId && !b.IsDeleted &&
            (TenantBusinessId == null || b.Id == TenantBusinessId));
        return business == null
            ? ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "Business not found.")
            : await UpdateForBusinessCoreAsync(business.Id, serviceId, request);
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

    private async Task<ApiResponse<ServiceCatalogItemResponse>> CreateForBusinessCoreAsync(Guid businessId, CreateServiceRequest request)
    {

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
            CreatedAt = DateTime.UtcNow
        };

        await _unitOfWork.ServiceCatalogItems.AddAsync(service);
        await _unitOfWork.SaveChangesAsync();

        return ApiResponse<ServiceCatalogItemResponse>.Ok(await MapAsync(service));
    }

    public async Task<ApiResponse<ServiceCatalogItemResponse>> UpdateServiceAsync(Guid ownerUserId, Guid serviceId, UpdateServiceRequest request)
    {
        var business = await ResolveOwnerBusinessAsync(ownerUserId);
        if (business == null)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "No business found for this account.");

        return await UpdateForBusinessCoreAsync(business.Id, serviceId, request);
    }

    private async Task<ApiResponse<ServiceCatalogItemResponse>> UpdateForBusinessCoreAsync(Guid businessId, Guid serviceId, UpdateServiceRequest request)
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
        await _unitOfWork.SaveChangesAsync();

        return ApiResponse<bool>.Ok(true);
    }

    public async Task<ApiResponse<List<EligibleStaffResponse>>> GetEligibleStaffAsync(Guid businessId, Guid[] serviceIds)
    {
        if (TenantBusinessId is Guid tenantId && businessId != tenantId)
            return ApiResponse<List<EligibleStaffResponse>>.Fail("NOT_FOUND", "Business not found.");
        var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.Id == businessId && !b.IsDeleted);
        if (business == null)
            return ApiResponse<List<EligibleStaffResponse>>.Fail("NOT_FOUND", "Business not found.");

        var distinct = (serviceIds ?? Array.Empty<Guid>()).Distinct().ToArray();
        var services = await _unitOfWork.ServiceCatalogItems.FindAsync(s => s.BusinessId == businessId &&
            s.IsActive && s.Showcase && distinct.Contains(s.Id));
        if (services.Count() != distinct.Length)
            return ApiResponse<List<EligibleStaffResponse>>.Fail("SERVICE_NOT_FOUND", "One or more services are unavailable.");

        // Resolve the candidate staff set from the staff-service assignments.
        List<Guid> staffIds;
        if (distinct.Length == 0)
        {
            // No service filter: every staff member of this business is eligible.
            var allStaff = await _unitOfWork.Users.FindAsync(u => u.StaffBusinessId == businessId);
            staffIds = allStaff.Select(u => u.Id).ToList();
        }
        else
        {
            var assignments = await _unitOfWork.StaffServiceAssignments
                .FindAsync(a => a.BusinessId == businessId && distinct.Contains(a.ServiceCatalogItemId));

            // A staff member is eligible only when assigned to ALL requested services.
            staffIds = assignments
                .GroupBy(a => a.StaffUserId)
                .Where(g => g.Select(x => x.ServiceCatalogItemId).Distinct().Count() == distinct.Length)
                .Select(g => g.Key)
                .ToList();
        }

        if (staffIds.Count == 0)
            return ApiResponse<List<EligibleStaffResponse>>.Ok(new List<EligibleStaffResponse>());

        var staff = await _unitOfWork.Users.FindAsync(u => staffIds.Contains(u.Id) && u.StaffBusinessId == businessId);

        return ApiResponse<List<EligibleStaffResponse>>.Ok(staff
            .OrderBy(u => u.FullName)
            .Select(u => new EligibleStaffResponse
            {
                UserId = u.Id,
                FullName = u.FullName,
                AvatarUrl = u.AvatarUrl
            })
            .ToList());
    }

    private async Task<List<ServiceCatalogItemResponse>> MapManyAsync(IEnumerable<ServiceCatalogItem> services)
    {
        var list = services.ToList();
        if (list.Count == 0) return new List<ServiceCatalogItemResponse>();

        // One batched query — a shared DbContext cannot run concurrent queries.
        var ids = list.Select(s => s.Id).ToList();
        var media = await _unitOfWork.ServiceMedia
            .FindNoTrackingAsync(x => ids.Contains(x.ServiceCatalogItemId) && x.Role == "Primary");
        var mediaByService = media
            .GroupBy(x => x.ServiceCatalogItemId)
            .ToDictionary(g => g.Key, g => g.First().MediaId);
        var mediaIds = mediaByService.Values.Distinct().ToArray();
        var relatedMedia = mediaIds.Length == 0
            ? new List<ServiceImageProjection>()
            : await _unitOfWork.Media.SelectNoTrackingAsync(
                x => mediaIds.Contains(x.Id),
                x => new ServiceImageProjection(x.Id, x.BusinessId, x.Purpose, x.Status, x.Visibility, x.VariantsJson));
        var mediaById = relatedMedia.ToDictionary(x => x.Id);

        return list.Select(s =>
        {
            var response = Map(s);
            if (mediaByService.TryGetValue(s.Id, out var mediaId))
            {
                response.ImageMediaId = mediaId;
                if (mediaById.TryGetValue(mediaId, out var image) &&
                    image.BusinessId == s.BusinessId && image.Purpose == MediaPurposes.ServiceImage)
                {
                    response.ImageStatus = image.Status.ToString().ToLowerInvariant();
                    if (image.Status == MediaStatus.Ready && image.Visibility == MediaVisibility.Public)
                        response.ImageVariants = MapImageVariants(image.VariantsJson);
                }
            }
            return response;
        }).ToList();
    }

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
        Showcase = s.Showcase,
        CreatedAt = s.CreatedAt
    };
}
