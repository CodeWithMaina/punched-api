using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// The <b>effective</b> provider model a business is running under, after
/// resolving its persisted <see cref="Business.OperatingMode"/> against its
/// module entitlements. This — not the raw operating mode — is what the
/// booking flow, availability engine and staff surfaces consult.
/// </summary>
public static class EffectiveProviderModel
{
    /// <summary>Owner is the sole provider; no staff layer exists.</summary>
    public const string Solo = "solo";

    /// <summary>
    /// The Staff module is not entitled, so the owner is treated as the
    /// provider for every service even though the business may be flagged
    /// multi-staff. The business stays fully operational without staff.
    /// </summary>
    public const string OwnerOnly = "owner_only";

    /// <summary>A team of providers; eligibility is resolved from categories.</summary>
    public const string CategoryBased = "category_based";
}

/// <summary>Resolved provider eligibility for one request (a service selection).</summary>
public sealed class ProviderEligibilityResult
{
    /// <summary>One of the <see cref="EffectiveProviderModel"/> constants.</summary>
    public string Model { get; init; } = EffectiveProviderModel.CategoryBased;

    /// <summary>The single provider when the model collapses to the owner (null if none).</summary>
    public Guid? SoloProviderId { get; init; }

    /// <summary>True when a customer must choose a provider.</summary>
    public bool RequiresProviderSelection => Model == EffectiveProviderModel.CategoryBased;

    /// <summary>Providers eligible for every requested service. Empty means "not bookable".</summary>
    public IReadOnlyList<Guid> EligibleProviderIds { get; init; } = Array.Empty<Guid>();
}

/// <summary>
/// <b>The single, reusable provider-eligibility mechanism.</b> Every surface
/// that needs to know "who can perform this?" — public booking, availability,
/// appointment creation/validation, staff management and the owner console —
/// resolves through here, so the rules live in exactly one place.
/// </summary>
public interface IProviderEligibilityService
{
    /// <summary>The effective provider model for a business.</summary>
    Task<string> ResolveModelAsync(Guid businessId, CancellationToken ct = default);

    /// <summary>The business owner's user id (active Owner membership), or null.</summary>
    Task<Guid?> ResolveOwnerIdAsync(Guid businessId, CancellationToken ct = default);

    /// <summary>
    /// The owner as a provider: their id (or null) and whether they have opted
    /// IN to providing services. Solo businesses default to opt-in; in a
    /// multi-staff business the owner opts in by assigning themselves categories.
    /// </summary>
    Task<(Guid? OwnerId, bool IsProvider)> ResolveOwnerProviderAsync(Guid businessId, CancellationToken ct = default);

    /// <summary>Providers eligible to take <b>all</b> of the requested services.</summary>
    Task<ProviderEligibilityResult> ResolveForServicesAsync(
        Guid businessId, IReadOnlyCollection<Guid> serviceIds, CancellationToken ct = default);

    /// <summary>The authoritative server-side gate for booking and reschedule flows.</summary>
    Task<bool> IsEligibleAsync(
        Guid businessId, Guid providerUserId, IReadOnlyCollection<Guid> serviceIds, CancellationToken ct = default);

    /// <summary>Provider ids (staff and/or owner) assigned to a single category.</summary>
    Task<IReadOnlyList<Guid>> ResolveProviderIdsForCategoryAsync(
        Guid businessId, Guid categoryId, CancellationToken ct = default);

    /// <summary>The category ids a provider is assigned to.</summary>
    Task<HashSet<Guid>> ResolveCategoryIdsForProviderAsync(
        Guid businessId, Guid providerUserId, CancellationToken ct = default);
}

/// <summary>
/// Default implementation. Reads only what a single decision needs (2–4 indexed
/// queries at most) and never materialises per-service eligibility rows.
/// </summary>
public sealed class ProviderEligibilityService : IProviderEligibilityService
{
    /// <summary>The module key that gates the whole staff layer.</summary>
    public const string StaffModuleKey = "staff";

    private readonly ApplicationDbContext _context;
    private readonly IModuleEntitlementService? _entitlements;

    public ProviderEligibilityService(
        ApplicationDbContext context,
        IModuleEntitlementService? entitlements = null)
    {
        _context = context;
        _entitlements = entitlements;
    }

    public async Task<Guid?> ResolveOwnerIdAsync(Guid businessId, CancellationToken ct = default) =>
        (await ResolveOwnerProviderAsync(businessId, ct)).OwnerId;

