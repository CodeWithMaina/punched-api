using Microsoft.Extensions.Options;
using PunchedApi.Application.Media;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Services.Storage;

namespace PunchedApi.Tests;

public sealed class MediaStorageTests
{
    private static MediaStorageOptions Options() => new()
    {
        PrivateBucket = "private", PublicBucket = "public",
        PublicBaseUrl = "https://media.example.test/", ServiceUrl = "https://account.r2.cloudflarestorage.com"
    };

    [Fact]
    public void KeysAreServerGeneratedAndCannotUseClientPaths()
    {
        var factory = new MediaKeyFactory();
        var businessId = Guid.NewGuid(); var mediaId = Guid.NewGuid();
        var media = new Media { Id = mediaId, BusinessId = businessId, Purpose = MediaPurposes.BusinessGallery };
        Assert.Equal($"pending/businesses/{businessId:N}/{mediaId:N}.bin", factory.CreatePendingKey(media));
        Assert.Equal($"delivery/{mediaId:N}/gallery-focal-1280w.webp", factory.CreateDeliveryKey(mediaId, "gallery-focal", 1280, "webp"));
        Assert.Throws<ArgumentException>(() => factory.CreateDeliveryKey(mediaId, "../secret", 100, "webp"));
    }

    [Fact]
    public void PublicUrlNeverReturnsAStoreKeySeparately()
    {
        var factory = new MediaUrlFactory(Microsoft.Extensions.Options.Options.Create(Options()));
        Assert.Equal("https://media.example.test/delivery/abc/gallery-320w.jpg", factory.CreatePublicUrl("delivery/abc/gallery-320w.jpg"));
    }

    [Fact]
    public async Task FakeStoreSignsContentTypeAndBoundsReads()
    {
        var store = new InMemoryObjectStore();
        var grant = await store.PresignPutAsync(ObjectStoreBucket.PrivateSource, "pending/a.bin", "image/png", DateTimeOffset.UtcNow.AddMinutes(5), default);
        Assert.Equal("image/png", grant.RequiredHeaders["Content-Type"]);
        await store.PutAsync(ObjectStoreBucket.PrivateSource, "pending/a.bin", new MemoryStream(new byte[] { 1, 2, 3 }), "image/png", "no-store", default);
        Assert.Equal(3, (await store.HeadAsync(ObjectStoreBucket.PrivateSource, "pending/a.bin", default))!.SizeBytes);
        await using var read = await store.GetBoundedAsync(ObjectStoreBucket.PrivateSource, "pending/a.bin", 3, default);
        Assert.NotNull(read);
        using var copy = new MemoryStream(); await read!.Content.CopyToAsync(copy);
        Assert.Equal(3, copy.Length);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetBoundedAsync(ObjectStoreBucket.PrivateSource, "pending/a.bin", 2, default));
        await store.DeleteAsync(ObjectStoreBucket.PrivateSource, "missing", default);
    }

    [Fact]
    public void ProductionValidationRejectsInsecureOrMissingConfiguration()
    {
        Assert.Throws<InvalidOperationException>(() => Options().Validate(true));
        var valid = Options(); valid.AccountId = "account"; valid.AccessKeyId = "key"; valid.SecretAccessKey = "secret";
        valid.Validate(true);
        valid.PublicBaseUrl = "http://insecure.example";
        Assert.Throws<InvalidOperationException>(() => valid.Validate(true));
    }
}
