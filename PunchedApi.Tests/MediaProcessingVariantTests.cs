using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkiaSharp;
using PunchedApi.Application.Media;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Tests;

/// <summary>
/// Regression coverage for the media processing pipeline's efficiency contract: one encoded
/// variant per DISTINCT rendered width, no duplicate R2 writes, and no per-variant HEAD
/// verification round trips.
/// </summary>
public sealed class MediaProcessingVariantTests
{
    private sealed record VariantRef(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("height")] int Height,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("transform")] string Transform);

    /// <summary>Counts storage round trips so redundant work becomes a test failure, not a guess.</summary>
    private sealed class CountingObjectStore : IObjectStore
    {
        public int PutCount;
        public int HeadCount;
        public readonly Dictionary<string, (byte[] Bytes, string ContentType, string CacheControl)> Items = new(StringComparer.Ordinal);

        public Task<PresignedObjectWrite> PresignPutAsync(ObjectStoreBucket bucket, string key, string contentType, DateTimeOffset expiresAt, CancellationToken cancellationToken)
            => Task.FromResult(new PresignedObjectWrite(new Uri($"https://upload.invalid/{Uri.EscapeDataString(key)}"), new Dictionary<string, string> { ["Content-Type"] = contentType }, expiresAt));

        public Task<ObjectMetadata?> HeadAsync(ObjectStoreBucket bucket, string key, CancellationToken cancellationToken)
        {
            HeadCount++;
            return Task.FromResult(Items.TryGetValue(key, out var item) ? new ObjectMetadata(item.Bytes.Length, item.ContentType) : null);
        }

        public Task<ObjectReadResult?> GetBoundedAsync(ObjectStoreBucket bucket, string key, long maxBytes, CancellationToken cancellationToken)
        {
            if (!Items.TryGetValue(key, out var item)) return Task.FromResult<ObjectReadResult?>(null);
            if (item.Bytes.LongLength > maxBytes) throw new InvalidDataException("OBJECT_TOO_LARGE");
            return Task.FromResult<ObjectReadResult?>(new(new MemoryStream(item.Bytes, writable: false), new(item.Bytes.Length, item.ContentType)));
        }

        public async Task PutAsync(ObjectStoreBucket bucket, string key, Stream content, string contentType, string cacheControl, CancellationToken cancellationToken)
        {
            PutCount++;
            await using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            Items[key] = (buffer.ToArray(), contentType, cacheControl);
        }

        public Task DeleteAsync(ObjectStoreBucket bucket, string key, CancellationToken cancellationToken)
        {
            Items.Remove(key);
            return Task.CompletedTask;
        }
    }

    private static MediaStorageOptions StorageOptions() => new()
    {
        PrivateBucket = "private",
        PublicBucket = "public",
        PublicBaseUrl = "https://media.example.test/",
        ServiceUrl = "https://account.r2.cloudflarestorage.com",
        VariantWidths = [320, 640, 1280],
        WorkerBatchSize = 1,
        CleanupBatchSize = 1
    };

    private static byte[] EncodeJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>Runs the pipeline for one source image and returns the persisted variant list.</summary>
    private static async Task<List<VariantRef>> ProcessAsync(CountingObjectStore store, int sourceWidth, int sourceHeight)
    {
        var mediaId = Guid.NewGuid();
        var sourceKey = $"pending/businesses/{Guid.NewGuid():N}/{mediaId:N}.bin";
        var source = EncodeJpeg(sourceWidth, sourceHeight);
        store.Items[sourceKey] = (source, "image/jpeg", "no-store");

        var options = Options.Create(StorageOptions());
        var urls = new MediaUrlFactory(options);
        var db = MediaProcessingTestDb.Create();
        var processor = new MediaProcessor(db, store, new MediaKeyFactory(), urls, new MediaValidator(options), options,
            new StubHttpClientFactory("https://media.example.test/"));

        // Real owner/business rows: the media FKs are enforced, so a fabricated id would fail.
        var owner = BookingTestBase.CreateOwner($"owner-{Guid.NewGuid():N}@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id);
        db.Users.Add(owner);
        db.Businesses.Add(business);
        await db.SaveChangesAsync();

        db.Media.Add(new Media
        {
            Id = mediaId,
            BusinessId = business.Id,
            UploadedByUserId = owner.Id,
            Purpose = MediaPurposes.BusinessGallery,
            SourceKey = sourceKey,
            Status = MediaStatus.Processing,
            DeclaredMimeType = "image/jpeg",
            ExpectedSizeBytes = source.Length,
            ProcessingAttempts = 1
        });
        await db.SaveChangesAsync();

        Assert.True(await processor.ProcessAsync(mediaId, CancellationToken.None));
        var media = await db.Media.FirstOrDefaultAsync(x => x.Id == mediaId);
        Assert.NotNull(media);
        Assert.Equal(MediaStatus.Ready, media!.Status);
        Assert.Equal(MediaVisibility.Public, media.Visibility);
        Assert.Equal(sourceWidth, media.Width);
        Assert.Equal(sourceHeight, media.Height);

        var variants = JsonSerializer.Deserialize<List<VariantRef>>(media.VariantsJson)!;
        await db.DisposeAsync();
        return variants;
    }

    [Fact]
    public async Task ProcessingWritesOneVariantPerDistinctRenderedWidth()
    {
        // A 200px source sits below every configured width, so all three collapse to 200px.
        // The previous loop encoded and uploaded three identical bitmaps under three keys.
        var store = new CountingObjectStore();
        var variants = await ProcessAsync(store, 200, 100);

        Assert.Equal(2, variants.Count);
        Assert.Single(variants.Select(v => v.Width).Distinct());
        Assert.Equal(200, variants[0].Width);
        Assert.Contains(variants, v => v.Format == "webp");
        Assert.Contains(variants, v => v.Format == "jpeg");

        // One PUT per variant; no per-variant HEAD verification round trips.
        Assert.Equal(2, store.PutCount);
        Assert.Equal(0, store.HeadCount);
        // Only delivery objects are immutable; the private source keeps its own policy.
        Assert.All(store.Items.Where(item => item.Key.StartsWith("delivery/", StringComparison.Ordinal)),
            item => Assert.Equal("public, max-age=31536000, immutable", item.Value.CacheControl));
    }

    [Fact]
    public async Task ProcessingDeduplicatesWidthsThatCollapseToTheSameRenderedSize()
    {
        // 500px wide: 320 renders below the source, while 640 and 1280 both render at 500px
        // because the pipeline never upscales. Those two must not be written twice.
        var store = new CountingObjectStore();
        var variants = await ProcessAsync(store, 500, 400);

        Assert.Equal(4, variants.Count);
        Assert.Equal(new[] { 320, 500 }, variants.Select(v => v.Width).Distinct().OrderBy(w => w));
        Assert.Equal(4, store.PutCount);
        Assert.Equal(0, store.HeadCount);
    }

    [Fact]
    public async Task ProcessingGeneratesEveryWidthWhenTheSourceIsLargeEnough()
    {
        var store = new CountingObjectStore();
        var variants = await ProcessAsync(store, 3000, 2000);

        Assert.Equal(6, variants.Count);
        Assert.Equal(new[] { 320, 640, 1280 }, variants.Select(v => v.Width).Distinct().OrderBy(w => w));
        Assert.Equal(6, store.PutCount);
        Assert.Equal(0, store.HeadCount);
    }

    /// <summary>Answers the pipeline's public-delivery probe without real network access.</summary>
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly string _publicBaseUrl;
        public StubHttpClientFactory(string publicBaseUrl) => _publicBaseUrl = publicBaseUrl;

        public HttpClient CreateClient(string name) => new(new StubHandler(_publicBaseUrl));

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly string _publicBaseUrl;
            public StubHandler(string publicBaseUrl) => _publicBaseUrl = publicBaseUrl;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                // Only the configured delivery host counts as reachable; anything else fails closed.
                var ok = request.RequestUri is not null
                    && request.RequestUri.Host.Equals(new Uri(_publicBaseUrl).Host, StringComparison.OrdinalIgnoreCase);
                var response = new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.NotFound);
                if (ok)
                {
                    // The pipeline requires a webp content type on the primary variant.
                    response.Content = new ByteArrayContent(Array.Empty<byte>());
                    response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/webp");
                }
                return Task.FromResult(response);
            }
        }
    }
}


