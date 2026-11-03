using Microsoft.Extensions.Options;
using Moq;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Media;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Tests;

/// <summary>
/// Owner editing of a gallery entry — caption, publication and position — and
/// what that means for the public gallery.
///
/// <para>The rule under test is that "attached" and "published" are different
/// things: an image can sit in the owner's gallery, keep its caption and its
/// place, and be invisible to visitors until the owner publishes it.</para>
/// </summary>
public sealed class GalleryEntryTests
{
    private static Media GalleryMedia(User owner, Business business, string url)
    {
        return new Media
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            UploadedByUserId = owner.Id,
            Purpose = MediaPurposes.BusinessGallery,
            SourceKey = $"private/source/{Guid.NewGuid():N}",
            Status = MediaStatus.Ready,
            Visibility = MediaVisibility.Private,
            Width = 1280,
            Height = 960,
            VariantsJson =
                $"[{{\"url\":\"{url}\",\"width\":1280,\"height\":960,\"format\":\"webp\",\"transform\":\"w1280\"}}]"
        };
    }

    private static MediaService CreateMediaService(
        Infrastructure.Data.ApplicationDbContext context, Business business)
    {
        var businessContext = new Mock<IBusinessContext>();
        businessContext.Setup(x => x.GetBusinessIdAsync()).ReturnsAsync(business.Id);
        businessContext.Setup(x => x.GetRole()).Returns("Business");

        var permissions = new Mock<IPermissionService>();
        permissions.Setup(x => x.HasPermission(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        return new MediaService(
            context,
            Mock.Of<IObjectStore>(),
            Mock.Of<IMediaKeyFactory>(),
            Mock.Of<IMediaUrlFactory>(),
            Mock.Of<IIdempotencyService>(),
            businessContext.Object,
            permissions.Object,
            Options.Create(new MediaStorageOptions()));
    }

    [Fact]
    public async Task Caption_visibility_and_position_are_saved_and_read_back()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var first = GalleryMedia(owner, business, "https://cdn.example/1.webp");
        var second = GalleryMedia(owner, business, "https://cdn.example/2.webp");
        await BookingTestBase.SeedAsync(context, first, second);

        var media = CreateMediaService(context, business);
        await media.AttachGalleryAsync(owner.Id, first.Id, 0, false, CancellationToken.None);
        await media.AttachGalleryAsync(owner.Id, second.Id, 1, false, CancellationToken.None);

        var updated = await media.UpdateGalleryEntryAsync(
            owner.Id, second.Id,
            new UpdateGalleryEntryRequest { Caption = "  Balayage in progress  ", IsVisible = false, SortOrder = 0 },
            CancellationToken.None);

        Assert.True(updated.Success, updated.Error?.Message);
        Assert.Equal("Balayage in progress", updated.Data!.Caption);
        Assert.False(updated.Data.IsVisible);
        Assert.Equal(0, updated.Data.SortOrder);

        // The other image shifted down, and the sequence stayed dense.
        var gallery = await media.GetBusinessGalleryAsync(owner.Id, CancellationToken.None);
        Assert.Equal(new[] { second.Id, first.Id }, gallery.Data!.Select(entry => entry.MediaId));
        Assert.Equal(new[] { 0, 1 }, gallery.Data.Select(entry => entry.SortOrder));
        Assert.Equal("Balayage in progress", gallery.Data[0].Caption);
    }

    [Fact]
    public async Task An_omitted_field_is_left_untouched()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var photo = GalleryMedia(owner, business, "https://cdn.example/1.webp");
        await BookingTestBase.SeedAsync(context, photo);

        var media = CreateMediaService(context, business);
        await media.AttachGalleryAsync(owner.Id, photo.Id, 0, false, CancellationToken.None);
        await media.UpdateGalleryEntryAsync(
            owner.Id, photo.Id,
            new UpdateGalleryEntryRequest { Caption = "Studio floor", IsVisible = false },
            CancellationToken.None);

        // Editing the caption must not silently re-publish the image.
        var captionOnly = await media.UpdateGalleryEntryAsync(
            owner.Id, photo.Id,
            new UpdateGalleryEntryRequest { Caption = "The studio floor" },
            CancellationToken.None);

        Assert.True(captionOnly.Success, captionOnly.Error?.Message);
        Assert.Equal("The studio floor", captionOnly.Data!.Caption);
        Assert.False(captionOnly.Data.IsVisible);

        // And an empty caption is a deliberate clear.
        var cleared = await media.UpdateGalleryEntryAsync(
            owner.Id, photo.Id, new UpdateGalleryEntryRequest { Caption = "   " }, CancellationToken.None);
        Assert.Null(cleared.Data!.Caption);
    }

    [Fact]
    public async Task An_overlong_caption_is_rejected_without_writing()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var photo = GalleryMedia(owner, business, "https://cdn.example/1.webp");
        await BookingTestBase.SeedAsync(context, photo);

        var media = CreateMediaService(context, business);
        await media.AttachGalleryAsync(owner.Id, photo.Id, 0, false, CancellationToken.None);

        var result = await media.UpdateGalleryEntryAsync(
            owner.Id, photo.Id,
            new UpdateGalleryEntryRequest { Caption = new string('x', 201) },
            CancellationToken.None);

        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
        Assert.Null(context.BusinessMedia.Single().Caption);
    }

    [Fact]
    public async Task Another_businesses_image_cannot_be_edited()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var otherOwner = BookingTestBase.CreateOwner("gallery-other@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other");
        await BookingTestBase.SeedAsync(context, owner, business, otherOwner, otherBusiness);

        var foreign = GalleryMedia(otherOwner, otherBusiness, "https://cdn.example/foreign.webp");
        await BookingTestBase.SeedAsync(context, foreign);

        var media = CreateMediaService(context, business);
        var result = await media.UpdateGalleryEntryAsync(
            owner.Id, foreign.Id,
            new UpdateGalleryEntryRequest { Caption = "Hijacked", IsVisible = false },
            CancellationToken.None);

        Assert.Equal("MEDIA_NOT_FOUND", result.Error?.Code);
    }

    [Fact]
    public async Task The_public_gallery_publishes_captions_and_hides_unpublished_images()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var published = GalleryMedia(owner, business, "https://cdn.example/published.webp");
        var hidden = GalleryMedia(owner, business, "https://cdn.example/hidden.webp");
        await BookingTestBase.SeedAsync(context, published, hidden);

        var media = CreateMediaService(context, business);
        await media.AttachGalleryAsync(owner.Id, published.Id, 0, false, CancellationToken.None);
        await media.AttachGalleryAsync(owner.Id, hidden.Id, 1, false, CancellationToken.None);
        await media.UpdateGalleryEntryAsync(
            owner.Id, published.Id,
            new UpdateGalleryEntryRequest { Caption = "Our first chair" },
            CancellationToken.None);
        await media.UpdateGalleryEntryAsync(
            owner.Id, hidden.Id, new UpdateGalleryEntryRequest { IsVisible = false }, CancellationToken.None);
        context.ChangeTracker.Clear();

        var gallery = new StorefrontGalleryService(
            context, new StubModuleEntitlements("gallery"),
            TestHelpers.CreateLogger<StorefrontGalleryService>());
        var result = await gallery.GetPublicGalleryAsync(business.Id);

        Assert.True(result.Success, result.Error?.Message);
        var image = Assert.Single(result.Data!.Images);
        Assert.Equal(published.Id, image.MediaId);
        Assert.Equal("Our first chair", image.Caption);
        Assert.DoesNotContain(result.Data.Images, entry => entry.MediaId == hidden.Id);
    }
}
