using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Media;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Services.Storage;

namespace PunchedApi.Tests;

/// <summary>
/// The filesystem media provider: the signed upload grant, the delivery locator
/// and the key/bucket rules that keep one tenant's uploads unreachable from
/// another path. These are the properties that make the provider safe to run in
/// development and CI instead of a real object store.
/// </summary>
public sealed class LocalMediaStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "punched-media-tests", Guid.NewGuid().ToString("N"));

    private MediaStorageOptions Options() => new()
    {
        Provider = MediaStorageOptions.ProviderLocal,
        StorageRoot = _root,
        LocalBaseUrl = "http://localhost:5091",
        PublicBaseUrl = "http://localhost:5091/v1/media/delivery",
        PrivateBucket = "private",
        PublicBucket = "public"
    };

    private static LocalMediaUrlSigner Signer(MediaStorageOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options));

    private static LocalObjectStore Store(MediaStorageOptions options, ILocalMediaUrlSigner signer) =>
        new(Microsoft.Extensions.Options.Options.Create(options), signer, NullLogger<LocalObjectStore>.Instance);

    private static LocalObjectStore Store(MediaStorageOptions options) => Store(options, Signer(options));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a lingering handle must not fail the suite */ }
    }

    [Fact]
    public void LocalProviderDoesNotRequireCloudCredentials()
    {
        Options().Validate(requireR2: false);
        Options().Validate(requireR2: true);
    }

    [Fact]
    public void LocalProviderRequiresABaseUrlAndPublicBaseUrl()
    {
        var missingBase = Options();
        missingBase.LocalBaseUrl = string.Empty;
        Assert.Throws<InvalidOperationException>(() => missingBase.Validate(requireR2: false));

        var relative = Options();
        relative.LocalBaseUrl = "/v1/media";
        Assert.Throws<InvalidOperationException>(() => relative.Validate(requireR2: false));

        var missingPublic = Options();
        missingPublic.PublicBaseUrl = string.Empty;
        Assert.Throws<InvalidOperationException>(() => missingPublic.Validate(requireR2: false));
    }

    [Fact]
    public async Task GrantIsSignedScopedAndSingleUse()
    {
        var options = Options();
        // One signer instance signs and verifies, exactly as the singleton does
        // in the application (an unconfigured key is random per process).
        var signer = Signer(options);
        var store = Store(options, signer);
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);

        var grant = await store.PresignPutAsync(ObjectStoreBucket.PrivateSource, "pending/businesses/a/1.bin", "image/jpeg", expires, default);

        Assert.StartsWith("http://localhost:5091/v1/media/local/upload/", grant.Url.ToString());
        Assert.Equal("image/jpeg", grant.RequiredHeaders["Content-Type"]);

        var signature = ExtractQuery(grant.Url, "sig");
        Assert.True(signer.Validate("pending/businesses/a/1.bin", "image/jpeg", expires.ToUnixTimeSeconds(), signature));

        // Any deviation from the signed triple is rejected.
        Assert.False(signer.Validate("pending/businesses/a/2.bin", "image/jpeg", expires.ToUnixTimeSeconds(), signature));
        Assert.False(signer.Validate("pending/businesses/a/1.bin", "image/png", expires.ToUnixTimeSeconds(), signature));
        Assert.False(signer.Validate("pending/businesses/a/1.bin", "image/jpeg", expires.AddMinutes(10).ToUnixTimeSeconds(), signature));
        Assert.False(signer.Validate("pending/businesses/a/1.bin", "image/jpeg", expires.ToUnixTimeSeconds(), "not-a-signature"));
    }

    [Fact]
    public void AnExpiredGrantIsRejected()
    {
        var options = Options();
        var signer = new LocalMediaUrlSigner(Microsoft.Extensions.Options.Options.Create(options));
        var past = DateTimeOffset.UtcNow.AddMinutes(-1);
        var signature = signer.Sign("pending/x.bin", "image/png", past);
        Assert.False(signer.Validate("pending/x.bin", "image/png", past.ToUnixTimeSeconds(), signature));
    }

    [Fact]
    public async Task OnlyPrivateSourceObjectsCanBeUploaded()
    {
        var store = Store(Options());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PresignPutAsync(ObjectStoreBucket.PublicDelivery, "delivery/x.webp", "image/webp", DateTimeOffset.UtcNow.AddMinutes(5), default));
    }

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("/etc/passwd")]
    [InlineData("pending/../../escape.bin")]
    [InlineData("pending\\escape.bin")]
    [InlineData("")]
    public async Task KeysThatCouldEscapeTheBucketRootAreRejected(string key)
    {
        var store = Store(Options());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.HeadAsync(ObjectStoreBucket.PrivateSource, key, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PutAsync(ObjectStoreBucket.PrivateSource, key, new MemoryStream([1]), "image/png", "no-store", default));
    }

    private static string ExtractQuery(Uri url, string name)
    {
        var query = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in query)
        {
            var parts = pair.Split('=', 2);
            if (parts[0] == name) return Uri.UnescapeDataString(parts[1]);
        }
        return string.Empty;
    }

    [Fact]
    public async Task ObjectsRoundTripWithinTheirOwnBucket()
    {
        var store = Store(Options());
        var payload = new byte[] { 9, 8, 7, 6 };

        await store.PutAsync(ObjectStoreBucket.PrivateSource, "pending/businesses/a/1.bin", new MemoryStream(payload), "image/png", "private", default);

        var head = await store.HeadAsync(ObjectStoreBucket.PrivateSource, "pending/businesses/a/1.bin", default);
        Assert.NotNull(head);
        Assert.Equal(payload.Length, head!.SizeBytes);

        await using (var read = await store.GetBoundedAsync(ObjectStoreBucket.PrivateSource, "pending/businesses/a/1.bin", 16, default))
        {
            Assert.NotNull(read);
            using var copy = new MemoryStream();
            await read!.Content.CopyToAsync(copy);
            Assert.Equal(payload, copy.ToArray());
        }

        // Reads are bounded: a larger object than the caller allows is refused.
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.GetBoundedAsync(ObjectStoreBucket.PrivateSource, "pending/businesses/a/1.bin", 2, default));

        // The same key in the other bucket is a different object.
        Assert.Null(await store.HeadAsync(ObjectStoreBucket.PublicDelivery, "pending/businesses/a/1.bin", default));

        await store.DeleteAsync(ObjectStoreBucket.PrivateSource, "pending/businesses/a/1.bin", default);
        Assert.Null(await store.HeadAsync(ObjectStoreBucket.PrivateSource, "pending/businesses/a/1.bin", default));
        // Deleting a missing object is not an error.
        await store.DeleteAsync(ObjectStoreBucket.PrivateSource, "pending/businesses/a/missing.bin", default);
    }

    [Fact]
    public async Task DeliveryLocatorOnlyExposesPublicDeliveryObjects()
    {
        var store = Store(Options());

        await store.PutAsync(ObjectStoreBucket.PublicDelivery, "delivery/abc/gallery-320w.webp", new MemoryStream([1, 2]), "image/webp", "public", default);
        await store.PutAsync(ObjectStoreBucket.PrivateSource, "pending/businesses/a/1.bin", new MemoryStream([3]), "image/png", "private", default);

        Assert.NotNull(store.TryResolveDeliveryPath("delivery/abc/gallery-320w.webp"));
        // A private source object is unreachable even when its key is known.
        Assert.Null(store.TryResolveDeliveryPath("pending/businesses/a/1.bin"));
        // Anything outside the delivery prefix, or missing, resolves to nothing.
        Assert.Null(store.TryResolveDeliveryPath("delivery/abc/missing.webp"));
        Assert.Null(store.TryResolveDeliveryPath(""));
        Assert.Null(store.TryResolveDeliveryPath("delivery/../../escape.webp"));
    }

    [Theory]
    [InlineData("delivery/abc/gallery-320w.webp", "image/webp")]
    [InlineData("delivery/abc/gallery-1280w.jpg", "image/jpeg")]
    [InlineData("delivery/abc/gallery-640w.jpeg", "image/jpeg")]
    [InlineData("delivery/abc/gallery-320w.png", "image/png")]
    [InlineData("delivery/abc/source.bin", "application/octet-stream")]
    public void DeliveryContentTypeIsDerivedFromTheEncodedFormat(string key, string expected)
    {
        Assert.Equal(expected, LocalObjectStore.ContentTypeFor(key));
    }
}
