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

        var primary = Assert.Single(context.ServiceMedia.Where(x => x.ServiceCatalogItemId == serviceId && x.Role == "Primary"));
        Assert.Equal(mediaRows[2].Id, primary.MediaId);
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