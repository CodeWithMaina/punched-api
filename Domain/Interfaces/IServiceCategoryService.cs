using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Business-scoped service category management.
///
/// <para>Every method resolves the caller's business server-side; a category id
/// from another business is indistinguishable from a missing one. Categories
/// are never shared between businesses.</para>
/// </summary>
public interface IServiceCategoryService
{
    /// <summary>Owner: all categories of the caller's business (including inactive).</summary>
    Task<ApiResponse<List<ServiceCategoryResponse>>> GetCategoriesAsync(Guid callerUserId, string role, bool includeInactive = true);

    /// <summary>Owner: a single category of the caller's business.</summary>
    Task<ApiResponse<ServiceCategoryResponse>> GetCategoryAsync(Guid callerUserId, string role, Guid categoryId);

    /// <summary>Owner: creates a category. Names are unique per business (case-insensitive).</summary>
    Task<ApiResponse<ServiceCategoryResponse>> CreateCategoryAsync(Guid callerUserId, string role, CreateServiceCategoryRequest request);

    /// <summary>Owner: partially updates a category.</summary>
    Task<ApiResponse<ServiceCategoryResponse>> UpdateCategoryAsync(Guid callerUserId, string role, Guid categoryId, UpdateServiceCategoryRequest request);

    /// <summary>Owner: deterministically rewrites the display order.</summary>
    Task<ApiResponse<List<ServiceCategoryResponse>>> ReorderCategoriesAsync(Guid callerUserId, string role, ReorderServiceCategoriesRequest request);

    /// <summary>
    /// Owner: deletes a category that no service references. A referenced
    /// category is refused (CATEGORY_IN_USE) and must be deactivated instead.
    /// </summary>
    Task<ApiResponse<bool>> DeleteCategoryAsync(Guid callerUserId, string role, Guid categoryId);

    /// <summary>Owner: the services assigned to one category of the caller's business.</summary>
    Task<ApiResponse<List<ServiceCatalogItemResponse>>> GetCategoryServicesAsync(Guid callerUserId, string role, Guid categoryId);

    /// <summary>
    /// Owner: copies the starter categories for the business's declared type,
    /// but only when the business has no categories yet. Idempotent.
    /// </summary>
    Task<ApiResponse<List<ServiceCategoryResponse>>> ApplyStarterCategoriesAsync(Guid callerUserId, string role);

    /// <summary>
    /// Public: the business's published catalogue grouped by ACTIVE category,
    /// plus the uncategorised remainder. Honours the serviceCatalog module
    /// entitlement, showcase flags and tenant narrowing.
    /// </summary>
    Task<ApiResponse<PublicServiceCatalogResponse>> GetPublicCatalogueAsync(Guid businessId);
}
