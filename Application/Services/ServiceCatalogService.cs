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
            .FindAsync(s => s.BusinessId == businessId && s.IsActive && s.Showcase);

        return ApiResponse<List<ServiceCatalogItemResponse>>.Ok(
            services.OrderBy(s => s.CreatedAt).Select(Map).ToList());
    }

    public async Task<ApiResponse<List<ServiceCatalogItemResponse>>> GetMyServicesAsync(Guid ownerUserId)
    {
        var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.OwnerId == ownerUserId);
        if (business == null)
            return ApiResponse<List<ServiceCatalogItemResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        var services = await _unitOfWork.ServiceCatalogItems
            .FindAsync(s => s.BusinessId == business.Id);

        return ApiResponse<List<ServiceCatalogItemResponse>>.Ok(
            services.OrderBy(s => s.CreatedAt).Select(Map).ToList());
    }

    public async Task<ApiResponse<ServiceCatalogItemResponse>> GetServiceAsync(Guid ownerUserId, Guid serviceId)
    {
        var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.OwnerId == ownerUserId);
        if (business == null)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var service = await _unitOfWork.ServiceCatalogItems.FirstOrDefaultAsync(s => s.Id == serviceId);
        if (service == null)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "Service not found.");
        if (service.BusinessId != business.Id)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("FORBIDDEN", "Not authorized to access this service.");

        return ApiResponse<ServiceCatalogItemResponse>.Ok(Map(service));
    }

    public async Task<ApiResponse<ServiceCatalogItemResponse>> CreateServiceAsync(Guid ownerUserId, CreateServiceRequest request)
    {
        var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.OwnerId == ownerUserId);
        if (business == null)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var service = new ServiceCatalogItem
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
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

        return ApiResponse<ServiceCatalogItemResponse>.Ok(Map(service));
    }

    public async Task<ApiResponse<ServiceCatalogItemResponse>> UpdateServiceAsync(Guid ownerUserId, Guid serviceId, UpdateServiceRequest request)
    {
        var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.OwnerId == ownerUserId);
        if (business == null)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var service = await _unitOfWork.ServiceCatalogItems.FirstOrDefaultAsync(s => s.Id == serviceId);
        if (service == null)
            return ApiResponse<ServiceCatalogItemResponse>.Fail("NOT_FOUND", "Service not found.");
        if (service.BusinessId != business.Id)
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

        return ApiResponse<ServiceCatalogItemResponse>.Ok(Map(service));
    }

    public async Task<ApiResponse<bool>> DeleteServiceAsync(Guid ownerUserId, Guid serviceId)
    {
        var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.OwnerId == ownerUserId);
        if (business == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "No business found for this account.");

        var service = await _unitOfWork.ServiceCatalogItems.FirstOrDefaultAsync(s => s.Id == serviceId);
        if (service == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "Service not found.");
        if (service.BusinessId != business.Id)
            return ApiResponse<bool>.Fail("FORBIDDEN", "Not authorized to access this service.");

        // Soft delete: ServiceCatalogItem has no IsDeleted column, so deactivate.
        service.IsActive = false;
        _unitOfWork.ServiceCatalogItems.Update(service);
        await _unitOfWork.SaveChangesAsync();

        return ApiResponse<bool>.Ok(true);
    }

    public async Task<ApiResponse<List<EligibleStaffResponse>>> GetEligibleStaffAsync(Guid businessId, Guid[] serviceIds)
    {
        var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.Id == businessId);
        if (business == null)
            return ApiResponse<List<EligibleStaffResponse>>.Fail("NOT_FOUND", "Business not found.");

        var distinct = (serviceIds ?? Array.Empty<Guid>()).Distinct().ToArray();

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