    public async Task<(Guid? OwnerId, bool IsProvider)> ResolveOwnerProviderAsync(
        Guid businessId, CancellationToken ct = default)
    {
        if (businessId == Guid.Empty) return (null, false);

        // The owner's own `IsBookable` is the "I provide services" switch: it is
        // true by default (so a solo owner is a provider out of the box) and an
        // owner can turn it off in any business type.
        var owner = await _context.BusinessMemberships.AsNoTracking()
            .Where(m => m.BusinessId == businessId &&
                        m.Role == BusinessMembershipRole.Owner &&
                        m.Status == BusinessMembershipStatus.Active)
            .Select(m => new { m.UserId, m.IsBookable })
            .FirstOrDefaultAsync(ct);

        return owner == null ? (null, false) : (owner.UserId, owner.IsBookable);
    }

    public async Task<string> ResolveModelAsync(Guid businessId, CancellationToken ct = default)
    {
        if (businessId == Guid.Empty) return EffectiveProviderModel.OwnerOnly;

        var operatingMode = await _context.Businesses.AsNoTracking()
            .Where(b => b.Id == businessId)
            .Select(b => b.OperatingMode)
            .FirstOrDefaultAsync(ct);

        if (BusinessOperatingModes.IsSolo(operatingMode))
            return EffectiveProviderModel.Solo;

        var staffEntitled = _entitlements == null ||
            await _entitlements.IsModuleEnabledAsync(businessId, StaffModuleKey);

        return staffEntitled
            ? EffectiveProviderModel.CategoryBased
            : EffectiveProviderModel.OwnerOnly;
    }

    public async Task<ProviderEligibilityResult> ResolveForServicesAsync(
        Guid businessId, IReadOnlyCollection<Guid> serviceIds, CancellationToken ct = default)
    {
        var requested = (serviceIds ?? Array.Empty<Guid>()).Where(id => id != Guid.Empty).Distinct().ToArray();

        var model = await ResolveModelAsync(businessId, ct);
        var (ownerId, ownerIsProvider) = await ResolveOwnerProviderAsync(businessId, ct);

        // Models that collapse to the owner never look at staff or categories.
        // The owner is the provider unless they have opted OUT of providing
        // services (their membership `IsBookable` is false).
        if (model != EffectiveProviderModel.CategoryBased)
        {
            var ownerOnly = ownerId is Guid o && ownerIsProvider ? new[] { o } : Array.Empty<Guid>();
            return new ProviderEligibilityResult
            {
                Model = model,
                SoloProviderId = ownerOnly.Length == 1 ? ownerOnly[0] : null,
                EligibleProviderIds = ownerOnly
            };
        }

        var staffIds = await _context.BusinessMemberships.AsNoTracking()
            .Where(m => m.BusinessId == businessId &&
                        m.Role == BusinessMembershipRole.Staff &&
                        m.Status == BusinessMembershipStatus.Active &&
                        m.IsBookable)
            .Select(m => m.UserId)
            .ToListAsync(ct);

        // No team yet → the owner stands in (if they provide services) so the
        // business stays bookable.
        if (staffIds.Count == 0)
        {
            var fallback = ownerId is Guid soloOwner && ownerIsProvider
                ? new[] { soloOwner }
                : Array.Empty<Guid>();
            return new ProviderEligibilityResult
            {
                Model = model,
                SoloProviderId = fallback.Length == 1 ? fallback[0] : null,
                EligibleProviderIds = fallback
            };
        }

        var providerIds = new HashSet<Guid>(staffIds);
        if (ownerId is Guid owner && ownerIsProvider) providerIds.Add(owner);

        var eligible = await ComputeCategoryEligibleAsync(businessId, providerIds, requested, ct);

        return new ProviderEligibilityResult
        {
            Model = model,
            SoloProviderId = null,
            EligibleProviderIds = eligible
        };
    }

    public async Task<bool> IsEligibleAsync(
        Guid businessId, Guid providerUserId, IReadOnlyCollection<Guid> serviceIds, CancellationToken ct = default)
    {
        if (providerUserId == Guid.Empty) return false;

        var result = await ResolveForServicesAsync(businessId, serviceIds, ct);
        return result.EligibleProviderIds.Contains(providerUserId);
    }

