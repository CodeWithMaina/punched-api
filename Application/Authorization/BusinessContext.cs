using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Modules;
using PunchedApi.Application.Services;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Authorization;

/// <summary>
/// Request-scoped module authorization context. Resolves the caller's
/// business/role once per request (server-side only — never from client
/// input), loads effective module entitlements lazily on the first module
/// check, applies the dependency closure, and memoizes so every subsequent
/// <c>[RequireModule]</c> check in the request is an in-memory lookup.
/// </summary>
public interface IBusinessContext
{
    /// <summary>The caller's role (Customer, Business, Staff, Admin) or null.</summary>
    string? GetRole();

    /// <summary>
    /// Server-resolved business id for the caller: owner via
    /// IBusinessScopeResolver, staff via User.StaffBusinessId. Null for
    /// Customer/Admin. Memoized per request.
    /// </summary>
    Task<Guid?> GetBusinessIdAsync();

    /// <summary>
    /// Effective module keys (entitlements + closed dependencies), loaded
    /// lazily and memoized. Empty until first load.
    /// </summary>
    HashSet<string> EffectiveModules { get; }

    /// <summary>Does the caller have access to the given module?</summary>
    Task<bool> HasModuleAsync(string moduleKey);
}

public sealed class BusinessContext : IBusinessContext
{
    private readonly IBusinessScopeResolver _scopeResolver;
    private readonly IModuleEntitlementService _entitlementService;
    private readonly ApplicationDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// The active tenant (null on the platform root / in unit tests). Optional
    /// so every existing construction site — including the test suite — keeps
    /// working unchanged.
    /// </summary>
    private readonly ITenantContext? _tenant;

    private Guid? _businessId;
    private bool _businessIdResolved;
    private HashSet<string>? _effectiveModules;

    /// <summary>
    /// Dependency-closed effective modules per business, memoized for the
    /// request. Keyed by business rather than a single field because a Customer
    /// caller resolves entitlements for the TENANT business, which is not their
    /// own — the two must not overwrite each other.
    /// </summary>
    private readonly Dictionary<Guid, HashSet<string>> _effectiveModulesByBusiness = new();

    public BusinessContext(
        IBusinessScopeResolver scopeResolver,
        IModuleEntitlementService entitlementService,
        ApplicationDbContext context,
        IHttpContextAccessor httpContextAccessor,
        ITenantContext? tenant = null)
    {
        _scopeResolver = scopeResolver;
        _entitlementService = entitlementService;
        _context = context;
        _httpContextAccessor = httpContextAccessor;
        _tenant = tenant;
    }

    public string? GetRole() =>
        _httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.Role)
        ?? _httpContextAccessor.HttpContext?.User.FindFirstValue("role");

    private Guid? GetUserId()
    {
        var claim = _httpContextAccessor.HttpContext?.User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    public async Task<Guid?> GetBusinessIdAsync()
    {
        if (_businessIdResolved) return _businessId;
        _businessIdResolved = true;

        var role = GetRole();
        var userId = GetUserId();
        if (userId == null) return _businessId = null;

        _businessId = role switch
        {
            // Owner: via the existing cached resolver (never from client input).
            "Business" => await _scopeResolver.GetOwnedBusinessIdAsync(userId.Value),

            // Staff: resolve the linked business server-side.
            "Staff" => await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == userId.Value)
                .Select(u => u.StaffBusinessId)
                .FirstOrDefaultAsync(),

            // Customers and Admins are not business-scoped.
            _ => null
        };

        return _businessId;
    }

    public HashSet<string> EffectiveModules =>
        _effectiveModules ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> HasModuleAsync(string moduleKey)
    {
        var role = GetRole();

        // Admin is platform-level; module gating does not apply.
        if (role == "Admin") return true;

        // Customers: the module must (a) be a module that has a customer
        // surface at all, and (b) be part of the entitlements of the business
        // the customer is actually acting on.
        //
        // Before this, the Customer branch only answered "is this module
        // customer-facing in the catalog?", which made every [RequireModule]
        // gate on the customer surface (appointments, cards/loyalty, stamps,
        // rewards/redemptions, payments, referrals, services) a no-op: a
        // customer could call the appointment API of a business whose
        // Appointments module was switched off. The catalog question is still
        // the first half of the answer, but it is no longer the whole answer.
        if (role == "Customer")
        {
            if (!CustomerCapabilityCatalog.IsCustomerFacing(moduleKey)) return false;

            // The active tenant is the server-side answer to "which business?".
            // It is populated by TenantConsistencyMiddleware from
            // X-Punched-Tenant / the API host BEFORE any controller runs, and
            // the caller's membership of it has already been verified there.
            // It can only ever NARROW access.
            var tenantBusinessId = _tenant?.BusinessId;
            if (tenantBusinessId == null)
            {
                // Platform root: there is no server-side business signal to
                // check against, so the legacy read-side answer stands. The
                // customer app resolves the business client-side and hides
                // unavailable capability surfaces; the API stays authoritative
                // whenever a tenant IS supplied.
                return true;
            }

            return CustomerCapabilityCatalog.CustomerMayUseModule(
                moduleKey,
                await GetEffectiveModulesAsync(tenantBusinessId.Value));
        }

        // Business/Staff: resolve business + entitlements once, memoize.
        var businessId = await GetBusinessIdAsync();
        if (businessId == null) return false;

        var effective = await GetEffectiveModulesAsync(businessId.Value);
        return effective.Contains(moduleKey);
    }

    /// <summary>
    /// The dependency-closed effective module set for a business, resolved once
    /// per request (and per business) via <see cref="IModuleEntitlementService"/>.
    /// </summary>
    private async Task<HashSet<string>> GetEffectiveModulesAsync(Guid businessId)
    {
        if (_effectiveModulesByBusiness.TryGetValue(businessId, out var cached))
            return cached;

        var resolved = ModuleCatalog.CloseDependencies(
            await _entitlementService.GetEffectiveModuleKeysAsync(businessId));

        _effectiveModulesByBusiness[businessId] = resolved;

        // Keep EffectiveModules pointing at the caller's own business for the
        // existing consumers of the property (it stays empty for a Customer,
        // who has no owned business).
        if (_effectiveModules == null && await GetBusinessIdAsync() == businessId)
            _effectiveModules = resolved;

        return resolved;
    }
}
