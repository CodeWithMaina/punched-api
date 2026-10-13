using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Moq;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.Media;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Business-gallery capacity, idempotency, and featured-image semantics.
/// Uses SQLite in-memory because <see cref="MediaService.AttachGalleryAsync"/> opens a
/// transaction; the Postgres FOR UPDATE lock is skipped for SQLite (see LockBusinessForUpdateAsync).
/// </summary>
public sealed class MediaGalleryQuotaTests
{
    private const int GalleryLimit = 3;

    private static MediaService CreateService(ApplicationDbContext context, IBusinessContext businessContext, MediaStorageOptions? options = null)
        => new(
            context,
            Mock.Of<IObjectStore>(),
            Mock.Of<IMediaKeyFactory>(),
            Mock.Of<IMediaUrlFactory>(),
            Mock.Of<IIdempotencyService>(),
            businessContext,
            Mock.Of<IPermissionService>(),
            Options.Create(options ?? new MediaStorageOptions { MaxGalleryImages = GalleryLimit }));

    private static IBusinessContext BusinessContext(Guid businessId, string role = "Business")
    {
        var ctx = new Mock<IBusinessContext>();
        ctx.Setup(x => x.GetBusinessIdAsync()).ReturnsAsync(businessId);
        ctx.Setup(x => x.GetRole()).Returns(role);
        return ctx.Object;
    }

