using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PunchedApi.Application.Modules;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Resolves effective module entitlements: plan grants layered with
/// per-business overrides, gated by subscription status/expiry.
/// Results are cached per business (60 s TTL) in <see cref="IMemoryCache"/>;
/// call <see cref="Invalidate"/> after any entitlement mutation.
/// Read-only with respect to entitlement data — never mutates it.
/// </summary>
public class ModuleEntitlementService : IModuleEntitlementService
{
    private const int CacheTtlSeconds = 60;

    private readonly ApplicationDbContext _context;
    private readonly ILogger<ModuleEntitlementService> _logger;
    private readonly IMemoryCache _cache;

    public ModuleEntitlementService(
        ApplicationDbContext context,
        ILogger<ModuleEntitlementService> logger,
        IMemoryCache? cache = null)
    {
        _context = context;
        _logger = logger;
        _cache = cache ?? new MemoryCache(new MemoryCacheOptions());
    }

    /// <summary>Cache key for a business's resolved entitlements.</summary>
    internal static string CacheKey(Guid businessId) => $"modules:entitlements:{businessId}";

    /// <inheritdoc />
    public IReadOnlyList<string> ValidateConfiguration(IEnumerable<(string ModuleKey, bool Enabled)> overrides)
    {
        var problems = new List<string>();
        var state = overrides
            .GroupBy(o => o.ModuleKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Enabled, StringComparer.OrdinalIgnoreCase);

        foreach (var (moduleKey, enabled) in state)
        {
            if (!enabled) continue;

            var definition = ModuleCatalog.Find(moduleKey);
            if (definition == null) continue;

            foreach (var dependency in definition.Dependencies)
            {
                var depEnabled = state.TryGetValue(dependency, out var on) && on;
                if (!depEnabled)
                    problems.Add($"module '{moduleKey}' enabled without dependency '{dependency}'");
            }
        }

        return problems;
    }

    public async Task<ModuleEntitlementResult> GetBusinessModulesAsync(Guid businessId, Guid? userId = null)
    {
        if (_cache.TryGetValue(CacheKey(businessId), out ModuleEntitlementResult? cached) && cached != null)
        {
            _logger.LogDebug("Entitlement cache hit for business {BusinessId}.", businessId);
            return cached;
        }

        var result = await ResolveAsync(businessId);
        _cache.Set(CacheKey(businessId), result, TimeSpan.FromSeconds(CacheTtlSeconds));
        return result;
    }

    private async Task<ModuleEntitlementResult> ResolveAsync(Guid businessId)
    {
        var result = new ModuleEntitlementResult();

        // Project only the fields entitlement resolution actually needs from the
        // subscription → plan → plan-modules graph (plan key/name + the set of
        // granted module ids). No entity tracking, no Module rows loaded through
        // the plan (we already load the active catalog separately below), and a
        // single round trip instead of the previous AsSplitQuery fan-out.
        var subscription = await _context.BusinessSubscriptions
            .AsNoTracking()
            .Where(s => s.BusinessId == businessId &&
                (s.Status == "active" || s.Status == "trial"))
            .Select(s => new
            {
                s.Status,
                s.EndsAt,
                PlanKey = s.Plan.Key,
                PlanName = s.Plan.Name,
                PlanModuleIds = s.Plan.PlanModules.Select(pm => pm.ModuleId).ToList()
            })
            .FirstOrDefaultAsync();

        if (subscription != null)
        {
            result.CurrentPlan = new SubscriptionPlan
            {
                Key = subscription.PlanKey,
                Name = subscription.PlanName
            };
            result.SubscriptionEndsAt = subscription.EndsAt;
            result.SubscriptionStatus = subscription.Status;
        }

        var subscriptionActive = subscription != null &&
            (subscription.Status == "active" || subscription.Status == "trial") &&
            (!subscription.EndsAt.HasValue || subscription.EndsAt > DateTime.UtcNow);

        var planModuleIds = subscription?.PlanModuleIds.ToHashSet() ?? new HashSet<Guid>();

        // Active module catalog — read-only, no tracking, only the columns the
        // entitlement projection consumes.
        var allModules = await _context.Modules
            .AsNoTracking()
            .Where(m => m.IsActive)
            .Select(m => new
            {
                m.Id,
                m.Key,
                m.Name,
                m.Description,
                m.IsCore,
                m.DependenciesJson
            })
            .ToListAsync();

        // Per-business overrides — read-only, projected to the few columns used.
        var businessOverrides = await _context.BusinessModules
            .AsNoTracking()
            .Where(bm => bm.BusinessId == businessId)
            .Select(bm => new
            {
                bm.ModuleId,
                bm.IsEnabled,
                bm.Source,
                bm.Reason
            })
            .ToListAsync();

        foreach (var module in allModules)
        {
            var entitlement = new ModuleEntitlement
            {
                Id = module.Id,
                Key = module.Key,
                Name = module.Name,
                Description = module.Description,
                IsCore = module.IsCore,
                Dependencies = ParseDependencies(module.DependenciesJson)
            };

            var overrideEntry = businessOverrides.FirstOrDefault(bm => bm.ModuleId == module.Id);
            if (overrideEntry != null)
            {
                entitlement.IsEnabled = overrideEntry.IsEnabled;
                entitlement.Source = overrideEntry.Source;
                entitlement.Reason = overrideEntry.Reason;
            }
            else
            {
                entitlement.IsEnabled = planModuleIds.Contains(module.Id);
                entitlement.Source = "PLAN";
            }

            entitlement.HasAccess = entitlement.IsEnabled && subscriptionActive;
            result.Modules.Add(entitlement);
        }

        _logger.LogDebug(
            "Resolved {Total} module entitlements for business {BusinessId}; {Enabled} with access (subscriptionActive={SubscriptionActive}).",
            result.Modules.Count, businessId, result.Modules.Count(m => m.HasAccess), subscriptionActive);

        return result;
    }

    public async Task<bool> IsModuleEnabledAsync(Guid businessId, string moduleKey)
    {
        var entitlements = await GetBusinessModulesAsync(businessId);
        return entitlements.Modules.Any(m => m.Key == moduleKey && m.HasAccess);
    }

    public async Task<HashSet<string>> GetEffectiveModuleKeysAsync(Guid businessId)
    {
        var entitlements = await GetBusinessModulesAsync(businessId);
        return entitlements.Modules.Where(m => m.HasAccess).Select(m => m.Key).ToHashSet();
    }

    /// <inheritdoc />
    public void Invalidate(Guid businessId)
    {
        _cache.Remove(CacheKey(businessId));
        _logger.LogDebug("Entitlement cache invalidated for business {BusinessId}.", businessId);
    }

    /// <summary>
    /// Safely parses the module's dependency JSON array; invalid JSON degrades
    /// to an empty dependency list rather than throwing.
    /// </summary>
    private static List<string> ParseDependencies(string? dependenciesJson)
    {
        if (string.IsNullOrEmpty(dependenciesJson)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(dependenciesJson) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }
}