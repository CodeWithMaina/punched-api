using Microsoft.Data.Sqlite;
using System.Text.Json;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Application.Validators;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Phase 5 tests for ServiceCatalogService: owner CRUD, public active list, and owner isolation.
/// </summary>
public class ServiceCatalogServiceTests
{
    [Fact]
    public void CreateServiceValidator_RejectsInvalidCatalogValues()
    {
        var result = new CreateServiceRequestValidator().Validate(new CreateServiceRequest
        {
            Name = " ",
            Description = new string('x', 501),
            DurationMinutes = 4,
            Price = -0.01m
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(CreateServiceRequest.Name));
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(CreateServiceRequest.Description));
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(CreateServiceRequest.DurationMinutes));
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(CreateServiceRequest.Price));
    }

    [Fact]
    public void UpdateServiceValidator_AllowsPartialUpdatesAndRejectsInvalidDuration()
    {
        var validator = new UpdateServiceRequestValidator();

        Assert.True(validator.Validate(new UpdateServiceRequest { Showcase = false }).IsValid);
        Assert.False(validator.Validate(new UpdateServiceRequest { DurationMinutes = 1441 }).IsValid);
    }

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

    [Fact]
    public async Task CreateServiceAsync_SetsActiveAndPrice_ReturnsCreated()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var result = await env.Service.CreateServiceAsync(env.Owner.Id, new CreateServiceRequest
        {
            Name = "Manicure",
            DurationMinutes = 45,
            Price = 800m
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(result.Data!.IsActive);
        Assert.Equal(800m, result.Data.Price);
        Assert.Equal(45, result.Data.DurationMinutes);
        Assert.Equal(env.Business.Id, result.Data.BusinessId);
    }

    [Fact]
    public async Task PublicDetails_ExcludeHiddenInactiveAndOtherBusinessServices()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var created = await env.Service.CreateServiceAsync(env.Owner.Id, new CreateServiceRequest
        { Name = "Cut", DurationMinutes = 30, Price = 500 });
        var serviceId = created.Data!.Id;
        Assert.True((await env.Service.GetPublicServiceAsync(env.Business.Id, serviceId)).Success);
        await env.Service.UpdateServiceAsync(env.Owner.Id, serviceId, new UpdateServiceRequest { Showcase = false });
        Assert.Equal("NOT_FOUND", (await env.Service.GetPublicServiceAsync(env.Business.Id, serviceId)).Error?.Code);
        await env.Service.UpdateServiceAsync(env.Owner.Id, serviceId, new UpdateServiceRequest { Showcase = true, IsActive = false });
        Assert.Equal("NOT_FOUND", (await env.Service.GetPublicServiceAsync(env.Business.Id, serviceId)).Error?.Code);
        Assert.Equal("NOT_FOUND", (await env.Service.GetPublicServiceAsync(Guid.NewGuid(), serviceId)).Error?.Code);
    }

    [Fact]
    public async Task AdminCrud_IsBusinessScoped_AndIncludesInactiveServices()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var otherOwner = BookingTestBase.CreateOwner("admin-other@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other");
        await BookingTestBase.SeedAsync(context, otherOwner, otherBusiness);
        var created = await env.Service.CreateForBusinessAsync(env.Business.Id, new CreateServiceRequest
        { Name = "Cut", DurationMinutes = 30, Price = 500 });
        Assert.True(created.Success);
        Assert.Equal("FORBIDDEN", (await env.Service.UpdateForBusinessAsync(otherBusiness.Id, created.Data!.Id,
            new UpdateServiceRequest { Price = 999 })).Error?.Code);
        Assert.True((await env.Service.DeleteForBusinessAsync(env.Business.Id, created.Data!.Id)).Success);
        var all = await env.Service.GetAdminServicesAsync(null);
        Assert.Single(all.Data!);
        Assert.False(all.Data![0].IsActive);
        Assert.Equal(env.Business.Name, all.Data[0].BusinessName);
        Assert.Empty((await env.Service.GetAdminServicesAsync(otherBusiness.Id)).Data!);
        Assert.Equal("NOT_FOUND", (await env.Service.CreateForBusinessAsync(Guid.NewGuid(), new CreateServiceRequest
        { Name = "Cut", DurationMinutes = 30, Price = 500 })).Error?.Code);
    }

    [Fact]
    public async Task EligibleStaff_RejectsServicesFromAnotherBusiness()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var result = await env.Service.GetEligibleStaffAsync(env.Business.Id, new[] { Guid.NewGuid() });
        Assert.Equal("SERVICE_NOT_FOUND", result.Error?.Code);
    }

    [Fact]
    public async Task ActiveTenant_NarrowsOwnerCrud_PublicDetails_AndAdminList()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var tenant = new TenantContext();
        tenant.Set("other", Guid.NewGuid());
        var service = new ServiceCatalogService(new PunchedApi.Infrastructure.Repositories.UnitOfWork(context),
            TestHelpers.CreateLogger<ServiceCatalogService>(), new StubModuleEntitlements("serviceCatalog"), tenant);
        Assert.Equal("NOT_FOUND", (await service.GetMyServicesAsync(env.Owner.Id)).Error?.Code);
        Assert.Equal("NOT_FOUND", (await service.CreateServiceAsync(env.Owner.Id,
            new CreateServiceRequest { Name = "Cut", DurationMinutes = 30, Price = 500 })).Error?.Code);
        Assert.Equal("NOT_FOUND", (await service.UpdateServiceAsync(env.Owner.Id, Guid.NewGuid(), new UpdateServiceRequest { Price = 100 })).Error?.Code);
        Assert.Equal("NOT_FOUND", (await service.DeleteServiceAsync(env.Owner.Id, Guid.NewGuid())).Error?.Code);
        Assert.Equal("NOT_FOUND", (await service.GetPublicServiceAsync(env.Business.Id, Guid.NewGuid())).Error?.Code);
        Assert.Equal("NOT_FOUND", (await service.GetEligibleStaffAsync(env.Business.Id, Array.Empty<Guid>())).Error?.Code);
        Assert.Empty((await service.GetAdminServicesAsync(null)).Data!);
    }

    [Fact]
    public async Task AdminCrud_SupportsUnassignedBusinesses()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        env.Business.OwnerId = null;
        await context.SaveChangesAsync();
        var created = await env.Service.CreateForBusinessAsync(env.Business.Id,
            new CreateServiceRequest { Name = "Cut", DurationMinutes = 30, Price = 500 });
        Assert.True(created.Success);
        Assert.True((await env.Service.UpdateForBusinessAsync(env.Business.Id, created.Data!.Id, new UpdateServiceRequest { Price = 600 })).Success);
        Assert.True((await env.Service.DeleteForBusinessAsync(env.Business.Id, created.Data!.Id)).Success);
        Assert.Equal("NOT_FOUND", (await env.Service.GetMyServicesAsync(env.Owner.Id)).Error?.Code);
    }

    [Fact]
    public async Task TenantMismatch_NarrowsOwnerAdminAndPublicOperations()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var created = await env.Service.CreateServiceAsync(env.Owner.Id, new CreateServiceRequest
        { Name = "Cut", DurationMinutes = 30, Price = 500 });
        var tenant = new TenantContext();
        tenant.Set("other-business", Guid.NewGuid());
        var scoped = new ServiceCatalogService(new PunchedApi.Infrastructure.Repositories.UnitOfWork(context),
            TestHelpers.CreateLogger<ServiceCatalogService>(), new StubModuleEntitlements("serviceCatalog"), tenant);
        Assert.Equal("NOT_FOUND", (await scoped.GetMyServicesAsync(env.Owner.Id)).Error?.Code);
        Assert.Equal("NOT_FOUND", (await scoped.UpdateServiceAsync(env.Owner.Id, created.Data!.Id, new UpdateServiceRequest { Price = 999 })).Error?.Code);
        Assert.Equal("NOT_FOUND", (await scoped.DeleteServiceAsync(env.Owner.Id, created.Data.Id)).Error?.Code);
        Assert.Equal("NOT_FOUND", (await scoped.GetServicesForBusinessAsync(env.Business.Id)).Error?.Code);
        Assert.Equal("NOT_FOUND", (await scoped.GetEligibleStaffAsync(env.Business.Id, Array.Empty<Guid>())).Error?.Code);
        Assert.Empty((await scoped.GetAdminServicesAsync(null)).Data!);
    }

    [Fact]
    public async Task UpdateServiceAsync_AppliesProvidedFieldsOnly()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var created = await env.Service.CreateServiceAsync(env.Owner.Id, new CreateServiceRequest
        {
            Name = "Cut",
            DurationMinutes = 60,
            Price = 500m
        });
        var id = created.Data!.Id;

        var updated = await env.Service.UpdateServiceAsync(env.Owner.Id, id, new UpdateServiceRequest { Price = 650m });

        Assert.True(updated.Success, updated.Error?.Message);
        Assert.Equal("Cut", updated.Data!.Name);
        Assert.Equal(60, updated.Data.DurationMinutes);
        Assert.Equal(650m, updated.Data.Price);
        Assert.True(updated.Data.IsActive);
    }

    [Fact]
    public async Task DeleteServiceAsync_SoftDeletes_ReturnsTrue()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var created = await env.Service.CreateServiceAsync(env.Owner.Id, new CreateServiceRequest
        {
            Name = "Cut",
            DurationMinutes = 60,
            Price = 500m
        });
        var id = created.Data!.Id;

        var result = await env.Service.DeleteServiceAsync(env.Owner.Id, id);

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(result.Data);
        var svc = await context.ServiceCatalogItems.FindAsync(id);
        Assert.False(svc!.IsActive);
    }
