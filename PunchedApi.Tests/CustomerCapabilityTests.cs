using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.Modules;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.SeedData;

namespace PunchedApi.Tests;

/// <summary>
/// Customer-capability matrix: a business's EFFECTIVE entitlements projected on
/// to the customer-facing capability set, and the tenant-scoped
/// <c>[RequireModule]</c> answer a CUSTOMER caller now gets.
///
/// <para>Two things are pinned here:</para>
/// <list type="number">
/// <item>The capability projection is scoped per business, derived from real
/// plan/override entitlements, and never exposes a business/internal module
/// (attendance, analytics, staff, settings, customers, programs,
/// customCardDesign).</item>
/// <item>When a tenant IS active, a customer's module access is the
/// intersection of "has a customer surface" and "this business has the
/// module" — so an appointment/rewards/payments API call for a business that
/// switched the module off is refused, not silently allowed.</item>
/// </list>
/// </summary>
public class CustomerCapabilityTests : IDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly ModuleEntitlementService _entitlements;
    private readonly string _dbName = Guid.NewGuid().ToString();

    public CustomerCapabilityTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options;
        _db = new ApplicationDbContext(options);
        _entitlements = new ModuleEntitlementService(_db, TestHelpers.CreateLogger<ModuleEntitlementService>());

        _db.Modules.AddRange(ModuleSeedData.GetModules());
        _db.SubscriptionPlans.AddRange(SubscriptionPlanSeedData.GetPlans());
        _db.SaveChanges();

        var modules = _db.Modules.ToDictionary(m => m.Key);
        var plans = _db.SubscriptionPlans.ToDictionary(p => p.Key);
        foreach (var (planKey, moduleKey) in PlanModuleSeedData.GetPlanModules())
            _db.PlanModules.Add(new PlanModule { PlanId = plans[planKey].Id, ModuleId = modules[moduleKey].Id });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    // ── Fixture helpers ─────────────────────────────────────────

    private async Task<Business> CreateBusinessAsync(string name)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(), Name = name, Category = "salon",
            Location = "Nairobi", MpesaNumber = "123456"
        };
        _db.Businesses.Add(business);
        await _db.SaveChangesAsync();
        return business;
    }

    private async Task SubscribeAsync(Guid businessId, string planKey)
    {
        var plan = _db.SubscriptionPlans.Single(p => p.Key == planKey);
        _db.BusinessSubscriptions.Add(new BusinessSubscription
        {
            BusinessId = businessId, PlanId = plan.Id, Status = "active",
            StartsAt = DateTime.UtcNow.AddDays(-30)
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Grants a module AND its dependencies, mirroring how plans are authored
    /// (every grant covers its dependencies explicitly).
    /// </summary>
    private async Task EnableModuleAsync(Guid businessId, string moduleKey)
    {
        foreach (var key in ModuleCatalog.CloseDependencies(new[] { moduleKey }))
        {
            var module = _db.Modules.Single(m => m.Key == key);
            _db.BusinessModules.Add(new BusinessModule
            {
                BusinessId = businessId, ModuleId = module.Id, IsEnabled = true,
                Source = "ADMIN", OverridesAt = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync();
    }

    /// <summary>Adds an active loyalty program so the loyalty capability resolves.</summary>
    private async Task AddActiveLoyaltyProgramAsync(Business business)
    {
        _db.LoyaltyPrograms.Add(new LoyaltyProgram
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Name = "Rewards",
            Description = "Stamp card",
            StampsRequired = 8,
            RewardDescription = "Free service",
            IsActive = true,
            Status = ProgramStatus.Active
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>The capability payload the customer app receives for a business.</summary>
    private async Task<IReadOnlyDictionary<string, bool>> CapabilitiesAsync(
        Guid businessId, bool loyaltyProgram = false, bool referralProgram = false)
    {
        var keys = await _entitlements.GetEffectiveModuleKeysAsync(businessId);
        return CustomerCapabilityCatalog.Resolve(keys, loyaltyProgram, referralProgram);
    }

    private BusinessContext CreateContext(string role, Guid? ownedBusinessId, ITenantContext? tenant = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        claims.Add(new Claim("userId", Guid.NewGuid().ToString()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return new BusinessContext(
            new StubScopeResolver(ownedBusinessId),
            new ModuleEntitlementService(_db, TestHelpers.CreateLogger<ModuleEntitlementService>()),
            new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(_dbName).Options),
            accessor,
            tenant);
    }

    private sealed class StubScopeResolver : IBusinessScopeResolver
    {
        private readonly Guid? _businessId;
        public StubScopeResolver(Guid? businessId) => _businessId = businessId;
        public Task<Guid?> GetOwnedBusinessIdAsync(Guid ownerId) => Task.FromResult(_businessId);
        public void InvalidateOwner(Guid ownerId) { }
    }

    /// <summary>Minimal tenant context: only the resolved business matters here.</summary>
    private sealed class StubTenantContext : ITenantContext
    {
        public StubTenantContext(Guid? businessId) => BusinessId = businessId;
        public string? Slug => null;
        public Guid? BusinessId { get; }
        public bool IsActive => BusinessId.HasValue;
    }

    // ══ 1. Catalog shape: customer-facing vs business/internal ═══

    [Fact]
    public void OnlyCustomerFacingModules_DeclareACapability()
    {
        var customerFacing = ModuleCatalog.Modules
            .Where(m => m.CustomerCapability != null)
            .Select(m => m.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "appointments", "loyalty", "notifications", "payments",
                "referral", "rewards", "serviceCatalog", "stamps",
            },
            customerFacing);
    }

    [Fact]
    public void EveryDeclaredCapability_IsAKnownCustomerCapabilityKey()
    {
        foreach (var module in ModuleCatalog.Modules)
        {
            if (module.CustomerCapability == null) continue;
            Assert.True(
                CustomerCapabilityCatalog.IsKnownCapability(module.CustomerCapability),
                $"module '{module.Key}' declares unknown customer capability '{module.CustomerCapability}'");
        }
    }

    [Fact]
    public void InternalModules_AreNeverCustomerFacing()
    {
        // A business may have ANY of these enabled without its customers
        // gaining a section, a route or an action.
        foreach (var key in new[] { "attendance", "analytics", "staff", "settings", "customers", "programs", "customCardDesign" })
        {
            Assert.False(CustomerCapabilityCatalog.IsCustomerFacing(key), $"'{key}' must not be customer-facing");
        }
    }

    [Fact]
    public void StampsModule_MapsOnToTheLoyaltyCustomerSurface()
    {
        // QrController is [Authorize(Roles = "Customer")] + [RequireModule("stamps")],
        // so stamps must be reachable by a customer — as part of loyalty, not as
        // a capability of its own.
        Assert.True(CustomerCapabilityCatalog.IsCustomerFacing("stamps"));
        Assert.Contains("stamps", CustomerCapabilityCatalog.ModulesFor(CustomerCapabilityCatalog.Loyalty));
        Assert.DoesNotContain(CustomerCapabilityCatalog.Loyalty, CustomerCapabilityCatalog.AllKeys.Where(k => k == "stamps"));
    }

    [Fact]
    public void Resolve_AlwaysReturnsTheFullKeySet()
    {
        var caps = CustomerCapabilityCatalog.Resolve(Array.Empty<string>(), false, false);

        Assert.Equal(
            CustomerCapabilityCatalog.AllKeys.OrderBy(k => k, StringComparer.Ordinal),
            caps.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(caps.Values, value => Assert.False(value));
    }

    // ══ 2. Effective entitlements → customer capabilities ════════

    [Fact]
    public async Task NoSubscription_ExposesNoCapabilities()
    {
        var business = await CreateBusinessAsync("Bare Biz");

        var caps = await CapabilitiesAsync(business.Id);

        Assert.All(caps.Values, value => Assert.False(value));
    }

    [Fact]
    public async Task EnterpriseBusiness_WithPrograms_ExposesEveryCapability()
    {
        var business = await CreateBusinessAsync("Ent Biz");
        await SubscribeAsync(business.Id, "enterprise");

        var caps = await CapabilitiesAsync(business.Id, loyaltyProgram: true, referralProgram: true);

        Assert.All(caps, entry => Assert.True(entry.Value, $"'{entry.Key}' should be exposed"));
    }

    [Fact]
    public async Task BusinessWithoutAppointments_ExposesNoAppointmentsOrReviews()
    {
        var business = await CreateBusinessAsync("No Appointments Biz");
        await SubscribeAsync(business.Id, "starter");

        // Grant loyalty explicitly so this case isolates "no appointments".
        await EnableModuleAsync(business.Id, "loyalty");
        await AddActiveLoyaltyProgramAsync(business);

        var caps = await CapabilitiesAsync(business.Id, loyaltyProgram: true);

        Assert.False(caps[CustomerCapabilityCatalog.Appointments]);
        Assert.False(caps[CustomerCapabilityCatalog.Reviews]);
        Assert.True(caps[CustomerCapabilityCatalog.Loyalty]);
    }

    [Fact]
    public async Task BusinessWithoutServices_ExposesNoServicesCapability()
    {
        var business = await CreateBusinessAsync("No Services Biz");
        await SubscribeAsync(business.Id, "starter");

        var caps = await CapabilitiesAsync(business.Id);

        Assert.False(caps[CustomerCapabilityCatalog.Services]);
    }

    [Fact]
    public async Task BusinessWithServicesModule_ExposesServices()
    {
        var business = await CreateBusinessAsync("Services Biz");
        await SubscribeAsync(business.Id, "starter");
        await EnableModuleAsync(business.Id, "serviceCatalog");

        var caps = await CapabilitiesAsync(business.Id);

        Assert.True(caps[CustomerCapabilityCatalog.Services]);
    }

    [Fact]
    public async Task LoyaltyModuleWithoutAnActiveProgram_ExposesNoLoyaltyOrRewards()
    {
        var business = await CreateBusinessAsync("Empty Loyalty Biz");
        await SubscribeAsync(business.Id, "starter");
        await EnableModuleAsync(business.Id, "loyalty");

        var caps = await CapabilitiesAsync(business.Id, loyaltyProgram: false);

        Assert.False(caps[CustomerCapabilityCatalog.Loyalty]);
        Assert.False(caps[CustomerCapabilityCatalog.Rewards]);
    }

    [Fact]
    public async Task ReferralModuleWithoutALiveProgram_ExposesNoReferralsCapability()
    {
        var business = await CreateBusinessAsync("Referral Biz");
        await SubscribeAsync(business.Id, "starter");
        await EnableModuleAsync(business.Id, "referral");

        var withProgram = await CapabilitiesAsync(business.Id, referralProgram: true);
        var withoutProgram = await CapabilitiesAsync(business.Id, referralProgram: false);

        Assert.True(withProgram[CustomerCapabilityCatalog.Referrals]);
        Assert.False(withoutProgram[CustomerCapabilityCatalog.Referrals]);
    }

    [Fact]
    public async Task InternalOnlyModules_NeverSurfaceAsCapabilities()
    {
        var business = await CreateBusinessAsync("Ops Biz");
        await SubscribeAsync(business.Id, "starter");

        // Attendance + analytics are enabled for the business…
        await EnableModuleAsync(business.Id, "attendance");
        await EnableModuleAsync(business.Id, "analytics");

        var caps = await CapabilitiesAsync(business.Id);

        // …and the capability payload has no key representing them.
        Assert.DoesNotContain("attendance", caps.Keys);
        Assert.DoesNotContain("analytics", caps.Keys);
        Assert.DoesNotContain("staff", caps.Keys);
        Assert.Equal(CustomerCapabilityCatalog.AllKeys.Count, caps.Count);
    }

    [Fact]
    public async Task DependencyClosure_ExposesCapabilitiesOfASoleGrantedModule()
    {
        var business = await CreateBusinessAsync("Closure Biz");
        await SubscribeAsync(business.Id, "starter");

        // Payments depends on appointments; granting payments must expose
        // payments AND (through the closure) the appointments capability.
        await EnableModuleAsync(business.Id, "payments");

        var caps = await CapabilitiesAsync(business.Id);

        Assert.True(caps[CustomerCapabilityCatalog.Payments]);
        Assert.True(caps[CustomerCapabilityCatalog.Appointments]);
        Assert.True(caps[CustomerCapabilityCatalog.Reviews]);
    }

    // ══ 3. Capabilities are scoped PER BUSINESS ══════════════════

    [Fact]
    public async Task TwoBusinesses_ExposeDifferentCapabilities()
    {
        var bizA = await CreateBusinessAsync("Biz A");
        var bizB = await CreateBusinessAsync("Biz B");

        await SubscribeAsync(bizA.Id, "starter");
        await EnableModuleAsync(bizA.Id, "appointments");   // A: appointments, no payments

        await SubscribeAsync(bizB.Id, "starter");
        await EnableModuleAsync(bizB.Id, "payments");       // B: payments ⇒ appointments, no services

        var capsA = await CapabilitiesAsync(bizA.Id);
        var capsB = await CapabilitiesAsync(bizB.Id);

        Assert.True(capsA[CustomerCapabilityCatalog.Appointments]);
        Assert.False(capsA[CustomerCapabilityCatalog.Payments]);
        Assert.False(capsA[CustomerCapabilityCatalog.Services]);

        Assert.True(capsB[CustomerCapabilityCatalog.Payments]);
        Assert.False(capsB[CustomerCapabilityCatalog.Services]);
        Assert.False(capsB[CustomerCapabilityCatalog.Loyalty]);
    }

    // ══ 4. Tenant-scoped authorization for CUSTOMER callers ══════

    [Fact]
    public async Task Customer_WithTenant_IsDeniedAModuleTheBusinessLacks()
    {
        var business = await CreateBusinessAsync("No Appointments Biz");
        await SubscribeAsync(business.Id, "starter");

        var ctx = CreateContext("Customer", ownedBusinessId: null, tenant: new StubTenantContext(business.Id));

        Assert.False(await ctx.HasModuleAsync("appointments"));
        Assert.False(await ctx.HasModuleAsync("loyalty"));
    }

    [Fact]
    public async Task Customer_WithTenant_IsAlwaysDeniedInternalModules()
    {
        var business = await CreateBusinessAsync("Ops Biz");
        await SubscribeAsync(business.Id, "enterprise");

        var ctx = CreateContext("Customer", ownedBusinessId: null, tenant: new StubTenantContext(business.Id));

        foreach (var key in new[] { "attendance", "analytics", "staff", "settings", "customers", "programs", "customCardDesign" })
            Assert.False(await ctx.HasModuleAsync(key), $"customer must never reach internal module '{key}'");
    }

    [Fact]
    public async Task Customer_WithTenant_IsAllowedACustomerModuleTheBusinessHas()
    {
        var business = await CreateBusinessAsync("Appointments Biz");
        await SubscribeAsync(business.Id, "starter");
        await EnableModuleAsync(business.Id, "appointments");

        var ctx = CreateContext("Customer", ownedBusinessId: null, tenant: new StubTenantContext(business.Id));

        Assert.True(await ctx.HasModuleAsync("appointments"));
        // appointments' closure includes customers/staff, which stay internal.
        Assert.False(await ctx.HasModuleAsync("customers"));
        Assert.False(await ctx.HasModuleAsync("staff"));
    }

    [Fact]
    public async Task Customer_TenantScoping_IsPerBusiness()
    {
        var withAppointments = await CreateBusinessAsync("Has Appointments");
        var withoutAppointments = await CreateBusinessAsync("No Appointments");
        await SubscribeAsync(withAppointments.Id, "starter");
        await SubscribeAsync(withoutAppointments.Id, "starter");
        await EnableModuleAsync(withAppointments.Id, "appointments");

        var allowed = CreateContext("Customer", null, new StubTenantContext(withAppointments.Id));
        var denied = CreateContext("Customer", null, new StubTenantContext(withoutAppointments.Id));

        Assert.True(await allowed.HasModuleAsync("appointments"));
        Assert.False(await denied.HasModuleAsync("appointments"));
    }

    [Fact]
    public async Task Customer_WithoutTenant_KeepsTheLegacyReadSideAnswer()
    {
        // Platform root: no server-side business signal exists, so the legacy
        // read-side answer stands. The customer app resolves the business
        // client-side and hides unavailable surfaces; the API stays
        // authoritative whenever a tenant IS supplied.
        var ctx = CreateContext("Customer", ownedBusinessId: null);

        Assert.True(await ctx.HasModuleAsync("appointments"));
        Assert.True(await ctx.HasModuleAsync("loyalty"));
        Assert.False(await ctx.HasModuleAsync("attendance"));
        Assert.False(await ctx.HasModuleAsync("analytics"));
    }

    [Fact]
    public async Task Admin_StillBypassesEveryModuleCheck()
    {
        var ctx = CreateContext("Admin", ownedBusinessId: null);

        foreach (var module in ModuleCatalog.Modules)
            Assert.True(await ctx.HasModuleAsync(module.Key));
    }
}
