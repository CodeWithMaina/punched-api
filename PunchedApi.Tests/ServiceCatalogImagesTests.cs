using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// The three-image contract for a service: ordering, cover derivation, the hard
/// ceiling, business isolation and the cleanup a delete performs.
///
/// <para>These assertions are deliberately about the observable API response
/// (<c>images</c>, ordered, with <c>isCover</c> on index 0) rather than about the
/// join table, so a future schema change cannot pass the tests while breaking the
/// gallery the frontend renders.</para>
/// </summary>
public sealed class ServiceCatalogImagesTests
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

    /// <summary>A service photo with two variants, ready unless told otherwise.</summary>
    private static Media CreatePhoto(Env env, string slug, MediaStatus status = MediaStatus.Ready) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = env.Business.Id,
        UploadedByUserId = env.Owner.Id,
        Purpose = MediaPurposes.ServiceImage,
        SourceKey = $"private/source/{slug}",
        Status = status,
        Visibility = status == MediaStatus.Ready ? MediaVisibility.Public : MediaVisibility.Private,
        Width = 800,
        Height = 1000,
        VariantsJson = status == MediaStatus.Ready
            ? $"[{{\"url\":\"https://cdn.example/{slug}-320.webp\",\"width\":320,\"height\":400,\"format\":\"webp\"}}," +
              $"{{\"url\":\"https://cdn.example/{slug}-1280.jpg\",\"width\":1280,\"height\":1600,\"format\":\"jpeg\"}}]"
            : "[]"
    };

    private static async Task<Guid> CreateServiceAsync(Env env, string name = "Creative Colour")
    {
        var created = await env.Service.CreateServiceAsync(env.Owner.Id, new CreateServiceRequest
        {
            Name = name, DurationMinutes = 120, Price = 5200m
        });
        Assert.True(created.Success, created.Error?.Message);
        return created.Data!.Id;
    }

    [Fact]
    public async Task SetServiceImagesAsync_KeepsTheSentOrderAndDerivesTheCoverFromIndexZero()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var serviceId = await CreateServiceAsync(env);
        var photos = new[] { CreatePhoto(env, "a"), CreatePhoto(env, "b"), CreatePhoto(env, "c") };
        context.Media.AddRange(photos);
        await context.SaveChangesAsync();

        // Deliberately not in creation order: the array order IS the gallery order.
        var result = await env.Service.SetServiceImagesAsync(
            env.Owner.Id, serviceId, [photos[2].Id, photos[0].Id, photos[1].Id]);

        Assert.True(result.Success, result.Error?.Message);
        var images = result.Data!;
        Assert.Equal(3, images.Count);
        Assert.Equal(new[] { photos[2].Id, photos[0].Id, photos[1].Id }, images.Select(image => image.MediaId));
        Assert.Equal(new[] { 0, 1, 2 }, images.Select(image => image.DisplayOrder));
        Assert.Equal(new[] { true, false, false }, images.Select(image => image.IsCover));
        Assert.Equal("https://cdn.example/c-1280.jpg", images[0].Url);

        // Re-reading returns the same order, so the write is persisted, not cached.
        var reread = await env.Service.GetServiceImagesAsync(env.Owner.Id, serviceId);
        Assert.Equal(new[] { photos[2].Id, photos[0].Id, photos[1].Id }, reread.Data!.Select(image => image.MediaId));

        // The legacy single-image fields keep describing the cover.
        var service = await env.Service.GetServiceAsync(env.Owner.Id, serviceId);
        Assert.Equal(photos[2].Id, service.Data!.ImageMediaId);
        Assert.Equal("ready", service.Data.ImageStatus);
        Assert.Equal("https://cdn.example/c-1280.jpg", service.Data.ImageVariants.Last().Url);
    }

    [Fact]
    public async Task SetServiceImagesAsync_ReordersInPlaceWithoutDroppingTheCover()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var serviceId = await CreateServiceAsync(env);
        var photos = new[] { CreatePhoto(env, "a"), CreatePhoto(env, "b") };
        context.Media.AddRange(photos);
        await context.SaveChangesAsync();

        await env.Service.SetServiceImagesAsync(env.Owner.Id, serviceId, [photos[0].Id, photos[1].Id]);
        var reordered = await env.Service.SetServiceImagesAsync(env.Owner.Id, serviceId, [photos[1].Id, photos[0].Id]);

        Assert.Equal(new[] { photos[1].Id, photos[0].Id }, reordered.Data!.Select(image => image.MediaId));
        Assert.Equal(new[] { true, false }, reordered.Data.Select(image => image.IsCover));

        // A reorder is a metadata write only: the media rows survive, so no
        // storage object is orphaned and no re-upload is implied.
        Assert.Equal(2, await context.Media.CountAsync());
        Assert.Equal(2, context.ServiceMedia.Count(link => link.ServiceCatalogItemId == serviceId));
    }

    [Fact]
    public async Task SetServiceImagesAsync_RejectsAFourthImageAndAllowsFewerThanThree()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var serviceId = await CreateServiceAsync(env);
        var photos = Enumerable.Range(0, 4).Select(index => CreatePhoto(env, $"p{index}")).ToArray();
        context.Media.AddRange(photos);
        await context.SaveChangesAsync();

        var tooMany = await env.Service.SetServiceImagesAsync(env.Owner.Id, serviceId, photos.Select(p => p.Id).ToArray());
        Assert.Equal("MEDIA_QUOTA_EXCEEDED", tooMany.Error?.Code);

        // Two images are perfectly valid: the ceiling is a maximum, not a target.
        var two = await env.Service.SetServiceImagesAsync(env.Owner.Id, serviceId, [photos[0].Id, photos[1].Id]);
        Assert.True(two.Success, two.Error?.Message);
        Assert.Equal(2, two.Data!.Count);

        // Sending the same photo twice does not consume two of the three slots.
        var duplicate = await env.Service.SetServiceImagesAsync(env.Owner.Id, serviceId, [photos[0].Id, photos[0].Id]);
        Assert.True(duplicate.Success, duplicate.Error?.Message);
        Assert.Single(duplicate.Data!);
    }

    [Fact]
    public async Task SetServiceImagesAsync_RemovesUnlistedImagesAndCanClearEveryImage()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var serviceId = await CreateServiceAsync(env);
        var photos = new[] { CreatePhoto(env, "a"), CreatePhoto(env, "b"), CreatePhoto(env, "c") };
        context.Media.AddRange(photos);
        await context.SaveChangesAsync();

        await env.Service.SetServiceImagesAsync(env.Owner.Id, serviceId, photos.Select(p => p.Id).ToArray());

        var reduced = await env.Service.SetServiceImagesAsync(env.Owner.Id, serviceId, [photos[1].Id]);
        Assert.Equal([photos[1].Id], reduced.Data!.Select(image => image.MediaId));
        Assert.True(reduced.Data[0].IsCover);
        Assert.Single(context.ServiceMedia.Where(link => link.ServiceCatalogItemId == serviceId));

        var cleared = await env.Service.SetServiceImagesAsync(env.Owner.Id, serviceId, []);
        Assert.True(cleared.Success, cleared.Error?.Message);
        Assert.Empty(cleared.Data!);
        Assert.Empty(context.ServiceMedia.Where(link => link.ServiceCatalogItemId == serviceId));
        Assert.Null((await env.Service.GetServiceAsync(env.Owner.Id, serviceId)).Data!.ImageMediaId);
    }

    [Fact]
    public async Task SetServiceImagesAsync_RejectsForeignOrUnfinishedMedia()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var serviceId = await CreateServiceAsync(env);

        var otherOwner = BookingTestBase.CreateOwner("image-other@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other");
        await BookingTestBase.SeedAsync(context, otherOwner, otherBusiness);

        var foreign = CreatePhoto(env, "foreign");
        foreign.BusinessId = otherBusiness.Id;
        foreign.UploadedByUserId = otherOwner.Id;
        var pending = CreatePhoto(env, "pending", MediaStatus.Pending);
        context.Media.AddRange(foreign, pending);
        await context.SaveChangesAsync();

        var crossTenant = await env.Service.SetServiceImagesAsync(env.Owner.Id, serviceId, [foreign.Id]);
        Assert.Equal("MEDIA_NOT_ATTACHABLE", crossTenant.Error?.Code);

        var unfinished = await env.Service.SetServiceImagesAsync(env.Owner.Id, serviceId, [pending.Id]);
        Assert.Equal("MEDIA_NOT_ATTACHABLE", unfinished.Error?.Code);

        var unknown = await env.Service.SetServiceImagesAsync(env.Owner.Id, serviceId, [Guid.NewGuid()]);
        Assert.Equal("MEDIA_NOT_FOUND", unknown.Error?.Code);
    }

    [Fact]
    public async Task ImageOperations_AreOwnerScoped()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var serviceId = await CreateServiceAsync(env);
        var photo = CreatePhoto(env, "scoped");
        context.Media.Add(photo);
        await context.SaveChangesAsync();

        var otherOwner = BookingTestBase.CreateOwner("images-other@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other");
        await BookingTestBase.SeedAsync(context, otherOwner, otherBusiness);

        // Another business can neither read nor rewrite this service's images.
        Assert.Equal("FORBIDDEN", (await env.Service.GetServiceImagesAsync(otherOwner.Id, serviceId)).Error?.Code);
        Assert.Equal("FORBIDDEN", (await env.Service.SetServiceImagesAsync(otherOwner.Id, serviceId, [photo.Id])).Error?.Code);

        // An unknown service is a 404, never a silent no-op.
        Assert.Equal("NOT_FOUND", (await env.Service.GetServiceImagesAsync(env.Owner.Id, Guid.NewGuid())).Error?.Code);
    }

    [Fact]
    public async Task DeleteServiceAsync_RemovesTheServiceImageRows()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var serviceId = await CreateServiceAsync(env);
        var photos = new[] { CreatePhoto(env, "a"), CreatePhoto(env, "b") };
        context.Media.AddRange(photos);
        await context.SaveChangesAsync();
        await env.Service.SetServiceImagesAsync(env.Owner.Id, serviceId, photos.Select(p => p.Id).ToArray());

        var deleted = await env.Service.DeleteServiceAsync(env.Owner.Id, serviceId);

        Assert.True(deleted.Success, deleted.Error?.Message);
        Assert.Empty(context.ServiceMedia.Where(link => link.ServiceCatalogItemId == serviceId));
        // The media rows themselves are kept: they carry their own retention policy.
        Assert.Equal(2, await context.Media.CountAsync());
    }

    [Fact]
    public async Task ServicesWithoutImages_StillMap()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var serviceId = await CreateServiceAsync(env, "No photos yet");

        var service = await env.Service.GetServiceAsync(env.Owner.Id, serviceId);

        Assert.True(service.Success, service.Error?.Message);
        Assert.Empty(service.Data!.Images);
        Assert.Null(service.Data.ImageMediaId);
        Assert.Equal(ServiceCatalogStatuses.Active, service.Data.Status);
    }
}