    private static Media CreateGalleryMedia(Guid businessId, Guid ownerId, MediaStatus status = MediaStatus.Ready) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        UploadedByUserId = ownerId,
        Purpose = MediaPurposes.BusinessGallery,
        SourceKey = $"private/source/{Guid.NewGuid():N}",
        Status = status,
        Visibility = status == MediaStatus.Ready ? MediaVisibility.Public : MediaVisibility.Private
    };

    private static async Task<(ApplicationDbContext Context, User Owner, Business Business)> NewContextAsync()
    {
        var connection = BookingTestBase.CreateConnection();
        var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);
        return (context, owner, business);
    }

    [Fact]
    public async Task AttachEnforcesCapacityAndReportsQuotaExceeded()
    {
        var (context, owner, business) = await NewContextAsync();
        var service = CreateService(context, BusinessContext(business.Id));

        for (var i = 0; i < GalleryLimit; i++)
        {
            var media = CreateGalleryMedia(business.Id, owner.Id);
            await BookingTestBase.SeedAsync(context, media);
            var result = await service.AttachGalleryAsync(owner.Id, media.Id, i, featured: false, CancellationToken.None);
            Assert.True(result.Success, result.Error?.Message);
        }

        var overflow = CreateGalleryMedia(business.Id, owner.Id);
        await BookingTestBase.SeedAsync(context, overflow);
        var overflowResult = await service.AttachGalleryAsync(owner.Id, overflow.Id, GalleryLimit, featured: false, CancellationToken.None);
        Assert.False(overflowResult.Success);
        Assert.Equal("MEDIA_QUOTA_EXCEEDED", overflowResult.Error?.Code);
        Assert.Equal(GalleryLimit, context.BusinessMedia.Count(x => x.BusinessId == business.Id));
    }

    [Fact]
    public async Task ReAttachingExistingMediaDoesNotConsumeCapacityTwice()
    {
        var (context, owner, business) = await NewContextAsync();
        var service = CreateService(context, BusinessContext(business.Id));

        for (var i = 0; i < GalleryLimit; i++)
        {
            var media = CreateGalleryMedia(business.Id, owner.Id);
            await BookingTestBase.SeedAsync(context, media);
            var attach = await service.AttachGalleryAsync(owner.Id, media.Id, i, featured: false, CancellationToken.None);
            Assert.True(attach.Success);
        }

        // Gallery is full, but a retried/idempotent re-attach of an already-attached image succeeds
        // and does not add a second row.
        var existingMediaId = context.BusinessMedia.First().MediaId;
        var replay = await service.AttachGalleryAsync(owner.Id, existingMediaId, 0, featured: true, CancellationToken.None);
        Assert.True(replay.Success, replay.Error?.Message);
        Assert.True(replay.Data!.Featured);
        Assert.Equal(GalleryLimit, context.BusinessMedia.Count(x => x.BusinessId == business.Id));
    }

    [Fact]
    public async Task DetachingFreesCapacityForANewAttachment()
    {
        var (context, owner, business) = await NewContextAsync();
        var service = CreateService(context, BusinessContext(business.Id));

        var attachedMedia = new List<Media>();
        for (var i = 0; i < GalleryLimit; i++)
        {
            var media = CreateGalleryMedia(business.Id, owner.Id);
            attachedMedia.Add(media);
            await BookingTestBase.SeedAsync(context, media);
            await service.AttachGalleryAsync(owner.Id, media.Id, i, featured: i == 0, CancellationToken.None);
        }

        // Detaching the featured first image must both free a slot and promote the next image.
        var detach = await service.DetachRelationshipAsync(owner.Id, "business-gallery", Guid.Empty, attachedMedia[0].Id, CancellationToken.None);
        Assert.True(detach.Success, detach.Error?.Message);

        var capacity = await service.GetBusinessGalleryCapacityAsync(owner.Id, CancellationToken.None);
        Assert.True(capacity.Success);
        Assert.Equal(GalleryLimit, capacity.Data!.Limit);
        Assert.Equal(GalleryLimit - 1, capacity.Data.Used);
        Assert.Equal(1, capacity.Data.Remaining);

        // Exactly one featured image remains, and it is the newly promoted one (old second image).
        var featured = Assert.Single(context.BusinessMedia.Where(x => x.IsFeatured));
        Assert.Equal(attachedMedia[1].Id, featured.MediaId);

        // The freed slot accepts a new image.
        var fresh = CreateGalleryMedia(business.Id, owner.Id);
        await BookingTestBase.SeedAsync(context, fresh);
        var attachFresh = await service.AttachGalleryAsync(owner.Id, fresh.Id, 0, featured: false, CancellationToken.None);
        Assert.True(attachFresh.Success, attachFresh.Error?.Message);
        Assert.Equal(GalleryLimit, context.BusinessMedia.Count(x => x.BusinessId == business.Id));
    }

    [Fact]
    public async Task UnlimitedGalleryWhenLimitIsZeroOrLess()
    {
        var (context, owner, business) = await NewContextAsync();
        var service = CreateService(context, BusinessContext(business.Id), new MediaStorageOptions { MaxGalleryImages = 0 });

        for (var i = 0; i < GalleryLimit + 4; i++)
        {
            var media = CreateGalleryMedia(business.Id, owner.Id);
            await BookingTestBase.SeedAsync(context, media);
            var result = await service.AttachGalleryAsync(owner.Id, media.Id, i, featured: false, CancellationToken.None);
            Assert.True(result.Success, result.Error?.Message);
        }

        var capacity = await service.GetBusinessGalleryCapacityAsync(owner.Id, CancellationToken.None);
        Assert.Null(capacity.Data!.Limit);
        Assert.Null(capacity.Data.Remaining);
        Assert.Equal(GalleryLimit + 4, capacity.Data.Used);
    }

    [Fact]
    public async Task CapacityIsScopedPerBusiness()
    {
        var connection = BookingTestBase.CreateConnection();
        var context = BookingTestBase.CreateContext(connection);
        var ownerA = BookingTestBase.CreateOwner("ownerA@test.com");
        var businessA = BookingTestBase.CreateBusiness(ownerA.Id, "Business A");
        var ownerB = BookingTestBase.CreateOwner("ownerB@test.com");
        var businessB = BookingTestBase.CreateBusiness(ownerB.Id, "Business B");
        await BookingTestBase.SeedAsync(context, ownerA, businessA, ownerB, businessB);

        var serviceA = CreateService(context, BusinessContext(businessA.Id));
        for (var i = 0; i < GalleryLimit; i++)
        {
            var media = CreateGalleryMedia(businessA.Id, ownerA.Id);
            await BookingTestBase.SeedAsync(context, media);
            await serviceA.AttachGalleryAsync(ownerA.Id, media.Id, i, featured: false, CancellationToken.None);
        }

        // A full gallery for business A does not block business B.
        var serviceB = CreateService(context, BusinessContext(businessB.Id));
        var mediaB = CreateGalleryMedia(businessB.Id, ownerB.Id);
        await BookingTestBase.SeedAsync(context, mediaB);
        var attachB = await serviceB.AttachGalleryAsync(ownerB.Id, mediaB.Id, 0, featured: false, CancellationToken.None);
        Assert.True(attachB.Success, attachB.Error?.Message);
    }

    [Fact]
    public async Task DetachingNonFeaturedImageDoesNotChangeFeatured()
    {
        var (context, owner, business) = await NewContextAsync();
        var service = CreateService(context, BusinessContext(business.Id));

        var media = new List<Media>();
        for (var i = 0; i < GalleryLimit; i++)
        {
            var m = CreateGalleryMedia(business.Id, owner.Id);
            media.Add(m);
            await BookingTestBase.SeedAsync(context, m);
            await service.AttachGalleryAsync(owner.Id, m.Id, i, featured: i == 1, CancellationToken.None);
        }

        // Remove a non-featured image (index 0); featured (index 1) must be untouched.
        await service.DetachRelationshipAsync(owner.Id, "business-gallery", Guid.Empty, media[0].Id, CancellationToken.None);
        var featured = Assert.Single(context.BusinessMedia.Where(x => x.IsFeatured));
        Assert.Equal(media[1].Id, featured.MediaId);
    }
}
