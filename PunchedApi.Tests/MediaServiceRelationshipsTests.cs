using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Moq;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.Media;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Tests;

public sealed class MediaServiceRelationshipsTests
{
    [Fact]
    public async Task ServiceAttachmentAllowsCompletedMediaAndRejectsIncompleteOrFailedMedia()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);
        var serviceResult = await BookingTestBase.CreateCatalogService(context).CreateServiceAsync(owner.Id, new()
        {
            Name = "Image service", DurationMinutes = 30, Price = 100m
        });
        var serviceId = serviceResult.Data!.Id;
        var mediaRows = new[]
        {
            CreateMedia(owner, business, MediaStatus.Uploaded),
            CreateMedia(owner, business, MediaStatus.Processing),
            CreateMedia(owner, business, MediaStatus.Ready),
            CreateMedia(owner, business, MediaStatus.Pending),
            CreateMedia(owner, business, MediaStatus.Failed)
        };
        context.Media.AddRange(mediaRows);
        await context.SaveChangesAsync();

        var businessContext = new Mock<IBusinessContext>();
        businessContext.Setup(x => x.GetBusinessIdAsync()).ReturnsAsync(business.Id);
        businessContext.Setup(x => x.GetRole()).Returns("Business");
        var mediaService = new MediaService(
            context,
            Mock.Of<IObjectStore>(),
            Mock.Of<IMediaKeyFactory>(),
            Mock.Of<IMediaUrlFactory>(),
            Mock.Of<IIdempotencyService>(),
            businessContext.Object,
            Mock.Of<IPermissionService>(),
            Options.Create(new MediaStorageOptions()));

        foreach (var media in mediaRows.Take(3))
        {
            var result = await mediaService.AttachServiceAsync(owner.Id, serviceId, media.Id, 0, CancellationToken.None);
            Assert.True(result.Success, result.Error?.Message);
            var replay = await mediaService.AttachServiceAsync(owner.Id, serviceId, media.Id, 0, CancellationToken.None);
            Assert.True(replay.Success, replay.Error?.Message);
        }

        foreach (var media in mediaRows.Skip(3))
        {
            var result = await mediaService.AttachServiceAsync(owner.Id, serviceId, media.Id, 0, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal("MEDIA_NOT_ATTACHABLE", result.Error?.Code);
        }

        // Three images are attached (a service's full allowance) and exactly one
        // of them — the one at display order 0 — is the cover.
        var rows = context.ServiceMedia.Where(x => x.ServiceCatalogItemId == serviceId).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { 0, 1, 2 }, rows.OrderBy(x => x.SortOrder).Select(x => x.SortOrder));
        var primary = Assert.Single(rows.Where(x => x.Role == "Primary"));
        Assert.Equal(mediaRows[2].Id, primary.MediaId);
        Assert.Equal(0, primary.SortOrder);
    }

    [Fact]
    public async Task ServiceAttachmentRefusesAFourthImage()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);
        var serviceId = (await BookingTestBase.CreateCatalogService(context).CreateServiceAsync(owner.Id, new()
        {
            Name = "Four images", DurationMinutes = 30, Price = 100m
        })).Data!.Id;

        var photos = Enumerable.Range(0, 4)
            .Select(_ => CreateMedia(owner, business, MediaStatus.Ready))
            .ToArray();
        context.Media.AddRange(photos);
        await context.SaveChangesAsync();

        var businessContext = new Mock<IBusinessContext>();
        businessContext.Setup(x => x.GetBusinessIdAsync()).ReturnsAsync(business.Id);
        businessContext.Setup(x => x.GetRole()).Returns("Business");
        var mediaService = new MediaService(
            context,
            Mock.Of<IObjectStore>(),
            Mock.Of<IMediaKeyFactory>(),
            Mock.Of<IMediaUrlFactory>(),
            Mock.Of<IIdempotencyService>(),
            businessContext.Object,
            Mock.Of<IPermissionService>(),
            Options.Create(new MediaStorageOptions { MaxServiceImages = 3 }));

        foreach (var photo in photos.Take(3))
        {
            var attached = await mediaService.AttachServiceAsync(owner.Id, serviceId, photo.Id, 3, CancellationToken.None);
            Assert.True(attached.Success, attached.Error?.Message);
        }

        var fourth = await mediaService.AttachServiceAsync(owner.Id, serviceId, photos[3].Id, 3, CancellationToken.None);
        Assert.False(fourth.Success);
        Assert.Equal("MEDIA_QUOTA_EXCEEDED", fourth.Error?.Code);
        Assert.Equal(3, context.ServiceMedia.Count(x => x.ServiceCatalogItemId == serviceId));
    }

    private static Media CreateMedia(User owner, Business business, MediaStatus status) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = business.Id,
        UploadedByUserId = owner.Id,
        Purpose = MediaPurposes.ServiceImage,
        SourceKey = $"private/source/{Guid.NewGuid():N}",
        Status = status,
        Visibility = status == MediaStatus.Ready ? MediaVisibility.Public : MediaVisibility.Private
    };
}