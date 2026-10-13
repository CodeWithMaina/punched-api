using System.Net;
using Microsoft.EntityFrameworkCore;
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
/// Regression coverage for the media lifecycle RECOVERY scenarios: completion idempotency
/// (replay + conflict), completion validation failures, processing retry/backoff, terminal
/// failure at the attempt cap, manual retry semantics, and the recovered-attempt metric.
/// These are the paths that must stay correct for an upload to survive crashes, double
/// clicks, and worker restarts without manual repair.
/// </summary>
public sealed class MediaRecoveryTests
{
    /// <summary>
    /// In-memory idempotency store with the same semantics as the production service:
    /// first response wins, and the same key against a different request hash is a conflict.
    /// </summary>
    private sealed class InMemoryIdempotencyService : IIdempotencyService
    {
        private readonly Dictionary<(string Key, Guid UserId), (string Hash, string Json)> _entries = new();

        public Task<IdempotencyLookupResult> TryGetAsync(string key, Guid userId, string requestHash)
        {
            if (!_entries.TryGetValue((key, userId), out var entry))
                return Task.FromResult(new IdempotencyLookupResult());
            if (!string.Equals(entry.Hash, requestHash, StringComparison.Ordinal))
                return Task.FromResult(new IdempotencyLookupResult { Found = true, Conflict = true });
            return Task.FromResult(new IdempotencyLookupResult { Found = true, ResponseJson = entry.Json });
        }

        public Task StoreAsync(string key, Guid userId, string requestHash, string responseJson)
        {
            _entries.TryAdd((key, userId), (requestHash, responseJson));
            return Task.CompletedTask;
        }
    }

    /// <summary>Object store stub where only the HEAD behaviour matters for completion.</summary>
    private sealed class HeadOnlyObjectStore : IObjectStore
    {
        public ObjectMetadata? HeadResult;
        public int HeadCount;

        public Task<ObjectMetadata?> HeadAsync(ObjectStoreBucket bucket, string key, CancellationToken cancellationToken)
        {
            HeadCount++;
            return Task.FromResult(HeadResult);
        }