    public async Task<IReadOnlyList<Guid>> ResolveProviderIdsForCategoryAsync(
        Guid businessId, Guid categoryId, CancellationToken ct = default)
    {
        if (businessId == Guid.Empty || categoryId == Guid.Empty) return Array.Empty<Guid>();

        return await _context.StaffServiceCategories.AsNoTracking()
            .Where(a => a.BusinessId == businessId && a.ServiceCategoryId == categoryId)
            .Select(a => a.StaffUserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<HashSet<Guid>> ResolveCategoryIdsForProviderAsync(
        Guid businessId, Guid providerUserId, CancellationToken ct = default)
    {
        if (businessId == Guid.Empty || providerUserId == Guid.Empty) return new HashSet<Guid>();

        var ids = await _context.StaffServiceCategories.AsNoTracking()
            .Where(a => a.BusinessId == businessId && a.StaffUserId == providerUserId)
            .Select(a => a.ServiceCategoryId)
            .ToListAsync(ct);

        return ids.ToHashSet();
    }

    /// <summary>
    /// The heart of category-based eligibility: a provider is eligible when, for
    /// every requested service, the service's category is among the provider's
    /// assigned categories (or — for an uncategorised legacy service — the
    /// explicit legacy assignment still exists).
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ComputeCategoryEligibleAsync(
        Guid businessId,
        IReadOnlyCollection<Guid> providerIds,
        Guid[] requestedServiceIds,
        CancellationToken ct)
    {
        if (requestedServiceIds.Length == 0 || providerIds.Count == 0)
            return Array.Empty<Guid>();

        // service → category (null when uncategorised). A service id from another
        // tenant simply will not resolve and cannot become bookable.
        var serviceCategories = await _context.ServiceCatalogItems.AsNoTracking()
            .Where(s => s.BusinessId == businessId && requestedServiceIds.Contains(s.Id))
            .Select(s => new { s.Id, s.CategoryId })
            .ToListAsync(ct);

        if (serviceCategories.Count != requestedServiceIds.Length)
            return Array.Empty<Guid>(); // one or more services are not of this business

        var categoryByService = serviceCategories.ToDictionary(s => s.Id, s => s.CategoryId);

        // All provider → category assignments for this tenant, in one query.
        var providerIdList = providerIds.ToList();
        var providerCategories = (await _context.StaffServiceCategories.AsNoTracking()
                .Where(a => a.BusinessId == businessId && providerIdList.Contains(a.StaffUserId))
                .Select(a => new { a.StaffUserId, a.ServiceCategoryId })
                .ToListAsync(ct))
            .GroupBy(a => a.StaffUserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ServiceCategoryId).ToHashSet());

        // Legacy fallback for uncategorised services. A service with no category
        // keeps working for the providers explicitly assigned it; and if NO
        // provider in the business has such an assignment at all, it is treated
        // as open to every provider — an uncategorised service must never become
        // silently unbookable during migration.
        var uncategorised = requestedServiceIds
            .Where(id => categoryByService.TryGetValue(id, out var c) && c == null)
            .ToArray();

        var legacy = new HashSet<(Guid Provider, Guid Service)>();
        var servicesWithExplicitAssignment = new HashSet<Guid>();
        if (uncategorised.Length > 0)
        {
            var rows = await _context.StaffServiceAssignments.AsNoTracking()
                .Where(a => a.BusinessId == businessId && uncategorised.Contains(a.ServiceCatalogItemId))
                .Select(a => new { a.StaffUserId, a.ServiceCatalogItemId })
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                servicesWithExplicitAssignment.Add(row.ServiceCatalogItemId);
                if (providerIds.Contains(row.StaffUserId))
                    legacy.Add((row.StaffUserId, row.ServiceCatalogItemId));
            }
        }

        bool ProviderCoversService(Guid providerId, Guid serviceId)
        {
            var categoryId = categoryByService[serviceId];

            if (categoryId is Guid category)
                return providerCategories.TryGetValue(providerId, out var cats) && cats.Contains(category);

            // Uncategorised: an explicit legacy assignment wins; with no explicit
            // assignment anywhere, the service is open to all providers.
            if (legacy.Contains((providerId, serviceId))) return true;
            return !servicesWithExplicitAssignment.Contains(serviceId);
        }

        return providerIdList
            .Where(p => requestedServiceIds.All(s => ProviderCoversService(p, s)))
            .ToList();
    }
}

