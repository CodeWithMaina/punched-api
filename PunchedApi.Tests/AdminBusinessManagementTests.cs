using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using PunchedApi.API.Controllers;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.SeedData;

namespace PunchedApi.Tests;

/// <summary>
/// Platform-administration business management.
///
/// Covers the two invariants the console depends on:
/// 1. a business's platform status is a real access gate — a suspended or
///    deactivated business holds NO module access regardless of its subscription;
/// 2. every administrative mutation persists its state and records an audit
///    entry, so the audit feed is a truthful record rather than a decoration.
///
/// Uses the real seed catalog against an EF InMemory database.
/// </summary>
public class AdminBusinessManagementTests : IDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly ModuleEntitlementService _entitlements;
    private readonly SubscriptionLifecycleService _lifecycle;
    private readonly AdminBusinessService _businesses;
    private readonly AdminTierService _tiers;
    private readonly Guid _actorId = Guid.NewGuid();
    private readonly Guid _businessId = Guid.NewGuid();

    public AdminBusinessManagementTests()
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

        _entitlements = new ModuleEntitlementService(_db, TestHelpers.CreateLogger<ModuleEntitlementService>());
        var audit = new SubscriptionAuditService(_db, TestHelpers.CreateLogger<SubscriptionAuditService>());
        _lifecycle = new SubscriptionLifecycleService(_db, _entitlements, TestHelpers.CreateLogger<SubscriptionLifecycleService>());
        _tiers = new AdminTierService(_db, _entitlements, audit, TestHelpers.CreateLogger<AdminTierService>());
        _businesses = new AdminBusinessService(
            _db,
            _entitlements,
            audit,
            new TenantUrlBuilder(Options.Create(new PublicAppSettings())),
            TestHelpers.CreateLogger<AdminBusinessService>());

        _db.Businesses.Add(new Business
        {
            Id = _businessId,
            Name = "Admin Test Salon",
            Category = "salon",
            Location = "Nairobi",
            MpesaNumber = "123456",
            Slug = "admin-test-salon",
            Status = BusinessStatus.Active,
        });
        _db.SaveChanges();
    }

    /// <summary>Attaches an active subscription on the given plan.</summary>
    private BusinessSubscription AddSubscription(string planKey, string status = "active", DateTime? endsAt = null)
    {
        var plan = _db.SubscriptionPlans.Single(p => p.Key == planKey);
        var sub = new BusinessSubscription
        {
            BusinessId = _businessId,
            PlanId = plan.Id,
            Status = status,
            StartsAt = DateTime.UtcNow.AddDays(-10),
            EndsAt = endsAt,
        };
        _db.BusinessSubscriptions.Add(sub);
        _db.SaveChanges();
        return sub;
    }

    private async Task<HashSet<string>> EffectiveKeysAsync()
    {
        _entitlements.Invalidate(_businessId);
        return await _entitlements.GetEffectiveModuleKeysAsync(_businessId);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Platform status gates module access ─────────────────

    [Fact]
    public async Task SuspendingABusiness_WithholdsEveryModule_AndReactivatingRestoresThem()
    {
        AddSubscription("growth");
        var before = await EffectiveKeysAsync();
        Assert.NotEmpty(before);

        var suspend = await _businesses.SetStatusAsync(
            _businessId, BusinessStatus.Suspended, "Chargeback investigation", _actorId);
        Assert.True(suspend.Success);
        Assert.Equal(BusinessStatus.Suspended, suspend.Data!.Status);
        Assert.NotNull(suspend.Data.SuspendedAt);
        Assert.Equal("Chargeback investigation", suspend.Data.SuspendedReason);

        // The subscription is untouched, but access is withheld.
        Assert.Empty(await EffectiveKeysAsync());

        var reactivate = await _businesses.SetStatusAsync(
            _businessId, BusinessStatus.Active, "Cleared", _actorId);
        Assert.True(reactivate.Success);
        Assert.Null(reactivate.Data!.SuspendedAt);
        Assert.Null(reactivate.Data.SuspendedReason);

        Assert.Equal(before, await EffectiveKeysAsync());
    }

    [Fact]
    public async Task DeactivatingABusiness_KeepsTheRowAndWithholdsAccess()
    {
        AddSubscription("growth");

        var result = await _businesses.SetStatusAsync(
            _businessId, BusinessStatus.Deactivated, "Closed by request", _actorId);

        Assert.True(result.Success);
        Assert.Equal(BusinessStatus.Deactivated, result.Data!.Status);
        Assert.NotNull(result.Data.DeactivatedAt);

        // The business and its subscription are retained — nothing is deleted.
        var row = await _db.Businesses.SingleAsync(b => b.Id == _businessId);
        Assert.False(row.IsDeleted);
        Assert.Single(
            await _db.BusinessSubscriptions.Where(s => s.BusinessId == _businessId).ToListAsync());
        Assert.Empty(await EffectiveKeysAsync());
    }

    [Fact]
    public async Task SetStatusAsync_RejectsAnUnknownStatus()
    {
        var result = await _businesses.SetStatusAsync(_businessId, "quarantined", null, _actorId);

        Assert.False(result.Success);
        Assert.Equal("INVALID_BUSINESS_STATUS", result.Error!.Code);
        Assert.Equal(
            BusinessStatus.Active,
            (await _db.Businesses.SingleAsync(b => b.Id == _businessId)).Status);
    }

    [Fact]
    public async Task SetStatusAsync_RecordsAnAuditEntryWithTheReason()
    {
        await _businesses.SetStatusAsync(
            _businessId, BusinessStatus.Suspended, "Fraud review", _actorId);

        var entry = await _db.SubscriptionAuditLogs.SingleAsync(
            a => a.Action == "BUSINESS_SUSPENDED" && a.TargetBusinessId == _businessId);
        Assert.Equal(_actorId, entry.ActorUserId);
        Assert.Equal("Fraud review", entry.Reason);
    }

    // ── Subscription lifecycle ──────────────────────────────

    [Fact]
    public async Task PausingASubscription_WithholdsAccess_AndResumingRestoresIt()
    {
        AddSubscription("growth");
        Assert.NotEmpty(await EffectiveKeysAsync());

        var paused = await _lifecycle.PauseAsync(_businessId, "Non-payment hold", _actorId);
        Assert.Equal(1, paused);

        var row = await _db.BusinessSubscriptions.SingleAsync(s => s.BusinessId == _businessId);
        Assert.Equal(SubscriptionStatus.Paused, row.Status);
        Assert.NotNull(row.PausedAt);
        Assert.Equal("Non-payment hold", row.PauseReason);
        Assert.Empty(await EffectiveKeysAsync());

        var resumed = await _lifecycle.ResumeAsync(_businessId, _actorId);
        Assert.Equal(1, resumed);

        row = await _db.BusinessSubscriptions.SingleAsync(s => s.BusinessId == _businessId);
        Assert.Equal(SubscriptionStatus.Active, row.Status);
        Assert.Null(row.PausedAt);
        Assert.Null(row.PauseReason);
        Assert.NotEmpty(await EffectiveKeysAsync());
    }

    [Fact]
    public async Task ResumingAfterThePeriodElapsed_GrantsAFreshInterval()
    {
        // Paused with a period that already lapsed while on hold.
        AddSubscription("starter", SubscriptionStatus.Paused, DateTime.UtcNow.AddDays(-5));

        await _lifecycle.ResumeAsync(_businessId, _actorId);

        var row = await _db.BusinessSubscriptions.SingleAsync(s => s.BusinessId == _businessId);
        Assert.Equal(SubscriptionStatus.Active, row.Status);
        Assert.NotNull(row.EndsAt);
        Assert.True(row.EndsAt > DateTime.UtcNow, "A resumed subscription must not be already expired.");
        Assert.NotEmpty(await EffectiveKeysAsync());
    }

    [Fact]
    public async Task ExtendingALapsedSubscription_ReactivatesIt_AndNeverShortensThePeriod()
    {
        AddSubscription("growth", SubscriptionStatus.Expired, DateTime.UtcNow.AddDays(-30));
        Assert.Empty(await EffectiveKeysAsync());

        var target = DateTime.UtcNow.AddDays(30);
        var extended = await _lifecycle.ExtendAsync(_businessId, target, _actorId, "Goodwill extension");
        Assert.Equal(1, extended);

        var row = await _db.BusinessSubscriptions.SingleAsync(s => s.BusinessId == _businessId);
        Assert.Equal(SubscriptionStatus.Active, row.Status);
        Assert.NotNull(row.EndsAt);
        Assert.True(row.EndsAt >= target.AddSeconds(-1));
        Assert.NotEmpty(await EffectiveKeysAsync());

        // Extending with an earlier date must not shorten the period.
        await _lifecycle.ExtendAsync(_businessId, DateTime.UtcNow.AddDays(1), _actorId, null);
        row = await _db.BusinessSubscriptions.SingleAsync(s => s.BusinessId == _businessId);
        Assert.True(
            row.EndsAt >= target.AddSeconds(-1),
            "Extend must never move the end date backwards.");
    }

    [Fact]
    public async Task DeactivatingASubscription_WithholdsAccess_AndRenewingRestoresIt()
    {
        AddSubscription("growth");

        var deactivated = await _lifecycle.DeactivateAsync(
            _businessId, "Administrative closure", _actorId);
        Assert.Equal(1, deactivated);

        var row = await _db.BusinessSubscriptions.SingleAsync(s => s.BusinessId == _businessId);
        Assert.Equal(SubscriptionStatus.Deactivated, row.Status);
        Assert.Empty(await EffectiveKeysAsync());

        // Reactivate is the documented way back from every withheld state.
        Assert.Equal(1, await _lifecycle.ReactivateAsync(_businessId, _actorId));
        row = await _db.BusinessSubscriptions.SingleAsync(s => s.BusinessId == _businessId);
        Assert.Equal(SubscriptionStatus.Active, row.Status);
        Assert.NotEmpty(await EffectiveKeysAsync());
    }

    [Fact]
    public async Task ReactivateAsync_BringsBackEveryWithheldState()
    {
        foreach (var withheld in new[]
                 {
                     SubscriptionStatus.Expired,
                     SubscriptionStatus.PastDue,
                     SubscriptionStatus.Canceled,
                     SubscriptionStatus.Paused,
                     SubscriptionStatus.Deactivated,
                 })
        {
            var sub = AddSubscription("growth", withheld, DateTime.UtcNow.AddDays(-1));
            Assert.Empty(await EffectiveKeysAsync());

            Assert.Equal(1, await _lifecycle.ReactivateAsync(_businessId, _actorId));

            var row = await _db.BusinessSubscriptions.SingleAsync(s => s.Id == sub.Id);
            Assert.Equal(SubscriptionStatus.Active, row.Status);
            Assert.Null(row.CanceledAt);
            Assert.Null(row.PausedAt);
            Assert.NotNull(row.EndsAt);
            Assert.True(row.EndsAt > DateTime.UtcNow);
            Assert.NotEmpty(await EffectiveKeysAsync());

            _db.BusinessSubscriptions.Remove(row);
            _db.SaveChanges();
        }
    }

    [Fact]
    public async Task LifecycleActions_WithNoSubscription_ChangeNothing()
    {
        Assert.Equal(0, await _lifecycle.PauseAsync(_businessId, "nothing to pause", _actorId));
        Assert.Equal(0, await _lifecycle.ResumeAsync(_businessId, _actorId));
        Assert.Equal(0, await _lifecycle.DeactivateAsync(_businessId, "nothing to deactivate", _actorId));
    }

    // ── Business list ───────────────────────────────────────

    [Fact]
    public async Task ListAsync_FiltersByPlatformStatus()
    {
        _db.Businesses.Add(new Business
        {
            Id = Guid.NewGuid(),
            Name = "Suspended Salon",
            Category = "salon",
            Location = "Mombasa",
            MpesaNumber = "654321",
            Slug = "suspended-salon",
            Status = BusinessStatus.Suspended,
        });
        _db.SaveChanges();

        var suspended = await _businesses.ListAsync(
            new AdminBusinessListQuery { Status = BusinessStatus.Suspended }, page: 1, pageSize: 20);
        Assert.True(suspended.Success);
        Assert.Single(suspended.Data!.Items);
        Assert.Equal("Suspended Salon", suspended.Data.Items[0].Name);
        Assert.Equal(BusinessStatus.Suspended, suspended.Data.Items[0].Status);

        var active = await _businesses.ListAsync(
            new AdminBusinessListQuery { Status = BusinessStatus.Active }, page: 1, pageSize: 20);
        Assert.Single(active.Data!.Items);
        Assert.Equal("Admin Test Salon", active.Data.Items[0].Name);
    }

    [Fact]
    public async Task ListAsync_ReportsTheEffectiveModulesOfEachBusiness()
    {
        AddSubscription("growth");

        var page = await _businesses.ListAsync(new AdminBusinessListQuery(), page: 1, pageSize: 20);

        Assert.True(page.Success);
        var row = Assert.Single(page.Data!.Items);
        Assert.NotEmpty(row.EnabledModules);
        Assert.Equal("growth", row.PlanKey);
        Assert.Equal(SubscriptionStatus.Active, row.SubscriptionStatus);
    }

    // ── Plan duplication ────────────────────────────────────

    [Fact]
    public async Task DuplicateTier_ClonesModulesIntoANewDraft_AndLeavesTheSourceAlone()
    {
        var source = _db.SubscriptionPlans.Single(p => p.Key == "growth");
        var sourceModules = await _db.PlanModules
            .Where(pm => pm.PlanId == source.Id)
            .Select(pm => pm.ModuleId)
            .ToListAsync();

        var result = await _tiers.DuplicateAsync(
            source.Id, new AdminDuplicateTierRequest { Key = "growth-copy" }, _actorId);

        Assert.True(result.Success);
        var copy = result.Data!;
        Assert.Equal("growth-copy", copy.Key);
        Assert.Equal(SubscriptionPlanLifecycleState.Draft.ToString(), copy.Lifecycle);
        Assert.False(copy.IsDefault);
        Assert.Equal(source.Price, copy.Price);
        Assert.Equal(source.BillingInterval, copy.BillingInterval);

        // The module set is cloned, not shared.
        var copyModules = await _db.PlanModules
            .Where(pm => pm.PlanId == copy.Id)
            .Select(pm => pm.ModuleId)
            .ToListAsync();
        Assert.Equal(sourceModules.OrderBy(id => id), copyModules.OrderBy(id => id));

        // The source tier is untouched and still assignable.
        var unchanged = await _db.SubscriptionPlans.SingleAsync(p => p.Id == source.Id);
        Assert.Equal(SubscriptionPlanLifecycleState.Active, unchanged.LifecycleState);
    }

    [Fact]
    public async Task DuplicateTier_RejectsAKeyThatAlreadyExists()
    {
        var source = _db.SubscriptionPlans.Single(p => p.Key == "growth");

        var result = await _tiers.DuplicateAsync(
            source.Id, new AdminDuplicateTierRequest { Key = "starter" }, _actorId);

        Assert.False(result.Success);
        Assert.Equal("DUPLICATE_TIER_KEY", result.Error!.Code);
    }

    [Fact]
    public async Task DuplicateTier_RecordsAnAuditEntry()
    {
        var source = _db.SubscriptionPlans.Single(p => p.Key == "starter");

        var result = await _tiers.DuplicateAsync(
            source.Id, new AdminDuplicateTierRequest { Key = "starter-copy" }, _actorId);

        Assert.True(result.Success);
        Assert.True(await _db.SubscriptionAuditLogs.AnyAsync(
            a => a.Action == "TIER_DUPLICATED" && a.TargetPlanId == result.Data!.Id));
    }

    // ── Module availability takes effect immediately ────────

    /// <summary>
    /// Switching a module off in the admin console must reach every business at
    /// once — not after the 60-second entitlement TTL.
    ///
    /// <para>The read below deliberately does NOT call <c>Invalidate</c> first:
    /// the admin action has to drop every cached resolution itself. Before that
    /// was wired up, a disabled module kept being served to live storefronts for
    /// the rest of the cache window.</para>
    /// </summary>
    [Fact]
    public async Task DisablingAModule_TakesEffectImmediately_WithoutWaitingForTheCacheTtl()
    {
        AddSubscription("growth");

        // Warm the per-business cache and pick a module the plan actually grants,
        // so the assertion cannot depend on which modules the seed bundles.
        var warm = await _entitlements.GetEffectiveModuleKeysAsync(_businessId);
        Assert.NotEmpty(warm);
        var target = warm.OrderBy(key => key, StringComparer.Ordinal).First();

        var result = await _tiers.UpdateModuleAsync(
            target,
            new AdminModuleUpdateRequest { IsActive = false, Reason = "Platform retired it" },
            _actorId);

        Assert.True(result.Success);
        Assert.False(result.Data!.IsActive);

        // Straight from the service, with no manual invalidation in between.
        var after = await _entitlements.GetEffectiveModuleKeysAsync(_businessId);
        Assert.DoesNotContain(target, after);
    }

    [Fact]
    public async Task ReEnablingAModule_AlsoTakesEffectImmediately()
    {
        AddSubscription("growth");

        var warm = await _entitlements.GetEffectiveModuleKeysAsync(_businessId);
        var target = warm.OrderBy(key => key, StringComparer.Ordinal).First();

        await _tiers.UpdateModuleAsync(
            target, new AdminModuleUpdateRequest { IsActive = false }, _actorId);
        Assert.DoesNotContain(target, await _entitlements.GetEffectiveModuleKeysAsync(_businessId));

        await _tiers.UpdateModuleAsync(
            target, new AdminModuleUpdateRequest { IsActive = true }, _actorId);
        Assert.Contains(target, await _entitlements.GetEffectiveModuleKeysAsync(_businessId));
    }

    [Fact]
    public async Task UpdatingAModule_RecordsAnAuditEntry()
    {
        AddSubscription("growth");

        var warm = await _entitlements.GetEffectiveModuleKeysAsync(_businessId);
        var target = warm.OrderBy(key => key, StringComparer.Ordinal).First();

        await _tiers.UpdateModuleAsync(
            target,
            new AdminModuleUpdateRequest { IsActive = false, Reason = "Retired platform-wide" },
            _actorId);

        var entry = await _db.SubscriptionAuditLogs.SingleAsync(a => a.Action == "MODULE_UPDATED");
        Assert.Equal(_actorId, entry.ActorUserId);
        Assert.Equal("Retired platform-wide", entry.Reason);
    }

    /// <summary>
    /// The exact call the admin console makes when the availability switch is
    /// flipped. It has to do BOTH halves or a live storefront keeps rendering the
    /// module: drop every cached entitlement (60 s TTL) AND evict the output-cached
    /// storefront payload (30 s).
    /// </summary>
    [Fact]
    public async Task TogglingAModuleThroughTheAdminEndpoint_InvalidatesEntitlementsAndEvictsTheStorefrontCache()
    {
        AddSubscription("growth");
        var warm = await _entitlements.GetEffectiveModuleKeysAsync(_businessId);
        var target = warm.OrderBy(key => key, StringComparer.Ordinal).First();

        var invalidator = new StubStorefrontCacheInvalidator();
        var controller = new AdminSubscriptionTiersController(
            _tiers,
            new AdminSubscriptionService(
                _db, _entitlements, TestHelpers.CreateLogger<AdminSubscriptionService>()),
            invalidator);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var response = await controller.UpdateModule(
            target,
            new AdminModuleUpdateRequest { IsActive = false, Reason = "Retired platform-wide" });

        Assert.IsType<OkObjectResult>(response);
        Assert.Equal(1, invalidator.Evictions);
        Assert.DoesNotContain(target, await _entitlements.GetEffectiveModuleKeysAsync(_businessId));
    }
}
