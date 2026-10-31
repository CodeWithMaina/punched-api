using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Filters;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Service category management. Base route: /v1/services/categories
///
/// <para>Owner/staff surface, gated twice: by the module entitlement
/// (<c>[RequireModule("serviceCatalog")]</c>) and by a business-scoped permission
/// (<see cref="RequirePermissionAttribute"/>). The service re-checks the
/// permission as well.</para>
///
/// <para>The public grouped catalogue lives at
/// <c>GET /v1/services/{businessId}/catalogue</c> on the service catalogue
/// controller, next to the existing public service list.</para>
/// </summary>
[ApiController]
[Route("v1/services/categories")]
[Produces("application/json")]
[Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
[RequireModule("serviceCatalog")]
[EnableRateLimiting("general")]
public partial class ServiceCategoryController : ControllerBase
{
    private readonly IServiceCategoryService _categories;

    public ServiceCategoryController(IServiceCategoryService categories)
    {
        _categories = categories;
    }

    /// <summary>
    /// All categories of the caller business, in display order. Includes
    /// deactivated categories by default so an owner can see (and reactivate)
    /// everything; pass <c>includeInactive=false</c> for the public view.
    /// </summary>
    [HttpGet]
    [RequirePermission("serviceCategories.view")]
    [ProducesResponseType(typeof(ApiResponse<List<ServiceCategoryResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCategories([FromQuery] bool includeInactive = true)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _categories.GetCategoriesAsync(userId.Value, GetTenantRole(), includeInactive);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>A single category of the caller business.</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission("serviceCategories.view")]
    [ProducesResponseType(typeof(ApiResponse<ServiceCategoryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCategory(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _categories.GetCategoryAsync(userId.Value, GetTenantRole(), id);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>The services filed under one category.</summary>
    [HttpGet("{id:guid}/services")]
    [RequirePermission("serviceCategories.view")]
    [ProducesResponseType(typeof(ApiResponse<List<ServiceCatalogItemResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCategoryServices(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _categories.GetCategoryServicesAsync(userId.Value, GetTenantRole(), id);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Creates a category. Names are unique per business (case-insensitive); a
    /// duplicate is answered with 409 CATEGORY_NAME_EXISTS rather than a silent
    /// second row.
    /// </summary>
    [HttpPost]
    [RequirePermission("serviceCategories.manage")]
    [ProducesResponseType(typeof(ApiResponse<ServiceCategoryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateCategory([FromBody] CreateServiceCategoryRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _categories.CreateCategoryAsync(userId.Value, GetTenantRole(), request);
        return result.Success ? Ok(result) : MapFailure(result);
    }
}

/// <summary>Category writes: update, reorder, delete and starter templates.</summary>
public partial class ServiceCategoryController
{
    /// <summary>Partially updates a category (rename, description, activate/deactivate).</summary>
    [HttpPatch("{id:guid}")]
    [RequirePermission("serviceCategories.manage")]
    [ProducesResponseType(typeof(ApiResponse<ServiceCategoryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateCategory(Guid id, [FromBody] UpdateServiceCategoryRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _categories.UpdateCategoryAsync(userId.Value, GetTenantRole(), id, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Rewrites the display order deterministically from the supplied id list.
    /// Ids that do not belong to this business are rejected, never ignored.
    /// </summary>
    [HttpPut("order")]
    [RequirePermission("serviceCategories.manage")]
    [ProducesResponseType(typeof(ApiResponse<List<ServiceCategoryResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReorderCategories([FromBody] ReorderServiceCategoriesRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _categories.ReorderCategoriesAsync(userId.Value, GetTenantRole(), request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Deletes a category that no service references. A referenced category is
    /// refused (409 CATEGORY_IN_USE) and should be deactivated instead.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission("serviceCategories.manage")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteCategory(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _categories.DeleteCategoryAsync(userId.Value, GetTenantRole(), id);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Copies the starter categories for the business declared type. Only ever
    /// applied when the business has NO categories yet, so it is safe to call
    /// again: a curated catalogue is never overwritten or duplicated.
    /// </summary>
    [HttpPost("starter-template")]
    [RequirePermission("serviceCategories.manage")]
    [ProducesResponseType(typeof(ApiResponse<List<ServiceCategoryResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ApplyStarterTemplate()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _categories.ApplyStarterCategoriesAsync(userId.Value, GetTenantRole());
        return result.Success ? Ok(result) : MapFailure(result);
    }
}

/// <summary>Shared controller helpers.</summary>
public partial class ServiceCategoryController
{
    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    /// <summary>
    /// The caller tenant persona for endpoints that admit owners and staff. The
    /// service applies the persona own scoping; an absent claim is treated as the
    /// more restrictive Staff persona.
    /// </summary>
    private string GetTenantRole()
    {
        var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
                   ?? User.FindFirst("role")?.Value;

        return string.Equals(role?.Trim(), "Business", StringComparison.OrdinalIgnoreCase)
            ? "Business"
            : "Staff";
    }

    /// <summary>Maps an ApiResponse failure onto the platform HTTP status convention.</summary>
    private IActionResult MapFailure<T>(ApiResponse<T> result)
        => result.Error?.Code switch
        {
            "NOT_FOUND" or "CATEGORY_NOT_FOUND" or "SERVICE_NOT_FOUND" => NotFound(result),
            "FORBIDDEN" or "MODULE_DISABLED" => StatusCode(StatusCodes.Status403Forbidden, result),
            "CATEGORY_NAME_EXISTS" or "CATEGORY_IN_USE" or "CATEGORY_LIMIT_REACHED" => Conflict(result),
            _ => BadRequest(result)
        };
}