[Fact]
    public async Task GetServicesForBusinessAsync_ReturnsOnlyActive_GetMyReturnsAll()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var active = await env.Service.CreateServiceAsync(env.Owner.Id, new CreateServiceRequest
        {
            Name = "Active",
            DurationMinutes = 30,
            Price = 100m
        });
        var inactive = await env.Service.CreateServiceAsync(env.Owner.Id, new CreateServiceRequest
        {
            Name = "Inactive",
            DurationMinutes = 30,
            Price = 100m
        });
        await env.Service.UpdateServiceAsync(env.Owner.Id, inactive.Data!.Id, new UpdateServiceRequest { IsActive = false });

        var pub = await env.Service.GetServicesForBusinessAsync(env.Business.Id);
        Assert.True(pub.Success, pub.Error?.Message);
        Assert.Single(pub.Data!);
        Assert.NotNull(active.Data);
        Assert.NotNull(pub.Data);
        Assert.Equal(active.Data.Id, pub.Data[0].Id);

        var mine = await env.Service.GetMyServicesAsync(env.Owner.Id);
        Assert.True(mine.Success, mine.Error?.Message);
        Assert.Equal(2, mine.Data!.Count);
    }

    [Fact]
    public async Task ServiceResponses_IncludeOnlyReadyPublicImageVariants()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var visible = await env.Service.CreateServiceAsync(env.Owner.Id, new CreateServiceRequest
        { Name = "Visible", DurationMinutes = 30, Price = 100 });
        var privateService = await env.Service.CreateServiceAsync(env.Owner.Id, new CreateServiceRequest
        { Name = "Private image", DurationMinutes = 30, Price = 100 });
        var publicMedia = new Media
        {
            Id = Guid.NewGuid(), BusinessId = env.Business.Id, UploadedByUserId = env.Owner.Id,
            Purpose = MediaPurposes.ServiceImage, SourceKey = "private/source/public-media",
            Status = MediaStatus.Ready, Visibility = MediaVisibility.Public,
            VariantsJson = JsonSerializer.Serialize(new[]
            {
                new MediaVariantResponse { Url = "https://cdn.example/320.webp", Width = 320, Height = 240, Format = "webp" },
                new MediaVariantResponse { Url = "https://cdn.example/1280.jpg", Width = 1280, Height = 960, Format = "jpeg" }
            })
        };
        var privateMedia = new Media
        {
            Id = Guid.NewGuid(), BusinessId = env.Business.Id, UploadedByUserId = env.Owner.Id,
            Purpose = MediaPurposes.ServiceImage, SourceKey = "private/source/private-media",
            Status = MediaStatus.Processing, Visibility = MediaVisibility.Private,
            VariantsJson = "[{\"url\":\"https://private.example/image.webp\",\"width\":320,\"height\":240,\"format\":\"webp\"}]"
        };
        context.Media.AddRange(publicMedia, privateMedia);
        context.ServiceMedia.AddRange(
            new ServiceMedia { ServiceCatalogItemId = visible.Data!.Id, MediaId = publicMedia.Id, Role = "Primary" },
            new ServiceMedia { ServiceCatalogItemId = privateService.Data!.Id, MediaId = privateMedia.Id, Role = "Primary" });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await env.Service.GetMyServicesAsync(env.Owner.Id);

        Assert.DoesNotContain(context.ChangeTracker.Entries(), entry =>
            entry.Entity is ServiceCatalogItem or ServiceMedia or Media);
        var visibleResponse = Assert.Single(result.Data!, item => item.Id == visible.Data.Id);
        Assert.Equal(publicMedia.Id, visibleResponse.ImageMediaId);
        Assert.Equal(new[] { "https://cdn.example/320.webp", "https://cdn.example/1280.jpg" },
            visibleResponse.ImageVariants.Select(variant => variant.Url));
        Assert.Equal("ready", visibleResponse.ImageStatus);
        var privateResponse = Assert.Single(result.Data!, item => item.Id == privateService.Data.Id);
        Assert.Equal(privateMedia.Id, privateResponse.ImageMediaId);
        Assert.Equal("processing", privateResponse.ImageStatus);
        Assert.Empty(privateResponse.ImageVariants);
    }

    [Fact]
    public async Task OwnerIsolation_SecondOwnerForbidden_UnknownNotFound_NoBusinessNotFound()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var otherOwner = BookingTestBase.CreateOwner("other@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other");
        var noBizOwner = BookingTestBase.CreateOwner("nobiz@test.com");
        await BookingTestBase.SeedAsync(context, otherOwner, otherBusiness, noBizOwner);

        var created = await env.Service.CreateServiceAsync(env.Owner.Id, new CreateServiceRequest
        {
            Name = "Cut",
            DurationMinutes = 60,
            Price = 500m
        });
        var id = created.Data!.Id;

        // second owner on another business's service → FORBIDDEN
        var get = await env.Service.GetServiceAsync(otherOwner.Id, id);
        Assert.Equal("FORBIDDEN", get.Error?.Code);
        var upd = await env.Service.UpdateServiceAsync(otherOwner.Id, id, new UpdateServiceRequest { Price = 999m });
        Assert.Equal("FORBIDDEN", upd.Error?.Code);
        var del = await env.Service.DeleteServiceAsync(otherOwner.Id, id);
        Assert.Equal("FORBIDDEN", del.Error?.Code);

        // unknown service → NOT_FOUND
        var unknown = await env.Service.GetServiceAsync(env.Owner.Id, Guid.NewGuid());
        Assert.Equal("NOT_FOUND", unknown.Error?.Code);

        // owner with no business → NOT_FOUND
        var noBiz = await env.Service.GetMyServicesAsync(noBizOwner.Id);
        Assert.Equal("NOT_FOUND", noBiz.Error?.Code);
    }
}