using PunchedApi.Application.DTOs;
using static PunchedApi.Application.DTOs.TierModuleView;

namespace PunchedApi.Application.Services;

/// <summary>
/// Owns admin subscription-tier (plan) CRUD, lifecycle transitions, and
/// tier-module configuration. Controllers stay thin; all business rules,
/// cache invalidation, and audit calls live here.
/// </summary>
public interface IAdminTierService
{
    /// <summary>All tiers with module/business counts (tier list).</summary>
    Task<ApiResponse<List<AdminTierSummary>>> ListAsync();

    /// <summary>KPI summary (active tiers, draft tiers, total subscribers).</summary>
    Task<ApiResponse<AdminSubscriptionSummary>> GetSummaryAsync();

    /// <summary>Single tier detail including its module set.</summary>
    Task<ApiResponse<AdminTierDetail>> GetAsync(Guid id);

    /// <summary>Creates a new Draft tier. Keys must be unique.</summary>
    Task<ApiResponse<AdminTierDetail>> CreateAsync(AdminTierCreateRequest request, Guid? actorUserId);

    /// <summary>Edits a Draft/Inactive tier's metadata (key immutable after publish).</summary>
    Task<ApiResponse<AdminTierDetail>> UpdateAsync(Guid id, AdminTierUpdateRequest request, Guid? actorUserId);

    /// <summary>DELETE semantics — archives the tier. Never physically deletes published tiers.</summary>
    Task<ApiResponse<MessageResponse>> DeleteAsync(Guid id, Guid? actorUserId);

    /// <summary>Publishes Draft/Inactive → Active (requires a core module + valid dependencies).</summary>
    Task<ApiResponse<AdminTierDetail>> PublishAsync(Guid id, Guid? actorUserId);

    /// <summary>Deactivates Draft/Active → Inactive.</summary>
    Task<ApiResponse<AdminTierDetail>> DeactivateAsync(Guid id, Guid? actorUserId);

    /// <summary>Archives Draft/Inactive → Archived (rejects if active subscribers).</summary>
    Task<ApiResponse<AdminTierDetail>> ArchiveAsync(Guid id, Guid? actorUserId);

    /// <summary>Current module set for a tier.</summary>
    Task<ApiResponse<AdminTierModulesResponse>> GetModulesAsync(Guid id);

    /// <summary>Transactionsally replaces a tier's module set (Draft/Inactive only).</summary>
    Task<ApiResponse<AdminTierModulesResponse>> SetModulesAsync(Guid id, AdminTierModulesRequest request, Guid? actorUserId);

    /// <summary>Admin module catalog (runtime manifest enriched with DB metadata).</summary>
    Task<ApiResponse<AdminModulesCatalogResponse>> GetCatalogModulesAsync();
}