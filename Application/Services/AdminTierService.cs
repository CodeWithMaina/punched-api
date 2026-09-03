using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Modules;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using static PunchedApi.Application.DTOs.TierModuleView;

namespace PunchedApi.Application.Services;

/// <summary>
/// <see cref="IAdminTierService"/> implementation. Encapsulates tier CRUD,
/// lifecycle state machine, and tier-module set management with audit +
/// entitlement-cache invalidation. All methods return <see cref="ApiResponse{T}"/>
/// and never throw for expected domain failures.
/// </summary>
public class AdminTierService : IAdminTierService
{
    // Lifecycle mirror invariant: LifecycleState == Active ⇔ IsActive == true.
    private const string ErrDuplicateKey = "DUPLICATE_TIER_KEY";
    private const string ErrInvalidModule = "INVALID_MODULE";
    private const string ErrDependencyMissing = "DEPENDENCY_MISSING";
    private const string ErrActiveModuleEditDisabled = "ACTIVE_TIER_HAS_MODULE_EDIT_DISABLED";
    private const string ErrNoCoreModule = "TIER_HAS_NO_CORE_MODULE";
    private const string ErrActiveSubscribers = "TIER_HAS_ACTIVE_SUBSCRIBERS";
    private const string ErrInvalidTransition = "INVALID_LIFECYCLE_TRANSITION";
    private const string ErrTierNotFound = "TIER_NOT_FOUND";

    private readonly ApplicationDbContext _context;
    private readonly IModuleEntitlementService _entitlements;
    private readonly ISubscriptionAuditService _audit;
    private readonly ILogger<AdminTierService> _logger;

    public AdminTierService(
        ApplicationDbContext context,
        IModuleEntitlementService entitlements,
        ISubscriptionAuditService audit,
        ILogger<AdminTierService> logger)
    {
        _context = context;
        _entitlements = entitlements;
        _audit = audit;
        _logger = logger;
    }

    // ═════════════════════════════════════════════════════════════
    //  LIST / SUMMARY
    // ═════════════════════════════════════════════════════════════

