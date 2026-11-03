using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PunchedApi.Application.Authorization;
using PunchedApi.API.Filters;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using Microsoft.AspNetCore.RateLimiting;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Service catalog controller — owner CRUD + public per-business active list.
/// Base route: /v1/services
/// Owner endpoints are [Authorize(Policy = MembershipPolicies.Owner)]; the public list is [AllowAnonymous].
/// </summary>
[ApiController]
[Route("v1/services")]
[Produces("application/json")]
    [RequireModule("serviceCatalog")]
[EnableRateLimiting("general")]
public class ServiceCatalogController : ControllerBase
{
    private readonly IServiceCatalogService _catalogService;
    private readonly IServiceCategoryService _categoryService;

    public ServiceCatalogController(
        IServiceCatalogService catalogService,
        IServiceCategoryService categoryService)
    {
        _catalogService = catalogService;
        _categoryService = categoryService;
    }

    [HttpGet("admin")]
    [Authorize(Policy = MembershipPolicies.PlatformAdmin)]
    public async Task<IActionResult> GetAdminServices([FromQuery] Guid? businessId)
    {
        var result = await _catalogService.GetAdminServicesAsync(businessId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    [HttpPost("admin/{businessId:guid}")]
    [Authorize(Policy = MembershipPolicies.PlatformAdmin)]
    public async Task<IActionResult> CreateForBusiness(Guid businessId, [FromBody] CreateServiceRequest request)
    {
        var result = await _catalogService.CreateForBusinessAsync(businessId, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    [HttpPatch("admin/{businessId:guid}/{id:guid}")]
    [Authorize(Policy = MembershipPolicies.PlatformAdmin)]
    public async Task<IActionResult> UpdateForBusiness(Guid businessId, Guid id, [FromBody] UpdateServiceRequest request)
    {
        var result = await _catalogService.UpdateForBusinessAsync(businessId, id, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    [HttpDelete("admin/{businessId:guid}/{id:guid}")]
    [Authorize(Policy = MembershipPolicies.PlatformAdmin)]
    public async Task<IActionResult> DeleteForBusiness(Guid businessId, Guid id)
    {
        var result = await _catalogService.DeleteForBusinessAsync(businessId, id);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    [HttpGet("{businessId:guid}/{id:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPublicService(Guid businessId, Guid id)
    {
        var result = await _catalogService.GetPublicServiceAsync(businessId, id);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Owner: list all of the owner's business services (including inactive).</summary>
    [HttpGet("me")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<List<ServiceCatalogItemResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyServices()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _catalogService.GetMyServicesAsync(userId.Value);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Owner: the business catalogue with DB-first filtering (category,
    /// uncategorised, active, search), sorting and pagination. Category names are
    /// resolved server-side so the client never joins two collections.
    /// </summary>
    [HttpGet("me/query")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<PaginatedResponse<ServiceCatalogItemResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> QueryMyServices(
        [FromQuery] Guid? categoryId,
        [FromQuery] bool uncategorized = false,
        [FromQuery] bool? isActive = null,
        [FromQuery] string? status = null,
        [FromQuery] decimal? minPrice = null,
        [FromQuery] decimal? maxPrice = null,
        [FromQuery] int? minDuration = null,
        [FromQuery] int? maxDuration = null,
        [FromQuery] bool? showcase = null,
        [FromQuery] bool? hasImages = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDirection = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _catalogService.QueryMyServicesAsync(userId.Value, new ServiceCatalogQueryRequest
        {
            CategoryId = categoryId,
            Uncategorized = uncategorized,
            IsActive = isActive,
            Status = status,
            MinPrice = minPrice,
            MaxPrice = maxPrice,
            MinDuration = minDuration,
            MaxDuration = maxDuration,
            Showcase = showcase,
            HasImages = hasImages,
            Search = search,
            SortBy = sortBy,
            SortDirection = sortDirection,
            Page = page,
            PageSize = pageSize
        });

        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Owner: get a single service belonging to the owner's business.</summary>
    [HttpGet("me/{id:guid}")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<ServiceCatalogItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetService(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _catalogService.GetServiceAsync(userId.Value, id);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Owner: create a new active service for the owner's business.</summary>
    [HttpPost("me")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<ServiceCatalogItemResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateService([FromBody] CreateServiceRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _catalogService.CreateServiceAsync(userId.Value, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Owner: partially update a service belonging to the owner's business.</summary>
    [HttpPatch("me/{id:guid}")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<ServiceCatalogItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateService(Guid id, [FromBody] UpdateServiceRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _catalogService.UpdateServiceAsync(userId.Value, id, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Owner: pause a service (still offered, temporarily not bookable).
    /// Explicit routes rather than a free-form status field, so the three
    /// lifecycle moves are each validated as a transition.
    /// </summary>
    [HttpPost("me/{id:guid}/pause")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    public Task<IActionResult> PauseService(Guid id) => SetStatus(id, ServiceCatalogStatuses.Paused);

    /// <summary>Owner: resume a paused service.</summary>
    [HttpPost("me/{id:guid}/resume")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    public Task<IActionResult> ResumeService(Guid id) => SetStatus(id, ServiceCatalogStatuses.Active);

    /// <summary>Owner: archive a service — filed away, never bookable again until restored.</summary>
    [HttpPost("me/{id:guid}/archive")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    public Task<IActionResult> ArchiveService(Guid id) => SetStatus(id, ServiceCatalogStatuses.Archived);

    /// <summary>Owner: restore an archived service back to active.</summary>
    [HttpPost("me/{id:guid}/restore")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    public Task<IActionResult> RestoreService(Guid id) => SetStatus(id, ServiceCatalogStatuses.Active);

    /// <summary>Owner: the service's images in gallery order (index 0 is the cover).</summary>
    [HttpGet("me/{id:guid}/images")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<List<ServiceImageResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetServiceImages(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _catalogService.GetServiceImagesAsync(userId.Value, id);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Owner: replace the ordered image set of a service. The array order is the
    /// display order, so the first id becomes the cover; the three-image ceiling,
    /// business ownership and upload state are all enforced server-side.
    /// </summary>
    [HttpPut("me/{id:guid}/images")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<List<ServiceImageResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetServiceImages(Guid id, [FromBody] SetServiceImagesRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _catalogService.SetServiceImagesAsync(userId.Value, id, request.MediaIds);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    private async Task<IActionResult> SetStatus(Guid id, string status)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _catalogService.SetServiceStatusAsync(userId.Value, id, status);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Owner: soft-delete a service by deactivating it.</summary>
    [HttpDelete("me/{id:guid}")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteService(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _catalogService.DeleteServiceAsync(userId.Value, id);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Public: list a business's active services.</summary>
    [HttpGet("{businessId:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<List<ServiceCatalogItemResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetServicesForBusiness(Guid businessId)
    {
        var result = await _catalogService.GetServicesForBusinessAsync(businessId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Public: the business's published catalogue grouped by ACTIVE category,
    /// plus the uncategorised remainder (which includes services filed under a
    /// deactivated category, so nothing bookable is ever hidden).
    ///
    /// <para>This is the storefront grouping contract: the client renders groups
    /// from backend data and never re-implements visibility rules. Honours the
    /// serviceCatalog entitlement, the active+showcase service rules and tenant
    /// narrowing.</para>
    /// </summary>
    [HttpGet("{businessId:guid}/catalogue")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<PublicServiceCatalogResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCatalogueForBusiness(Guid businessId)
    {
        var result = await _categoryService.GetPublicCatalogueAsync(businessId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Public: staff eligible to perform the given services (booking wizard, staff step).
    /// Pass no serviceIds to list every staff member of the business.
    /// </summary>
    [HttpGet("{businessId:guid}/staff")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<List<EligibleStaffResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEligibleStaff(Guid businessId, [FromQuery] string? serviceIds)
    {
        var parsed = new List<Guid>();
        if (!string.IsNullOrWhiteSpace(serviceIds))
        {
            foreach (var raw in serviceIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Guid.TryParse(raw, out var id))
                    return BadRequest(ApiResponse<List<EligibleStaffResponse>>.Fail("VALIDATION_ERROR", "serviceIds must be a comma-separated list of GUIDs."));
                parsed.Add(id);
            }
        }

        var result = await _catalogService.GetEligibleStaffAsync(businessId, parsed.ToArray());
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Maps an ApiResponse failure to the HTTP status dictated by backend.md §9.
    /// Success always returns 200 (Ok) via the callers.
    /// </summary>
    private IActionResult MapFailure<T>(ApiResponse<T> result)
        => result.Error?.Code switch
        {
            "NOT_FOUND" or "SERVICE_NOT_FOUND" or "STAFF_NOT_FOUND" or "CUSTOMER_NOT_FOUND" or "MEDIA_NOT_FOUND" => NotFound(result),
            "FORBIDDEN" => StatusCode(StatusCodes.Status403Forbidden, result),
            // The public catalogue path enforces the module gate itself (the
            // attribute cannot: see ServiceCatalogService). Surfaced as 403 so
            // the storefront reads it the same way as any other blocked module.
            "MODULE_DISABLED" => StatusCode(StatusCodes.Status403Forbidden, result),
            "OVERBOOKING" or "SLOT_UNAVAILABLE" or "INVALID_STATUS_TRANSITION" or "MEDIA_QUOTA_EXCEEDED" => Conflict(result),
            _ => BadRequest(result)   // STAFF_NOT_AVAILABLE, VALIDATION_ERROR, BUSINESS_NOT_FOUND, fallback
        };

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}
