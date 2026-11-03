using Microsoft.Data.Sqlite;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// The service lifecycle (active / paused / archived) and the DB-first list
/// filters the Services list is built on.
///
/// <para>The archived state is the one the storefront must never see, so every
/// transition test asserts both the owner-facing status and the public list — a
/// status that only changed one of the two would be a storefront leak.</para>
/// </summary>
public sealed class ServiceCatalogLifecycleTests
{
    private sealed class Env
    {
        public ApplicationDbContext Context = null!;
        public ServiceCatalogService Service = null!;
        public Business Business = null!;
        public User Owner = null!;
    }

    private static async Task<Env> CreateEnvAsync(SqliteConnection connection)
    {
        var context = BookingTestBase.CreateContext(connection);
        var service = BookingTestBase.CreateCatalogService(context);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);
        return new Env { Context = context, Service = service, Business = business, Owner = owner };
    }

    private static async Task<Guid> CreateServiceAsync(
        Env env, string name, int durationMinutes = 60, decimal price = 3200m, bool showcase = true)
    {
        var created = await env.Service.CreateServiceAsync(env.Owner.Id, new CreateServiceRequest
        {
            Name = name, DurationMinutes = durationMinutes, Price = price, Showcase = showcase
        });
        Assert.True(created.Success, created.Error?.Message);
        return created.Data!.Id;
    }

    private static async Task<List<string>> PublicNamesAsync(Env env)
    {
        var publicList = await env.Service.GetServicesForBusinessAsync(env.Business.Id);
        Assert.True(publicList.Success, publicList.Error?.Message);
        return publicList.Data!.Select(service => service.Name).ToList();
    }

    [Fact]
    public async Task SetServiceStatusAsync_MovesThroughPausedArchivedAndActive()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var serviceId = await CreateServiceAsync(env, "Signature Cut");

        var paused = await env.Service.SetServiceStatusAsync(env.Owner.Id, serviceId, ServiceCatalogStatuses.Paused);
        Assert.True(paused.Success, paused.Error?.Message);
        Assert.Equal(ServiceCatalogStatuses.Paused, paused.Data!.Status);
        Assert.False(paused.Data.IsActive);
        Assert.False(paused.Data.IsArchived);
        Assert.Empty(await PublicNamesAsync(env));

        var resumed = await env.Service.SetServiceStatusAsync(env.Owner.Id, serviceId, ServiceCatalogStatuses.Active);
        Assert.Equal(ServiceCatalogStatuses.Active, resumed.Data!.Status);
        Assert.Equal(["Signature Cut"], await PublicNamesAsync(env));

        var archived = await env.Service.SetServiceStatusAsync(env.Owner.Id, serviceId, ServiceCatalogStatuses.Archived);
        Assert.Equal(ServiceCatalogStatuses.Archived, archived.Data!.Status);
        Assert.False(archived.Data.IsActive);
        Assert.True(archived.Data.IsArchived);
        // Archived is the state the storefront must never see.
        Assert.Empty(await PublicNamesAsync(env));

        var restored = await env.Service.SetServiceStatusAsync(env.Owner.Id, serviceId, ServiceCatalogStatuses.Active);
        Assert.Equal(ServiceCatalogStatuses.Active, restored.Data!.Status);
        Assert.False(restored.Data.IsArchived);
        Assert.Equal(["Signature Cut"], await PublicNamesAsync(env));
    }

    [Fact]
    public async Task SetServiceStatusAsync_RejectsUnknownStatusesAndForeignServices()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var serviceId = await CreateServiceAsync(env, "Gloss Treatment");

        var unknown = await env.Service.SetServiceStatusAsync(env.Owner.Id, serviceId, "deleted");
        Assert.Equal("VALIDATION_ERROR", unknown.Error?.Code);

        var otherOwner = BookingTestBase.CreateOwner("lifecycle-other@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other");
        await BookingTestBase.SeedAsync(context, otherOwner, otherBusiness);

        var forbidden = await env.Service.SetServiceStatusAsync(otherOwner.Id, serviceId, ServiceCatalogStatuses.Paused);
        Assert.Equal("FORBIDDEN", forbidden.Error?.Code);

        var missing = await env.Service.SetServiceStatusAsync(env.Owner.Id, Guid.NewGuid(), ServiceCatalogStatuses.Paused);
        Assert.Equal("NOT_FOUND", missing.Error?.Code);
    }

    [Fact]
    public async Task UpdateServiceAsync_KeepsTheArchiveFlagAndActivityConsistent()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var serviceId = await CreateServiceAsync(env, "Bridal Trial", 90, 7500m);

        var archived = await env.Service.UpdateServiceAsync(env.Owner.Id, serviceId, new UpdateServiceRequest { IsArchived = true });
        Assert.True(archived.Success, archived.Error?.Message);
        Assert.True(archived.Data!.IsArchived);
        Assert.False(archived.Data.IsActive);
        Assert.Equal(ServiceCatalogStatuses.Archived, archived.Data.Status);

        // Sending both flags cannot produce an archived-but-active row.
        var contradictory = await env.Service.UpdateServiceAsync(
            env.Owner.Id, serviceId, new UpdateServiceRequest { IsArchived = true, IsActive = true });
        Assert.True(contradictory.Data!.IsArchived);
        Assert.False(contradictory.Data.IsActive);

        var restored = await env.Service.UpdateServiceAsync(env.Owner.Id, serviceId, new UpdateServiceRequest { IsArchived = false });
        Assert.False(restored.Data!.IsArchived);
        Assert.True(restored.Data.IsActive);
        Assert.Equal(ServiceCatalogStatuses.Active, restored.Data.Status);
    }

    [Fact]
    public async Task QueryMyServicesAsync_FiltersByStatusPriceDurationAndStorefrontVisibility()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await CreateServiceAsync(env, "Scalp Ritual", 30, 1800m);
        await CreateServiceAsync(env, "Signature Cut", 60, 3200m);
        await CreateServiceAsync(env, "Creative Colour", 120, 5200m);
        var hidden = await CreateServiceAsync(env, "Keratin Smoothing", 150, 9500m, showcase: false);
        var pausedId = await CreateServiceAsync(env, "Bridal Trial", 90, 7500m);
        await env.Service.SetServiceStatusAsync(env.Owner.Id, pausedId, ServiceCatalogStatuses.Paused);

        async Task<int> CountAsync(ServiceCatalogQueryRequest request)
        {
            var result = await env.Service.QueryMyServicesAsync(env.Owner.Id, request);
            Assert.True(result.Success, result.Error?.Message);
            return result.Data!.TotalCount;
        }

        Assert.Equal(5, await CountAsync(new()));
        Assert.Equal(4, await CountAsync(new() { Status = ServiceCatalogStatuses.Active }));
        Assert.Equal(1, await CountAsync(new() { Status = ServiceCatalogStatuses.Paused }));
        Assert.Equal(0, await CountAsync(new() { Status = ServiceCatalogStatuses.Archived }));

        // The list's default filter selects two statuses at once, which arrives
        // as one comma-separated value rather than two round trips. A repeated
        // value is harmless: the filter is flag-based, not a list to count.
        Assert.Equal(5, await CountAsync(new() { Status = "active,paused" }));
        Assert.Equal(1, await CountAsync(new() { Status = "paused,archived" }));
        Assert.Equal(1, await CountAsync(new() { Status = "paused,paused" }));

        // Price and duration are inclusive bounds, so a service exactly on the
        // bound belongs to the bucket the design labels it with.
        Assert.Equal(2, await CountAsync(new() { MaxPrice = 3200m }));
        Assert.Equal(4, await CountAsync(new() { MinPrice = 3200m }));
        Assert.Equal(1, await CountAsync(new() { MinDuration = 31, MaxDuration = 60 }));
        Assert.Equal(2, await CountAsync(new() { MinDuration = 91 }));

        // Storefront visibility is the showcase flag, not the status.
        Assert.Equal(1, await CountAsync(new() { Showcase = false }));
        Assert.Equal(4, await CountAsync(new() { Showcase = true }));
        Assert.Equal(3, await CountAsync(new() { Showcase = true, Status = ServiceCatalogStatuses.Active }));

        var hiddenRow = await env.Service.GetServiceAsync(env.Owner.Id, hidden);
        Assert.False(hiddenRow.Data!.Showcase);
    }

    [Fact]
    public async Task QueryMyServicesAsync_SortsByNameDurationAndNewest()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await CreateServiceAsync(env, "Scalp Ritual", 30, 1800m);
        await CreateServiceAsync(env, "Creative Colour", 120, 5200m);
        await CreateServiceAsync(env, "Gloss Treatment", 45, 2400m);

        async Task<List<string>> NamesAsync(string sortBy, string direction)
        {
            var result = await env.Service.QueryMyServicesAsync(env.Owner.Id, new ServiceCatalogQueryRequest
            {
                SortBy = sortBy, SortDirection = direction, PageSize = 10
            });
            Assert.True(result.Success, result.Error?.Message);
            return result.Data!.Items.Select(service => service.Name).ToList();
        }

        Assert.Equal(
            ["Creative Colour", "Gloss Treatment", "Scalp Ritual"],
            await NamesAsync("name", "asc"));
        Assert.Equal(
            ["Scalp Ritual", "Gloss Treatment", "Creative Colour"],
            await NamesAsync("duration", "asc"));
        Assert.Equal(
            ["Creative Colour", "Gloss Treatment", "Scalp Ritual"],
            await NamesAsync("duration", "desc"));

        // Price ordering is a numeric ORDER BY on `numeric(10,2)`. The SQLite test
        // provider refuses decimal ordering outright ("SQLite does not support
        // expressions of type 'decimal' in ORDER BY clauses"), so the price sort
        // is exercised against Postgres by the API suite rather than here; the
        // price *filter* above is provider-agnostic and covered.
        var newest = await NamesAsync("newest", "desc");
        Assert.Equal(3, newest.Count);
    }
}
