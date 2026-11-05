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

    /// <summary>
    /// Key of the shared generation marker. Bumping it orphans every cached
    /// entry in one step, which is how <see cref="InvalidateAll"/> works without
    /// having to enumerate (and therefore know) every business's cache key.
    /// </summary>
    private const string GenerationKey = "modules:entitlements:generation";

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

        // Seed the generation marker up front. Creating it lazily would let two
        // concurrent first calls each build their own marker; one of them would
        // then be invisible to readers, so a bump written to it would be lost and
        // a stale entry could survive.
        Generation();
    }

    /// <summary>Mutable generation marker; a field so it can be read/incremented atomically.</summary>
    private sealed class CacheGeneration
    {
        public long Value;
    }

    /// <summary>The shared generation marker for this cache instance.</summary>
    private CacheGeneration Generation() =>
        _cache.GetOrCreate(GenerationKey, _ => new CacheGeneration())!;

    private long CurrentGeneration() => Interlocked.Read(ref Generation().Value);

    /// <summary>
    /// A resolved entitlement list, stamped with the generation it was written
    /// under. A mismatch means a platform-wide change has happened since, so the
    /// entry is treated as a miss even though it is still in the cache.
    /// </summary>
    private sealed record CachedEntitlements(ModuleEntitlementResult Result, long Generation);

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
        if (_cache.TryGetValue(CacheKey(businessId), out CachedEntitlements? cached) &&
            cached != null &&
            cached.Generation == CurrentGeneration())
        {
            _logger.LogDebug("Entitlement cache hit for business {BusinessId}.", businessId);
            return cached.Result;
        }

        var result = await ResolveAsync(businessId);
        _cache.Set(
            CacheKey(businessId),
            new CachedEntitlements(result, CurrentGeneration()),
            TimeSpan.FromSeconds(CacheTtlSeconds));
        return result;
    }

    private async Task<ModuleEntitlementResult> ResolveAsync(Guid businessId)
    {
        var result = new ModuleEntitlementResult();

        // Platform-administration gate: a suspended or deactivated business holds
        // no module access regardless of its subscription state. The business row
        // is the authority (the global query filter also excludes soft-deleted rows).
        var businessStatus = await _context.Businesses
            .AsNoTracking()
            .Where(b => b.Id == businessId)
            .Select(b => b.Status)
            .FirstOrDefaultAsync();
        var businessActive = businessStatus == BusinessStatus.Active;

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
            SubscriptionStatus.GrantsAccess(subscription.Status) &&
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

            entitlement.HasAccess = entitlement.IsEnabled && subscriptionActive && businessActive;
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

    /// <inheritdoc />
    public void InvalidateAll()
    {
        // Bump the shared generation. Every entry already in the cache is now
        // stamped with an older generation and is treated as a miss on its next
        // read, so the whole cache is dropped in O(1) without enumerating keys.
        var generation = Interlocked.Increment(ref Generation().Value);
        _logger.LogWarning(
            "Entitlement cache invalidated for ALL businesses (generation {Generation}).", generation);
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