    public async Task<ApiResponse<List<AdminTierSummary>>> ListAsync()
    {
        try
        {
            var tiers = await _context.SubscriptionPlans
                .AsNoTracking()
                .Include(p => p.PlanModules)
                .OrderBy(p => p.DisplayOrder ?? int.MaxValue)
                .ThenBy(p => p.Name)
                .ToListAsync();

            // Efficient subscriber counts per tier (one grouped query).
            var counts = await _context.BusinessSubscriptions
                .AsNoTracking()
                .GroupBy(s => s.PlanId)
                .Select(g => new { PlanId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(k => k.PlanId, v => v.Count);

            var response = tiers.Select(p => ToSummary(p, counts)).ToList();
            return ApiResponse<List<AdminTierSummary>>.Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list subscription tiers.");
            return ApiResponse<List<AdminTierSummary>>.Fail("LIST_FAILED", "Failed to list subscription tiers.");
        }
    }

    public async Task<ApiResponse<AdminSubscriptionSummary>> GetSummaryAsync()
    {
        try
        {
            var tiers = await _context.SubscriptionPlans.AsNoTracking().ToListAsync();
            var subscribers = await _context.BusinessSubscriptions.AsNoTracking().CountAsync();
            return ApiResponse<AdminSubscriptionSummary>.Ok(new AdminSubscriptionSummary
            {
                TotalTiers = tiers.Count,
                ActiveTiers = tiers.Count(p => p.LifecycleState == SubscriptionPlanLifecycleState.Active),
                DraftTiers = tiers.Count(p => p.LifecycleState == SubscriptionPlanLifecycleState.Draft),
                TotalSubscribers = subscribers
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute subscription summary.");
            return ApiResponse<AdminSubscriptionSummary>.Fail("SUMMARY_FAILED", "Failed to compute subscription summary.");
        }
    }

    public async Task<ApiResponse<AdminTierDetail>> GetAsync(Guid id)
    {
        try
        {
            var tier = await _context.SubscriptionPlans
                .AsNoTracking()
                .Include(p => p.PlanModules)
                .ThenInclude(pm => pm.Module)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (tier == null)
                return ApiResponse<AdminTierDetail>.Fail(ErrTierNotFound, "No subscription tier exists with the given id.");

            var counts = await _context.BusinessSubscriptions
                .AsNoTracking()
                .Where(s => s.PlanId == id)
                .GroupBy(s => s.PlanId)
                .Select(g => g.Count())
                .SingleOrDefaultAsync();

            return ApiResponse<AdminTierDetail>.Ok(ToDetail(tier, counts));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get tier {TierId}.", id);
            return ApiResponse<AdminTierDetail>.Fail("GET_FAILED", "Failed to load the subscription tier.");
        }
    }
// ═════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ═════════════════════════════════════════════════════════════

    public async Task<ApiResponse<AdminTierDetail>> PublishAsync(Guid id, Guid? actorUserId)
    {
        try
        {
            var tier = await _context.SubscriptionPlans
                .Include(p => p.PlanModules)
                .ThenInclude(pm => pm.Module)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (tier == null)
                return ApiResponse<AdminTierDetail>.Fail(ErrTierNotFound, "No subscription tier exists with the given id.");

            if (tier.LifecycleState is not (SubscriptionPlanLifecycleState.Draft or SubscriptionPlanLifecycleState.Inactive))
                return ApiResponse<AdminTierDetail>.Fail(ErrInvalidTransition,
                    $"Cannot publish a tier in state '{tier.LifecycleState}'.");

            // Publish preconditions.
            var includedKeys = tier.PlanModules.Select(pm => pm.Module.Key).ToList();
            var hasCore = includedKeys.Any(k => ModuleCatalog.Find(k)?.Visibility == ModuleVisibility.Core);
            if (!hasCore)
                return ApiResponse<AdminTierDetail>.Fail(ErrNoCoreModule,
                    "A tier must include at least one Core module before it can be published.");

            var dependencyProblem = FindDependencyProblem(includedKeys);
            if (dependencyProblem != null)
                return ApiResponse<AdminTierDetail>.Fail(ErrDependencyMissing, dependencyProblem);

            var now = DateTime.UtcNow;
            ApplyState(tier, SubscriptionPlanLifecycleState.Active, now, actorUserId);

            // If no default tier exists yet, this published tier becomes the default.
            if (!await _context.SubscriptionPlans.AnyAsync(p => p.IsDefault))
                tier.IsDefault = true;

            await _context.SaveChangesAsync();
            await InvalidateSubscribersAsync(id);
            await _audit.RecordAsync(
                "TIER_PUBLISHED", actorUserId, targetBusinessId: null, targetPlanId: tier.Id,
                payloadJson: JsonSerializer.Serialize(new { tier.Id, tier.Key, State = tier.LifecycleState.ToString() }),
                reason: "Tier published.");

            _logger.LogInformation("Admin published subscription tier {TierKey} ({TierId}) by {Actor}.", tier.Key, tier.Id, actorUserId);
            return await GetAsync(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish subscription tier {TierId}.", id);
            return ApiResponse<AdminTierDetail>.Fail("PUBLISH_FAILED", "Failed to publish the subscription tier.");
        }
    }

    public async Task<ApiResponse<AdminTierDetail>> DeactivateAsync(Guid id, Guid? actorUserId)
    {
        try
        {
            var tier = await _context.SubscriptionPlans.FirstOrDefaultAsync(p => p.Id == id);
            if (tier == null)
                return ApiResponse<AdminTierDetail>.Fail(ErrTierNotFound, "No subscription tier exists with the given id.");

            // Draft / Active → Inactive. Archived is terminal.
            if (tier.LifecycleState is not (SubscriptionPlanLifecycleState.Draft or SubscriptionPlanLifecycleState.Active))
                return ApiResponse<AdminTierDetail>.Fail(ErrInvalidTransition,
                    $"Cannot deactivate a tier in state '{tier.LifecycleState}'.");

            var now = DateTime.UtcNow;
            ApplyState(tier, SubscriptionPlanLifecycleState.Inactive, now, actorUserId);
            await _context.SaveChangesAsync();
            await _audit.RecordAsync(
                "TIER_DEACTIVATED", actorUserId, targetBusinessId: null, targetPlanId: tier.Id,
                payloadJson: JsonSerializer.Serialize(new { tier.Id, tier.Key }),
                reason: "Tier deactivated (existing subscribers keep access until expiry).");

            _logger.LogInformation("Admin deactivated subscription tier {TierKey} ({TierId}) by {Actor}.", tier.Key, tier.Id, actorUserId);
            return await GetAsync(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deactivate subscription tier {TierId}.", id);
            return ApiResponse<AdminTierDetail>.Fail("DEACTIVATE_FAILED", "Failed to deactivate the subscription tier.");
        }
    }
public async Task<ApiResponse<AdminTierDetail>> ArchiveAsync(Guid id, Guid? actorUserId)
    {
        try
        {
            var tier = await _context.SubscriptionPlans.FirstOrDefaultAsync(p => p.Id == id);
            if (tier == null)
                return ApiResponse<AdminTierDetail>.Fail(ErrTierNotFound, "No subscription tier exists with the given id.");

            // Draft / Inactive → Archived (terminal). Active must be deactivated first.
            if (tier.LifecycleState is not (SubscriptionPlanLifecycleState.Draft or SubscriptionPlanLifecycleState.Inactive))
                return ApiResponse<AdminTierDetail>.Fail(ErrInvalidTransition,
                    $"Cannot archive a tier in state '{tier.LifecycleState}'. Deactivate it first.");

            var hasActiveSubscribers = await _context.BusinessSubscriptions.AnyAsync(s =>
                s.PlanId == id && (s.Status == "active" || s.Status == "trial") &&
                (!s.EndsAt.HasValue || s.EndsAt > DateTime.UtcNow));
            if (hasActiveSubscribers)
                return ApiResponse<AdminTierDetail>.Fail(ErrActiveSubscribers,
                    "The tier has active subscribers and cannot be archived until they expire or are moved.");

            var now = DateTime.UtcNow;
            ApplyState(tier, SubscriptionPlanLifecycleState.Archived, now, actorUserId);
            tier.ArchivedAt = now;
            await _context.SaveChangesAsync();
            await _audit.RecordAsync(
                "TIER_ARCHIVED", actorUserId, targetBusinessId: null, targetPlanId: tier.Id,
                payloadJson: JsonSerializer.Serialize(new { tier.Id, tier.Key }),
                reason: "Tier archived (terminal).");

            _logger.LogInformation("Admin archived subscription tier {TierKey} ({TierId}) by {Actor}.", tier.Key, tier.Id, actorUserId);
            return await GetAsync(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive subscription tier {TierId}.", id);
            return ApiResponse<AdminTierDetail>.Fail("ARCHIVE_FAILED", "Failed to archive the subscription tier.");
        }
}
// ═════════════════════════════════════════════════════════════
    //  CREATE / UPDATE / DELETE
    // ═════════════════════════════════════════════════════════════

    public async Task<ApiResponse<AdminTierDetail>> CreateAsync(AdminTierCreateRequest request, Guid? actorUserId)
    {
        try
        {
            var key = request.Key.Trim().ToLowerInvariant();
            if (await _context.SubscriptionPlans.AnyAsync(p => p.Key == key))
                return ApiResponse<AdminTierDetail>.Fail(ErrDuplicateKey, $"A tier with key '{key}' already exists.");

            if (request.IsDefault)
                await ClearDefaultFlagAsync();

            var now = DateTime.UtcNow;
            // New tiers always start as Draft (not assignable, IsActive mirrored false).
            var tier = new SubscriptionPlan
            {
                Id = Guid.NewGuid(),
                Key = key,
                Name = request.Name.Trim(),
                Description = Normalize(request.Description),
                Price = request.Price,
                BillingInterval = request.BillingInterval,
                DisplayOrder = request.DisplayOrder,
                IsDefault = request.IsDefault,
                IsActive = false,
                LifecycleState = SubscriptionPlanLifecycleState.Draft,
                CreatedAt = now
            };
            _context.SubscriptionPlans.Add(tier);
            await _context.SaveChangesAsync();

            if (request.ModuleKeys.Count > 0)
            {
                var moduleError = await ApplyModuleSetAsync(tier, request.ModuleKeys);
                if (moduleError != null)
                {
                    // Invalid module set on create — roll back the tier.
                    _context.SubscriptionPlans.Remove(tier);
                    await _context.SaveChangesAsync();
                    var (mErrCode, mErrMsg) = moduleError.Value;
                    return ApiResponse<AdminTierDetail>.Fail(mErrCode, mErrMsg);
                }
                await _context.SaveChangesAsync();
            }

            await _audit.RecordAsync(
                "TIER_CREATED", actorUserId, targetBusinessId: null, targetPlanId: tier.Id,
                payloadJson: JsonSerializer.Serialize(new { tier.Id, tier.Key, tier.Name, tier.Price }),
                reason: "Tier created as Draft.");
_logger.LogInformation("Admin created subscription tier {TierKey} ({TierId}) as Draft by {Actor}.", tier.Key, tier.Id, actorUserId);
            return await GetAsync(tier.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create subscription tier.");
            return ApiResponse<AdminTierDetail>.Fail("CREATE_FAILED", "Failed to create the subscription tier.");
        }
    }

    public async Task<ApiResponse<AdminTierDetail>> UpdateAsync(Guid id, AdminTierUpdateRequest request, Guid? actorUserId)
    {
        try
        {
            var tier = await _context.SubscriptionPlans.FirstOrDefaultAsync(p => p.Id == id);
            if (tier == null)
                return ApiResponse<AdminTierDetail>.Fail(ErrTierNotFound, "No subscription tier exists with the given id.");

            if (tier.LifecycleState == SubscriptionPlanLifecycleState.Archived)
                return ApiResponse<AdminTierDetail>.Fail(ErrInvalidTransition, "Archived tiers cannot be edited.");

            var before = ToPayloadSnapshot(tier);
            tier.Name = request.Name.Trim();
            tier.Description = Normalize(request.Description);
            tier.Price = request.Price;
            tier.BillingInterval = request.BillingInterval;
            tier.DisplayOrder = request.DisplayOrder;

            if (request.IsDefault != tier.IsDefault)
            {
                if (request.IsDefault)
                    await ClearDefaultFlagAsync();
                tier.IsDefault = request.IsDefault;
            }

            await _context.SaveChangesAsync();
            await _audit.RecordAsync(
                "TIER_UPDATED", actorUserId, targetBusinessId: null, targetPlanId: tier.Id,
                payloadJson: JsonSerializer.Serialize(new { before, after = ToPayloadSnapshot(tier) }),
                reason: "Tier metadata updated.");

            _logger.LogInformation("Admin updated subscription tier {TierKey} ({TierId}) by {Actor}.", tier.Key, tier.Id, actorUserId);
            return await GetAsync(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update subscription tier {TierId}.", id);
            return ApiResponse<AdminTierDetail>.Fail("UPDATE_FAILED", "Failed to update the subscription tier.");
        }
    }

    public async Task<ApiResponse<MessageResponse>> DeleteAsync(Guid id, Guid? actorUserId)
    {
        var archive = await ArchiveAsync(id, actorUserId);
        if (!archive.Success)
        {
            return ApiResponse<MessageResponse>.Fail(archive.Error!.Code, archive.Error.Message);
        }
        return ApiResponse<MessageResponse>.Ok(new MessageResponse
        {
            Message = $"Tier '{archive.Data!.Key}' archived."
        });
    }

// ═════════════════════════════════════════════════════════════
    //  TIER MODULES
    // ═════════════════════════════════════════════════════════════

    public async Task<ApiResponse<AdminTierModulesResponse>> GetModulesAsync(Guid id)
    {
        try
        {
            var tier = await _context.SubscriptionPlans
                .AsNoTracking()
                .Include(p => p.PlanModules)
                .ThenInclude(pm => pm.Module)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (tier == null)
                return ApiResponse<AdminTierModulesResponse>.Fail(ErrTierNotFound, "No subscription tier exists with the given id.");

            var included = tier.PlanModules.Select(pm => pm.Module.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var response = new AdminTierModulesResponse
            {
                TierId = tier.Id,
                ModuleKeys = tier.PlanModules.Select(pm => pm.Module.Key).OrderBy(x => x).ToList(),
                Modules = BuildModuleViews(included)
            };
            return ApiResponse<AdminTierModulesResponse>.Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get modules for tier {TierId}.", id);
            return ApiResponse<AdminTierModulesResponse>.Fail("GET_FAILED", "Failed to load tier modules.");
        }
    }

    public async Task<ApiResponse<AdminTierModulesResponse>> SetModulesAsync(Guid id, AdminTierModulesRequest request, Guid? actorUserId)
    {
        try
        {
            var tier = await _context.SubscriptionPlans
                .Include(p => p.PlanModules)
                .ThenInclude(pm => pm.Module)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (tier == null)
                return ApiResponse<AdminTierModulesResponse>.Fail(ErrTierNotFound, "No subscription tier exists with the given id.");

            if (tier.LifecycleState == SubscriptionPlanLifecycleState.Active)
                return ApiResponse<AdminTierModulesResponse>.Fail(ErrActiveModuleEditDisabled,
                    "This tier is live. Deactivate it to change its modules.");

            if (tier.LifecycleState == SubscriptionPlanLifecycleState.Archived)
                return ApiResponse<AdminTierModulesResponse>.Fail(ErrInvalidTransition,
                    "Archived tiers cannot have their modules edited.");

            var moduleError = await ApplyModuleSetAsync(tier, request.ModuleKeys);
            if (moduleError != null)
            {
                var (sErrCode, sErrMsg) = moduleError.Value;
                return ApiResponse<AdminTierModulesResponse>.Fail(sErrCode, sErrMsg);
            }

            await _context.SaveChangesAsync();
            await InvalidateSubscribersAsync(id);
            await _audit.RecordAsync(
                "TIER_MODULES_SET", actorUserId, targetBusinessId: null, targetPlanId: tier.Id,
                payloadJson: JsonSerializer.Serialize(new { moduleKeys = tier.PlanModules.Select(pm => pm.Module.Key).ToList() }),
                reason: request.Reason ?? "Tier module set updated.");

            _logger.LogInformation("Admin set module set for tier {TierKey} ({TierId}) to {Count} modules by {Actor}.",
                tier.Key, tier.Id, tier.PlanModules.Count, actorUserId);
            return await GetModulesAsync(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set modules for tier {TierId}.", id);
            return ApiResponse<AdminTierModulesResponse>.Fail("UPDATE_FAILED", "Failed to update tier modules.");
        }
    }

    public async Task<ApiResponse<AdminModulesCatalogResponse>> GetCatalogModulesAsync()
    {
        try
        {
            var dbModules = (await _context.Modules.AsNoTracking().ToListAsync())
                .ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);

            var catalog = ModuleCatalog.Modules
                .OrderBy(m => m.Visibility)
                .ThenBy(m => m.Name)
                .Select(def =>
                {
                    dbModules.TryGetValue(def.Key, out var row);
                    return new AdminCatalogModule
                    {
                        Key = def.Key,
                        Name = row?.Name ?? def.Name,
                        Description = row?.Description ?? def.Description,
                        IsCore = row?.IsCore ?? def.Visibility == ModuleVisibility.Core,
                        Visibility = def.Visibility.ToString(),
                        Dependencies = def.Dependencies.ToList()
                    };
                })
                .ToList();

            return ApiResponse<AdminModulesCatalogResponse>.Ok(new AdminModulesCatalogResponse { Modules = catalog });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load admin module catalog.");
            return ApiResponse<AdminModulesCatalogResponse>.Fail("CATALOG_FAILED", "Failed to load the module catalog.");
        }
    }
// ═════════════════════════════════════════════════════════════
    //  HELPERS
    // ═════════════════════════════════════════════════════════════

    /// <summary>Atomically replaces a tier's module set (validating existence + dependencies).</summary>
    private async Task<(string Code, string Message)?> ApplyModuleSetAsync(SubscriptionPlan tier, IReadOnlyCollection<string> requestedKeys)
    {
        var normalized = requestedKeys.Select(k => k.Trim().ToLowerInvariant()).Distinct().ToList();
        var modulesByKey = await _context.Modules.Where(m => normalized.Contains(m.Key)).ToDictionaryAsync(m => m.Key, StringComparer.OrdinalIgnoreCase);

        if (modulesByKey.Count != normalized.Count)
        {
            var missing = normalized.First(k => !modulesByKey.ContainsKey(k));
            return (ErrInvalidModule, $"Module '{missing}' does not exist in the catalog.");
        }

        var dependencyProblem = FindDependencyProblem(normalized);
        if (dependencyProblem != null)
            return (ErrDependencyMissing, dependencyProblem);

        // Diff: insert missing rows, remove deleted rows, leave unchanged rows untouched.
        var requestedIds = normalized.Select(k => modulesByKey[k].Id).ToHashSet();
        var existing = tier.PlanModules.Select(pm => pm.ModuleId).ToHashSet();

        foreach (var moduleId in requestedIds.Where(id => !existing.Contains(id)))
        {
            tier.PlanModules.Add(new PlanModule { PlanId = tier.Id, ModuleId = moduleId });
        }

        var removed = tier.PlanModules.Where(pm => !requestedIds.Contains(pm.ModuleId)).ToList();
        foreach (var pm in removed)
        {
            tier.PlanModules.Remove(pm);
            _context.PlanModules.Remove(pm);
        }

        return null;
    }

    /// <summary>
    /// Dependency closure validation using the single source of truth
    /// (<see cref="ModuleCatalog"/>). A non-core included module must have all
    /// of its catalog dependencies either included in the set or be a Core module
    /// (always available to every business).
    /// </summary>
    private static string? FindDependencyProblem(IReadOnlyCollection<string> includedKeys)
    {
        var included = new HashSet<string>(includedKeys, StringComparer.OrdinalIgnoreCase);

        foreach (var key in includedKeys)
        {
            var definition = ModuleCatalog.Find(key);
            if (definition == null) continue;

            foreach (var dependency in definition.Dependencies)
            {
                var depIsCore = ModuleCatalog.Find(dependency)?.Visibility == ModuleVisibility.Core;
                if (!included.Contains(dependency) && depIsCore)
                {
                    // Core dependencies are implicitly available; no problem.
                    continue;
                }
                if (!included.Contains(dependency))
                    return $"Module '{key}' depends on '{dependency}', which is not included in this tier.";
            }
        }

        return null;
    }

    /// <summary>Invalidates every business subscribed to the given tier.</summary>
    private async Task InvalidateSubscribersAsync(Guid planId)
    {
        var businessIds = await _context.BusinessSubscriptions
            .AsNoTracking()
            .Where(s => s.PlanId == planId)
            .Select(s => s.BusinessId)
            .ToListAsync();
        foreach (var businessId in businessIds)
            _entitlements.Invalidate(businessId);
    }

    /// <summary>Clears the default flag on all tiers so a new one may become the sole default.</summary>
    private async Task ClearDefaultFlagAsync()
    {
        var defaults = await _context.SubscriptionPlans.Where(p => p.IsDefault).ToListAsync();
        foreach (var p in defaults)
            p.IsDefault = false;
    }
// ═════════════════════════════════════════════════════════════
    //  MAPPING HELPERS
    // ═════════════════════════════════════════════════════════════

    /// <summary>Applies a lifecycle state transition, keeping the IsActive mirror invariant.</summary>
    private static void ApplyState(SubscriptionPlan tier, SubscriptionPlanLifecycleState next, DateTime now, Guid? actorUserId)
    {
        tier.LifecycleState = next;
        tier.IsActive = next == SubscriptionPlanLifecycleState.Active;

        if (next == SubscriptionPlanLifecycleState.Active)
        {
            tier.PublishedAt = now;
            tier.LastPublishedByUserId = actorUserId;
            tier.DeactivatedAt = null;
            tier.ArchivedAt = null;
        }
        else if (next == SubscriptionPlanLifecycleState.Inactive)
        {
            tier.DeactivatedAt = now;
            tier.ArchivedAt = null;
        }
        else if (next == SubscriptionPlanLifecycleState.Archived)
        {
            tier.ArchivedAt = now;
            tier.DeactivatedAt ??= now;
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Dictionary<string, object?> ToPayloadSnapshot(SubscriptionPlan p) => new()
    {
        ["key"] = p.Key,
        ["name"] = p.Name,
        ["description"] = p.Description,
        ["price"] = p.Price,
        ["billingInterval"] = p.BillingInterval,
        ["isDefault"] = p.IsDefault,
        ["displayOrder"] = p.DisplayOrder
    };

    private static AdminTierSummary ToSummary(SubscriptionPlan p, Dictionary<Guid, int> counts) => new()
    {
        Id = p.Id,
        Key = p.Key,
        Name = p.Name,
        Description = p.Description,
        Price = p.Price,
        BillingInterval = p.BillingInterval,
        Lifecycle = p.LifecycleState.ToString(),
        IsDefault = p.IsDefault,
        DisplayOrder = p.DisplayOrder,
        ModuleCount = p.PlanModules.Count,
        BusinessCount = counts.TryGetValue(p.Id, out var c) ? c : 0,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.DeactivatedAt ?? p.PublishedAt ?? p.CreatedAt
    };

    private static AdminTierDetail ToDetail(SubscriptionPlan p, int businessCount) => new()
    {
        Id = p.Id,
        Key = p.Key,
        Name = p.Name,
        Description = p.Description,
        Price = p.Price,
        BillingInterval = p.BillingInterval,
        Lifecycle = p.LifecycleState.ToString(),
        IsDefault = p.IsDefault,
        DisplayOrder = p.DisplayOrder,
        PublishedAt = p.PublishedAt,
        DeactivatedAt = p.DeactivatedAt,
        ArchivedAt = p.ArchivedAt,
        LastPublishedByUserId = p.LastPublishedByUserId,
        CreatedAt = p.CreatedAt,
        ModuleCount = p.PlanModules.Count,
        BusinessCount = businessCount,
        Modules = BuildModuleViews(p.PlanModules.Select(pm => pm.Module.Key).ToHashSet(StringComparer.OrdinalIgnoreCase))
    };

    private static List<TierModuleView> BuildModuleViews(HashSet<string> includedKeys) =>
        ModuleCatalog.Modules
            .OrderBy(m => m.Visibility)
            .ThenBy(m => m.Name)
            .Select(def => new TierModuleView
            {
                Key = def.Key,
                Name = def.Name,
                Description = def.Description,
                IsCore = def.Visibility == ModuleVisibility.Core,
                Visibility = def.Visibility.ToString(),
                Included = includedKeys.Contains(def.Key),
                Dependencies = def.Dependencies.ToList()
            })
            .ToList();
}