        public Task<PresignedObjectWrite> PresignPutAsync(ObjectStoreBucket bucket, string key, string contentType, DateTimeOffset expiresAt, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ObjectReadResult?> GetBoundedAsync(ObjectStoreBucket bucket, string key, long maxBytes, CancellationToken cancellationToken)
            => Task.FromResult<ObjectReadResult?>(null);

        public Task PutAsync(ObjectStoreBucket bucket, string key, Stream content, string contentType, string cacheControl, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task DeleteAsync(ObjectStoreBucket bucket, string key, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private static MediaService CreateService(ApplicationDbContext context, IObjectStore store, User owner, Business business)
    {
        var businessContext = new Mock<IBusinessContext>();
        businessContext.Setup(x => x.GetBusinessIdAsync()).ReturnsAsync(business.Id);
        businessContext.Setup(x => x.GetRole()).Returns("Business");
        return new MediaService(
            context,
            store,
            Mock.Of<IMediaKeyFactory>(),
            Mock.Of<IMediaUrlFactory>(),
            new InMemoryIdempotencyService(),
            businessContext.Object,
            Mock.Of<IPermissionService>(),
            Options.Create(new MediaStorageOptions()));
    }

    private static Media CreatePendingMedia(User owner, Business business, string sourceKey, long? expectedSize)
        => new()
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id, // ck_media_owner_xor: business-owned media leaves OwnerUserId null
            OwnerUserId = null,
            UploadedByUserId = owner.Id,
            Purpose = MediaPurposes.BusinessGallery,
            SourceKey = sourceKey,
            Status = MediaStatus.Pending,
            DeclaredMimeType = "image/jpeg",
            ExpectedSizeBytes = expectedSize
        };

    [Fact]
    public async Task CompletionReplayReturnsTheSameResponseWithoutASecondTransition()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner($"owner-{Guid.NewGuid():N}@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var store = new HeadOnlyObjectStore { HeadResult = new ObjectMetadata(100, "image/jpeg") };
        var media = CreatePendingMedia(owner, business, $"pending/{Guid.NewGuid():N}", 100);
        context.Media.Add(media);
        await context.SaveChangesAsync();
        var service = CreateService(context, store, owner, business);

        var first = await service.CompleteAsync(owner.Id, media.Id, "key-1", CancellationToken.None);
        Assert.True(first.Success, first.Error?.Message);
        Assert.Equal("Uploaded", first.Data!.Status);
        var firstUploadedAt = (await context.Media.FirstAsync(x => x.Id == media.Id)).UploadedAt;
        Assert.NotNull(firstUploadedAt);

        var replay = await service.CompleteAsync(owner.Id, media.Id, "key-1", CancellationToken.None);
        Assert.True(replay.Success, replay.Error?.Message);
        Assert.Equal(first.Data.Id, replay.Data!.Id);
        Assert.Equal(first.Data.Status, replay.Data.Status);

        var after = await context.Media.FirstAsync(x => x.Id == media.Id);
        Assert.Equal(firstUploadedAt, after.UploadedAt);
        // The replay must not re-probe storage: one HEAD for the whole sequence.
        Assert.Equal(1, store.HeadCount);
    }


    [Fact]
    public async Task CompletionWithTheSameKeyOnDifferentMediaIsAConflict()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner($"owner-{Guid.NewGuid():N}@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var store = new HeadOnlyObjectStore { HeadResult = new ObjectMetadata(100, "image/jpeg") };
        var mediaA = CreatePendingMedia(owner, business, $"pending/{Guid.NewGuid():N}", 100);
        var mediaB = CreatePendingMedia(owner, business, $"pending/{Guid.NewGuid():N}", 100);
        context.Media.AddRange(mediaA, mediaB);
        await context.SaveChangesAsync();
        var service = CreateService(context, store, owner, business);

        Assert.True((await service.CompleteAsync(owner.Id, mediaA.Id, "shared-key", CancellationToken.None)).Success);
        var conflict = await service.CompleteAsync(owner.Id, mediaB.Id, "shared-key", CancellationToken.None);
        Assert.False(conflict.Success);
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Error!.Code);
        // The conflicting request must not have touched media B.
        Assert.Equal(MediaStatus.Pending, (await context.Media.FirstAsync(x => x.Id == mediaB.Id)).Status);
    }

    [Fact]
    public async Task CompletionWithoutAnUploadedObjectFailsAndLeavesTheRowPending()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner($"owner-{Guid.NewGuid():N}@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var store = new HeadOnlyObjectStore { HeadResult = null };
        var media = CreatePendingMedia(owner, business, $"pending/{Guid.NewGuid():N}", 100);
        context.Media.Add(media);
        await context.SaveChangesAsync();
        var service = CreateService(context, store, owner, business);

        var result = await service.CompleteAsync(owner.Id, media.Id, "missing-object", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("MEDIA_UPLOAD_MISSING", result.Error!.Code);

        var row = await context.Media.FirstAsync(x => x.Id == media.Id);
        Assert.Equal(MediaStatus.Pending, row.Status);
        Assert.Null(row.UploadedAt);
    }

    [Fact]
    public async Task CompletionRejectsASizeMismatchAndLeavesTheRowPending()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner($"owner-{Guid.NewGuid():N}@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var store = new HeadOnlyObjectStore { HeadResult = new ObjectMetadata(999, "image/jpeg") };
        var media = CreatePendingMedia(owner, business, $"pending/{Guid.NewGuid():N}", 100);
        context.Media.Add(media);
        await context.SaveChangesAsync();
        var service = CreateService(context, store, owner, business);

        var result = await service.CompleteAsync(owner.Id, media.Id, "size-mismatch", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("MEDIA_SIZE_MISMATCH", result.Error!.Code);

        var row = await context.Media.FirstAsync(x => x.Id == media.Id);
        Assert.Equal(MediaStatus.Pending, row.Status);
        Assert.Null(row.UploadedAt);
    }


    // ── Processing-side recovery ────────────────────────────────────────────

    /// <summary>Minimal store: serves a preloaded source object or reports it missing.</summary>
    private sealed class SourceOnlyObjectStore : IObjectStore
    {
        private readonly Dictionary<string, byte[]> _sources = new(StringComparer.Ordinal);
        public void AddSource(string key, byte[] bytes) => _sources[key] = bytes;

        public Task<PresignedObjectWrite> PresignPutAsync(ObjectStoreBucket bucket, string key, string contentType, DateTimeOffset expiresAt, CancellationToken cancellationToken)
            => Task.FromResult(new PresignedObjectWrite(new Uri($"https://upload.invalid/{Uri.EscapeDataString(key)}"), new Dictionary<string, string>(), expiresAt));

        public Task<ObjectMetadata?> HeadAsync(ObjectStoreBucket bucket, string key, CancellationToken cancellationToken)
            => Task.FromResult(_sources.TryGetValue(key, out var bytes) ? new ObjectMetadata(bytes.Length, "image/jpeg") : null);

        public Task<ObjectReadResult?> GetBoundedAsync(ObjectStoreBucket bucket, string key, long maxBytes, CancellationToken cancellationToken)
        {
            if (!_sources.TryGetValue(key, out var bytes)) return Task.FromResult<ObjectReadResult?>(null);
            return Task.FromResult<ObjectReadResult?>(new ObjectReadResult(new MemoryStream(bytes, writable: false), new ObjectMetadata(bytes.Length, "image/jpeg")));
        }

        public Task PutAsync(ObjectStoreBucket bucket, string key, Stream content, string contentType, string cacheControl, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DeleteAsync(ObjectStoreBucket bucket, string key, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private static MediaStorageOptions RecoveryStorageOptions() => new()
    {
        PrivateBucket = "private",
        PublicBucket = "public",
        PublicBaseUrl = "https://media.example.test/",
        ServiceUrl = "https://account.r2.cloudflarestorage.com"
    };

    private static async Task<Media> SeedProcessingMediaAsync(ApplicationDbContext db, User owner, Business business, string sourceKey, int attempts)
    {
        var media = new Media
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id, // ck_media_owner_xor: business-owned media leaves OwnerUserId null
            OwnerUserId = null,
            UploadedByUserId = owner.Id,
            Purpose = MediaPurposes.BusinessGallery,
            SourceKey = sourceKey,
            Status = MediaStatus.Processing,
            DeclaredMimeType = "image/jpeg",
            ProcessingAttempts = attempts,
            ProcessingLeaseToken = "lease-token",
            ProcessingLeaseUntil = DateTime.UtcNow.AddMinutes(5),
            UploadedAt = DateTime.UtcNow.AddSeconds(-10)
        };
        db.Media.Add(media);
        await db.SaveChangesAsync();
        return media;
    }


    /// <summary>Answers the pipeline's public-delivery probe without real network access.</summary>
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler());

        private sealed class StubHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>())
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/webp");
                return Task.FromResult(response);
            }
        }
    }

