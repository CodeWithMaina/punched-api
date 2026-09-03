using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.SeedData;

namespace PunchedApi.Tests;

/// <summary>
/// Unit tests for AdminTierService: CRUD, lifecycle state machine, publish
/// preconditions, module-set editing rules, and IsActive mirroring.
/// Uses the real seed catalog against an EF InMemory database.
/// </summary>
public class AdminTierServiceTests : IDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly AdminTierService _service;
    private readonly Guid _actorId = Guid.NewGuid();

    public AdminTierServiceTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new ApplicationDbContext(options);

        _db.Modules.AddRange(ModuleSeedData.GetModules());
        _db.SubscriptionPlans.AddRange(SubscriptionPlanSeedData.GetPlans());
        _db.SaveChanges();

        var modules = _db.Modules.ToDictionary(m => m.Key);
        var plans = _db.SubscriptionPlans.ToDictionary(p => p.Key);
        foreach (var (planKey, moduleKey) in PlanModuleSeedData.GetPlanModules())
            _db.PlanModules.Add(new PlanModule { PlanId = plans[planKey].Id, ModuleId = modules[moduleKey].Id });
        _db.SaveChanges();

        var entitlements = new ModuleEntitlementService(_db, TestHelpers.CreateLogger<ModuleEntitlementService>());
        var audit = new SubscriptionAuditService(_db, TestHelpers.CreateLogger<SubscriptionAuditService>());
        _service = new AdminTierService(_db, entitlements, audit, TestHelpers.CreateLogger<AdminTierService>());
    }

    private AdminTierCreateRequest CreateRequest(string key = "testtier", List<string>? moduleKeys = null) =>
        new()
        {
            Name = "Test Tier",
            Key = key,
            Description = "A test tier",
            Price = 999m,
            BillingInterval = "monthly",
            ModuleKeys = moduleKeys ?? new List<string> { "customers", "staff" }
        };

    // ── Create ──────────────────────────────────────────────

    [Fact]
    public async Task Create_StartsAsDraft_AndMirrorsIsActive()
    {
        var result = await _service.CreateAsync(CreateRequest(), _actorId);

        Assert.True(result.Success);
        Assert.Equal("Draft", result.Data!.Lifecycle);
        var entity = await _db.SubscriptionPlans.SingleAsync(p => p.Key == "testtier");
        Assert.Equal(SubscriptionPlanLifecycleState.Draft, entity.LifecycleState);
        Assert.False(entity.IsActive);
    }

    [Fact]
    public async Task Create_DuplicateKey_IsRejected()
    {
        var result = await _service.CreateAsync(CreateRequest("starter"), _actorId);
        Assert.False(result.Success);
        Assert.Equal("DUPLICATE_TIER_KEY", result.Error?.Code);
    }

    // ── Publish ─────────────────────────────────────────────

    [Fact]
    public async Task Publish_WithCoreModule_Succeeds_AndSetsIsActive()
    {
        var created = (await _service.CreateAsync(CreateRequest(moduleKeys: new List<string> { "customers", "staff", "appointments" }), _actorId)).Data!;
        var result = await _service.PublishAsync(created.Id, _actorId);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("Active", result.Data!.Lifecycle);
        var entity = await _db.SubscriptionPlans.SingleAsync(p => p.Id == created.Id);
        Assert.True(entity.IsActive);
        Assert.Equal(SubscriptionPlanLifecycleState.Active, entity.LifecycleState);
        Assert.NotNull(entity.PublishedAt);
        Assert.Equal(_actorId, entity.LastPublishedByUserId);
    }

    [Fact]
    public async Task Publish_WithMissingDependency_IsRejected()
    {
        var dependentKey = "programs"; // depends on loyalty (Premium, not included)
        var result = await _service.CreateAsync(CreateRequest(moduleKeys: new List<string> { dependentKey }), _actorId);
        Assert.False(result.Success);
        Assert.Equal("DEPENDENCY_MISSING", result.Error?.Code);
    }

    // ── Lifecycle state machine ─────────────────────────────

    [Fact]
    public async Task Lifecycle_ArchivedIsTerminal()
    {
        var created = (await _service.CreateAsync(CreateRequest(), _actorId)).Data!;
        var deactivated = await _service.DeactivateAsync(created.Id, _actorId);
        Assert.Equal("Inactive", deactivated.Data!.Lifecycle);
        var archived = await _service.ArchiveAsync(created.Id, _actorId);
        Assert.Equal("Archived", archived.Data!.Lifecycle);

        Assert.False((await _service.PublishAsync(created.Id, _actorId)).Success);
        Assert.False((await _service.DeactivateAsync(created.Id, _actorId)).Success);
        Assert.False((await _service.UpdateAsync(created.Id, new AdminTierUpdateRequest
        { Name = "x", Price = 1, BillingInterval = "monthly", DisplayOrder = 1, IsDefault = false }, _actorId)).Success);
        var entity = await _db.SubscriptionPlans.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(SubscriptionPlanLifecycleState.Archived, entity.LifecycleState);
    }

    [Fact]
    public async Task Deactivate_ActiveTier_SetsInactive_AndCanRepublish()
    {
        var created = (await _service.CreateAsync(CreateRequest(), _actorId)).Data!;
        await _service.PublishAsync(created.Id, _actorId);
        var result = await _service.DeactivateAsync(created.Id, _actorId);
        Assert.Equal("Inactive", result.Data!.Lifecycle);
        var entity = await _db.SubscriptionPlans.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.False(entity.IsActive);
        var republished = await _service.PublishAsync(created.Id, _actorId);
        Assert.Equal("Active", republished.Data!.Lifecycle);
    }

    // ── Module editing ──────────────────────────────────────

    [Fact]
    public async Task SetModules_ActiveTier_IsRejected()
    {
        var created = (await _service.CreateAsync(CreateRequest(), _actorId)).Data!;
        await _service.PublishAsync(created.Id, _actorId);
        var result = await _service.SetModulesAsync(created.Id,
            new AdminTierModulesRequest { ModuleKeys = new List<string> { "customers" } }, _actorId);
        Assert.False(result.Success);
        Assert.Equal("ACTIVE_TIER_HAS_MODULE_EDIT_DISABLED", result.Error?.Code);
    }

    [Fact]
    public async Task SetModules_DraftTier_AddsAndRemoves()
    {
        var created = (await _service.CreateAsync(CreateRequest(), _actorId)).Data!;
        var result = await _service.SetModulesAsync(created.Id,
            new AdminTierModulesRequest { ModuleKeys = new List<string> { "customers", "stamps" } }, _actorId);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(new[] { "customers", "stamps" }.OrderBy(x => x), result.Data!.ModuleKeys.OrderBy(x => x));

        var after = await _service.SetModulesAsync(created.Id,
            new AdminTierModulesRequest { ModuleKeys = new List<string> { "stamps" } }, _actorId);
        Assert.Equal(new[] { "stamps" }, after.Data!.ModuleKeys.OrderBy(x => x));
    }

    [Fact]
    public async Task SetModules_InvalidModule_IsRejected()
    {
        var created = (await _service.CreateAsync(CreateRequest(), _actorId)).Data!;
        var result = await _service.SetModulesAsync(created.Id,
            new AdminTierModulesRequest { ModuleKeys = new List<string> { "no-such-module" } }, _actorId);
        Assert.False(result.Success);
        Assert.Equal("INVALID_MODULE", result.Error?.Code);
    }

    // ── Archive with subscribers ────────────────────────────

    [Fact]
    public async Task Archive_TierWithActiveSubscribers_IsRejected()
    {
        var business = new Business { Id = Guid.NewGuid(), Name = "Biz", Category = "salon", Location = "NBO", MpesaNumber = "1" };
        _db.Businesses.Add(business);
        _db.SaveChanges();

        var created = (await _service.CreateAsync(CreateRequest(), _actorId)).Data!;
        await _service.PublishAsync(created.Id, _actorId);
        _db.BusinessSubscriptions.Add(new BusinessSubscription
        { BusinessId = business.Id, PlanId = created.Id, Status = "active", StartsAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        // Active → Archive is not a valid transition; deactivate first, then the
        // active subscribers must block the archive.
        await _service.DeactivateAsync(created.Id, _actorId);
        var result = await _service.ArchiveAsync(created.Id, _actorId);
        Assert.False(result.Success);
        Assert.Equal("TIER_HAS_ACTIVE_SUBSCRIBERS", result.Error?.Code);
    }

    // ── Seeder safety ───────────────────────────────────────

    [Fact]
    public async Task Seeder_DoesNotOverwrite_AdminLifecycleChanges()
    {
        var created = (await _service.CreateAsync(CreateRequest(), _actorId)).Data!;
        Assert.Equal("Draft", created.Lifecycle);

        var seeder = new PunchedApi.Infrastructure.Data.Seeding.ModuleCatalogSeeder(
            _db, TestHelpers.CreateLogger<PunchedApi.Infrastructure.Data.Seeding.ModuleCatalogSeeder>());
        await seeder.EnsureModuleCatalogAsync();

        var after = await _db.SubscriptionPlans.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(SubscriptionPlanLifecycleState.Draft, after.LifecycleState);
    }




    public void Dispose() => _db.Dispose();
}