    private static byte[] EncodeJpeg(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static MediaProcessor CreateProcessor(ApplicationDbContext db, SourceOnlyObjectStore store, IOptions<MediaStorageOptions> options)
        => new(db, store, new MediaKeyFactory(), new MediaUrlFactory(options), new MediaValidator(options), options, new StubHttpClientFactory());

    [Fact]
    public async Task ProcessingFailureWhileAttemptsRemainSchedulesABackoffRetry()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner($"owner-{Guid.NewGuid():N}@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var store = new SourceOnlyObjectStore(); // source missing → UPLOAD_MISSING failure
        var media = await SeedProcessingMediaAsync(context, owner, business, $"pending/{Guid.NewGuid():N}", attempts: 1);
        var options = Options.Create(RecoveryStorageOptions());
        var processor = CreateProcessor(context, store, options);

        Assert.False(await processor.ProcessAsync(media.Id, CancellationToken.None));

        var row = await context.Media.FirstAsync(x => x.Id == media.Id);
        Assert.Equal(MediaStatus.Uploaded, row.Status); // eligible again
        Assert.Equal("UPLOAD_MISSING", row.LastErrorCode);
        Assert.Null(row.ProcessingLeaseToken);         // lease released for the next worker
        Assert.Null(row.ProcessingLeaseUntil);
        Assert.NotNull(row.NextAttemptAt);             // backoff scheduled
        Assert.True(row.NextAttemptAt > DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(1, row.ProcessingAttempts);       // the failed attempt was already counted at claim
    }

    [Fact]
    public async Task ProcessingFailureAtTheAttemptCapMarksTheRowFailed()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner($"owner-{Guid.NewGuid():N}@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var store = new SourceOnlyObjectStore();
        var maxAttempts = RecoveryStorageOptions().Processing.MaxAttempts;
        var media = await SeedProcessingMediaAsync(context, owner, business, $"pending/{Guid.NewGuid():N}", attempts: maxAttempts);
        var processor = CreateProcessor(context, store, Options.Create(RecoveryStorageOptions()));

        Assert.False(await processor.ProcessAsync(media.Id, CancellationToken.None));

        var row = await context.Media.FirstAsync(x => x.Id == media.Id);
        Assert.Equal(MediaStatus.Failed, row.Status);
        Assert.Null(row.NextAttemptAt); // terminal: only a manual retry resurrects it
    }

    [Fact]
    public async Task ProcessingSucceedsAfterAPriorFailureAndAnchorsReadyAt()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner($"owner-{Guid.NewGuid():N}@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var store = new SourceOnlyObjectStore();
        var sourceKey = $"pending/{Guid.NewGuid():N}";
        var jpeg = EncodeJpeg(400, 300);
        store.AddSource(sourceKey, jpeg);
        var media = await SeedProcessingMediaAsync(context, owner, business, sourceKey, attempts: 2);
        var options = Options.Create(RecoveryStorageOptions());
        var processor = CreateProcessor(context, store, options);

        Assert.True(await processor.ProcessAsync(media.Id, CancellationToken.None));

        var row = await context.Media.FirstAsync(x => x.Id == media.Id);
        Assert.Equal(MediaStatus.Ready, row.Status);
        Assert.NotNull(row.ReadyAt);
        Assert.True(row.ReadyAt >= row.UploadedAt); // single-clock ordering invariant
        Assert.Null(row.ProcessingLeaseToken);
        Assert.Null(row.ProcessingLeaseUntil);
    }

    [Fact]
    public async Task ManualRetryResurrectsAFailedRowOnlyWhenAttemptsRemain()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner($"owner-{Guid.NewGuid():N}@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var service = CreateService(context, new HeadOnlyObjectStore(), owner, business);
        var maxAttempts = new MediaStorageOptions().Processing.MaxAttempts;

        var exhausted = CreatePendingMedia(owner, business, $"pending/{Guid.NewGuid():N}", null);
        exhausted.Status = MediaStatus.Failed;
        exhausted.ProcessingAttempts = maxAttempts;
        exhausted.LastErrorCode = "UPLOAD_MISSING";

        var retryable = CreatePendingMedia(owner, business, $"pending/{Guid.NewGuid():N}", null);
        retryable.Status = MediaStatus.Failed;
        retryable.ProcessingAttempts = 1;
        retryable.LastErrorCode = "UPLOAD_MISSING";

        context.Media.AddRange(exhausted, retryable);
        await context.SaveChangesAsync();

        var blocked = await service.RetryAsync(owner.Id, exhausted.Id, CancellationToken.None);
        Assert.True(blocked.Success); // API answers 200 with unchanged state, not an error
        Assert.Equal("Failed", blocked.Data!.Status);

        var resurrected = await service.RetryAsync(owner.Id, retryable.Id, CancellationToken.None);
        Assert.True(resurrected.Success);
        Assert.Equal("Uploaded", resurrected.Data!.Status);
        var row = await context.Media.FirstAsync(x => x.Id == retryable.Id);
        Assert.Null(row.LastErrorCode);
        Assert.NotNull(row.NextAttemptAt);
        Assert.True(row.NextAttemptAt <= DateTime.UtcNow);
    }

    [Fact]
    public async Task ReclaimExpiredLeaseFailsExhaustedRowsAndLeavesLiveLeasesAlone()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner($"owner-{Guid.NewGuid():N}@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var expired = CreatePendingMedia(owner, business, $"pending/{Guid.NewGuid():N}", null);
        expired.Status = MediaStatus.Processing;
        expired.ProcessingAttempts = new MediaStorageOptions().Processing.MaxAttempts;
        expired.ProcessingLeaseUntil = DateTime.UtcNow.AddMinutes(-1);
        expired.Purpose = MediaPurposes.BusinessGallery;

        var live = CreatePendingMedia(owner, business, $"pending/{Guid.NewGuid():N}", null);
        live.Status = MediaStatus.Processing;
        live.ProcessingAttempts = 1;
        live.ProcessingLeaseUntil = DateTime.UtcNow.AddMinutes(5);
        live.Purpose = MediaPurposes.BusinessGallery;

        context.Media.AddRange(expired, live);
        await context.SaveChangesAsync();

        await MediaLeaseOperations.ReclaimExpiredAsync(context, new MediaStorageOptions(), DateTime.UtcNow, CancellationToken.None);

        // ExecuteUpdateAsync writes straight to the database and bypasses the change
        // tracker, so the AddRange-tracked instances are stale from here on. Clear them
        // so the verification reads below hit the database, not the pre-reclaim snapshot.
        context.ChangeTracker.Clear();

        var reclaimed = await context.Media.FindAsync(expired.Id);
        Assert.NotNull(reclaimed);
        Assert.Equal(MediaStatus.Failed, reclaimed.Status);
        Assert.Equal("PROCESSING_LEASE_EXPIRED", reclaimed.LastErrorCode);

        var liveRow = await context.Media.FindAsync(live.Id);
        Assert.NotNull(liveRow);
        Assert.Equal(MediaStatus.Processing, liveRow.Status);
    }
}


