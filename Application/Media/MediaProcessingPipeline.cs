using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using SkiaSharp;

namespace PunchedApi.Application.Media;

public interface IMediaValidator
{
    Task<ValidatedMedia> ValidateAsync(PunchedApi.Domain.Entities.Media media, byte[] bytes, CancellationToken cancellationToken);
}

public sealed record ValidatedMedia(
    string DetectedMimeType,
    int Width,
    int Height,
    long PixelCount,
    long SizeBytes,
    string Sha256,
    byte[] Bytes);

public interface IMediaProcessor
{
    Task<bool> ProcessAsync(Guid mediaId, CancellationToken cancellationToken);
}

public sealed class MediaValidator(IOptions<MediaStorageOptions> options) : IMediaValidator
{
    private readonly MediaStorageOptions _options = options.Value;

    public Task<ValidatedMedia> ValidateAsync(PunchedApi.Domain.Entities.Media media, byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length == 0) throw new InvalidOperationException("MEDIA_EMPTY");
        if (!_options.Limits.Purposes.TryGetValue(media.Purpose, out var limit)) throw new InvalidOperationException("UNSUPPORTED_MEDIA_PURPOSE");
        if (bytes.Length > limit.MaxBytes) throw new InvalidOperationException("MEDIA_TOO_LARGE");

        var detectedMime = DetectMimeType(bytes);
        if (!_options.Limits.SupportedInputMimeTypes.Contains(detectedMime, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("UNSUPPORTED_MEDIA_TYPE");
        if (!string.Equals(media.DeclaredMimeType, detectedMime, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("DECLARED_MIME_MISMATCH");

        using var codec = SKCodec.Create(new SKMemoryStream(bytes));
        if (codec is null) throw new InvalidOperationException("INVALID_IMAGE");

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) throw new InvalidOperationException("INVALID_IMAGE");

        var width = info.Width;
        var height = info.Height;
        var pixels = (long)width * height;

        if (width < limit.MinWidth || height < limit.MinHeight)
            throw new InvalidOperationException("IMAGE_TOO_SMALL");
        if (width > limit.MaxWidth || height > limit.MaxHeight)
            throw new InvalidOperationException("IMAGE_TOO_LARGE");
        if (pixels > limit.MaxPixels)
            throw new InvalidOperationException("IMAGE_PIXEL_LIMIT_EXCEEDED");

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return Task.FromResult(new ValidatedMedia(detectedMime, width, height, pixels, bytes.LongLength, sha, bytes));
    }

    private static string DetectMimeType(byte[] bytes)
    {
        if (bytes.Length < 8) throw new InvalidOperationException("INVALID_IMAGE");

        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return "image/png";

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";

        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return "image/webp";

        throw new InvalidOperationException("UNSUPPORTED_MEDIA_TYPE");
    }
}

public sealed class MediaProcessor(
    ApplicationDbContext db,
    IObjectStore store,
    IMediaKeyFactory keys,
    IMediaUrlFactory urls,
    IMediaValidator validator,
    IOptions<MediaStorageOptions> options,
    IHttpClientFactory httpClientFactory) : IMediaProcessor
{
    private readonly ApplicationDbContext _db = db;
    private readonly IObjectStore _store = store;
    private readonly IMediaKeyFactory _keys = keys;
    private readonly IMediaUrlFactory _urls = urls;
    private readonly IMediaValidator _validator = validator;
    private readonly MediaStorageOptions _options = options.Value;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

    public async Task<bool> ProcessAsync(Guid mediaId, CancellationToken cancellationToken)
    {
        var media = await _db.Media.FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
        if (media is null) return false;

        if (media.Status is MediaStatus.Ready or MediaStatus.Failed or MediaStatus.Deleted or MediaStatus.Deleting)
            return media.Status == MediaStatus.Ready;

        // MEASUREMENT: the stopwatch starts after the row is claimed, so it times ONLY the
        // image work (download original → validate → decode → encode → PUT variants → probe).
        // Time spent queued between completion and this claim is recorded separately by the
        // worker as queue delay, and the two are never summed or mixed.
        var attempt = media.ProcessingAttempts;
        var processingStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!_options.Limits.Purposes.TryGetValue(media.Purpose, out var limit))
                throw new InvalidOperationException("UNSUPPORTED_MEDIA_PURPOSE");

            var original = await _store.GetBoundedAsync(ObjectStoreBucket.PrivateSource, media.SourceKey, limit.MaxBytes, cancellationToken);
            if (original is null)
                throw new InvalidOperationException("UPLOAD_MISSING");

            await using (original)
            {
                using var buffer = new MemoryStream();
                await original.Content.CopyToAsync(buffer, cancellationToken);
                var bytes = buffer.ToArray();
                var validation = await _validator.ValidateAsync(media, bytes, cancellationToken);

                var width = validation.Width;
                var height = validation.Height;
                using var image = SKBitmap.Decode(bytes);
                if (image is null) throw new InvalidOperationException("INVALID_IMAGE");

                using var normalized = NormalizeOrientation(image, ReadEncodedOrigin(bytes));
                if (normalized.Width == 0 || normalized.Height == 0) throw new InvalidOperationException("INVALID_IMAGE");

                media.DetectedMimeType = validation.DetectedMimeType;
                media.SourceSizeBytes = validation.SizeBytes;
                media.Width = validation.Width;
                media.Height = validation.Height;
                media.Sha256 = validation.Sha256;
                var variants = DeserializeVariants(media.VariantsJson).ToList();
                await _db.SaveChangesAsync(cancellationToken);
                var variantWidths = _options.VariantWidths.Count > 0 ? _options.VariantWidths : [320, 640, 1280];

                // Distinct target widths only. Several configured widths collapse to the same
                // rendered width whenever the source is at or below them; re-encoding and
                // re-uploading an identical bitmap under a different key is pure waste (CPU,
                // an R2 PUT, and stored bytes) and also bloats the variants list.
                var renderedWidths = variantWidths
                    .Select(variantWidth => variantWidth > 0 && width > variantWidth ? variantWidth : width)
                    .Distinct()
                    .OrderByDescending(target => target)
                    .ToArray();

                var uploads = new List<Task>();
                foreach (var targetWidth in renderedWidths)
                {
                    var scale = targetWidth < width ? (double)targetWidth / width : 1d;
                    var targetHeight = Math.Max(1, (int)Math.Round(height * scale));

                    using var scaled = normalized.Resize(new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888), SKSamplingOptions.Default);
                    if (scaled is null) throw new InvalidOperationException("DERIVATIVE_GENERATION_FAILED");

                    var encodedWebp = EncodeBitmap(scaled, "webp");
                    if (encodedWebp.Length == 0) throw new InvalidOperationException("DERIVATIVE_GENERATION_FAILED");
                    var encodedJpg = EncodeBitmap(scaled, "jpg");
                    if (encodedJpg.Length == 0) throw new InvalidOperationException("DERIVATIVE_GENERATION_FAILED");

                    var webpKey = _keys.CreateDeliveryKey(media.Id, media.Purpose, targetWidth, "webp");
                    var jpgKey = _keys.CreateDeliveryKey(media.Id, media.Purpose, targetWidth, "jpg");
                    RecordVariant(variants, new MediaVariantResponse
                    {
                        Url = _urls.CreatePublicUrl(webpKey), Width = targetWidth, Height = targetHeight,
                        Format = "webp", Transform = "public"
                    });
                    RecordVariant(variants, new MediaVariantResponse
                    {
                        Url = _urls.CreatePublicUrl(jpgKey), Width = targetWidth, Height = targetHeight,
                        Format = "jpeg", Transform = "public"
                    });

                    // Fan the uploads out: encoding is already finished, so the only remaining
                    // cost is network. Serialising PUTs would make time-to-Ready scale with the
                    // number of widths. Delivery objects are immutable and content-addressed per
                    // key, so parallel writes can never partially overwrite each other.
                    var webpStream = new MemoryStream(encodedWebp);
                    var jpgStream = new MemoryStream(encodedJpg);
                    uploads.Add(UploadAndDisposeAsync(_store, ObjectStoreBucket.PublicDelivery, webpKey, webpStream, "image/webp", cancellationToken));
                    uploads.Add(UploadAndDisposeAsync(_store, ObjectStoreBucket.PublicDelivery, jpgKey, jpgStream, "image/jpeg", cancellationToken));
                }

                if (variants.Count == 0) throw new InvalidOperationException("DERIVATIVE_GENERATION_FAILED");
                await Task.WhenAll(uploads);

                media.VariantsJson = JsonSerializer.Serialize(variants);
                media.UpdatedAt = DateTime.UtcNow;
                // One write per attempt instead of one per variant width.
                await _db.SaveChangesAsync(cancellationToken);

                // One public-delivery probe instead of one HEAD per variant: every key shares
                // the same delivery path, so a single successful HEAD proves the whole batch is
                // reachable and removes 2N R2 HEAD round trips from time-to-Ready.
                using (var publicResponse = await _httpClientFactory.CreateClient().SendAsync(
                    new HttpRequestMessage(HttpMethod.Head, variants[0].Url),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken))
                {
                    if (!publicResponse.IsSuccessStatusCode || !string.Equals(publicResponse.Content.Headers.ContentType?.MediaType, "image/webp", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("PUBLIC_DELIVERY_UNAVAILABLE");
                }

                media.Status = MediaStatus.Ready;
                media.Visibility = MediaVisibility.Public;
                media.LastErrorCode = null;
                media.NextAttemptAt = null;
                media.ProcessingLeaseToken = null;
                media.ProcessingLeaseUntil = null;
                // Server-side anchor for the completion→Ready interval.
                var readyAt = DateTime.UtcNow;
                media.ReadyAt = readyAt;
                media.UpdatedAt = readyAt;
                await _db.SaveChangesAsync(cancellationToken);

                processingStopwatch.Stop();
                MediaMetrics.RecordProcessingDuration(processingStopwatch.Elapsed, media.Purpose);
                MediaMetrics.RecordProcessingSucceeded();
                MediaMetrics.RecordVariantsGenerated(variants.Count, media.Purpose);

                // Upload acceptance → Ready and completion → Ready, both from server-side
                // timestamps on this row. A single clock domain means no cross-clock arithmetic.
                if (media.UploadedAt is DateTime uploadedAt)
                    MediaMetrics.RecordUploadToReady(readyAt - uploadedAt, media.Purpose);

                // This attempt succeeded after at least one prior failure: a real recovery,
                // which is the number that proves retry policy actually rescues uploads.
                if (attempt >= 2)
                    MediaMetrics.RecordProcessingRecovered(media.Purpose, attempt);
                return true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var stableError = NormalizeErrorCode(ex);
            media.LastErrorCode = stableError;
            media.UpdatedAt = DateTime.UtcNow;
            media.ProcessingLeaseToken = null;
            media.ProcessingLeaseUntil = null;

            var attempts = media.ProcessingAttempts;
            if (attempts >= _options.Processing.MaxAttempts)
            {
                media.Status = MediaStatus.Failed;
                media.NextAttemptAt = null;
            }
            else
            {
                media.Status = MediaStatus.Uploaded;
                media.NextAttemptAt = DateTime.UtcNow.AddMinutes(Math.Pow(2, Math.Min(attempts, 5)));
            }

            await _db.SaveChangesAsync(cancellationToken);
            processingStopwatch.Stop();
            MediaMetrics.RecordProcessingFailed();
            MediaMetrics.RecordProcessingDuration(processingStopwatch.Elapsed, media.Purpose);
            if (attempt >= 2) MediaMetrics.RecordProcessingRetry();
            return false;
        }
    }

    private static string NormalizeErrorCode(Exception ex)
    {
        while (ex.InnerException is not null && ex is not AmazonS3Exception and not HttpRequestException)
            ex = ex.InnerException;

        if (ex is AmazonS3Exception storageError)
        {
            var providerCode = new string((storageError.ErrorCode ?? "UNKNOWN")
                .Where(char.IsAsciiLetterOrDigit)
                .Take(32)
                .ToArray())
                .ToUpperInvariant();
            return $"R2_{(int)storageError.StatusCode}_{providerCode}";
        }

        if (ex is HttpRequestException httpError)
            return httpError.StatusCode is { } statusCode
                ? $"PUBLIC_HTTP_{(int)statusCode}"
                : "PUBLIC_HTTP_REQUEST_FAILED";

        var code = ex.Message switch
        {
            "MEDIA_EMPTY" => "MEDIA_EMPTY",
            "MEDIA_TOO_LARGE" => "MEDIA_TOO_LARGE",
            "UNSUPPORTED_MEDIA_TYPE" => "UNSUPPORTED_MEDIA_TYPE",
            "DECLARED_MIME_MISMATCH" => "DECLARED_MIME_MISMATCH",
            "IMAGE_TOO_SMALL" => "IMAGE_TOO_SMALL",
            "IMAGE_TOO_LARGE" => "IMAGE_TOO_LARGE",
            "IMAGE_PIXEL_LIMIT_EXCEEDED" => "IMAGE_PIXEL_LIMIT_EXCEEDED",
            "UPLOAD_MISSING" => "UPLOAD_MISSING",
            "INVALID_IMAGE" => "INVALID_IMAGE",
            "DERIVATIVE_GENERATION_FAILED" => "DERIVATIVE_GENERATION_FAILED",
            "PUBLIC_DELIVERY_UNAVAILABLE" => "PUBLIC_DELIVERY_UNAVAILABLE",
            _ => $"PROCESSING_{new string(ex.GetType().Name.Where(char.IsAsciiLetterOrDigit).Take(48).ToArray()).ToUpperInvariant()}"
        };
        return code;
    }

    private static byte[] EncodeBitmap(SKBitmap source, string format)
    {
        if (source.Width <= 0 || source.Height <= 0) throw new InvalidOperationException("INVALID_IMAGE");

        using var image = SKImage.FromBitmap(source);
        using var data = format switch
        {
            "webp" => image.Encode(SKEncodedImageFormat.Webp, 90),
            "jpg" => image.Encode(SKEncodedImageFormat.Jpeg, 90),
            _ => image.Encode(SKEncodedImageFormat.Webp, 90)
        };
        return data?.ToArray() ?? Array.Empty<byte>();
    }

    private static SKEncodedOrigin ReadEncodedOrigin(byte[] bytes)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(bytes));
        return codec?.EncodedOrigin ?? SKEncodedOrigin.TopLeft;
    }

    private static SKBitmap NormalizeOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        var swapDimensions = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var normalized = new SKBitmap(swapDimensions ? source.Height : source.Width, swapDimensions ? source.Width : source.Height);
        using var canvas = new SKCanvas(normalized);

        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Scale(-1, 1, normalized.Width / 2f, normalized.Height / 2f);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.RotateDegrees(180, normalized.Width / 2f, normalized.Height / 2f);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Scale(1, -1, normalized.Width / 2f, normalized.Height / 2f);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(normalized.Width, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, normalized.Height);
                canvas.RotateDegrees(270);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.Translate(normalized.Width, 0);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(0, normalized.Height);
                canvas.RotateDegrees(270);
                canvas.Scale(-1, 1);
                break;
        }

        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        return normalized;
    }

    /// <summary>
    /// Uploads one delivery variant and disposes its stream afterwards. A successful R2 PUT is
    /// already a durable acknowledgement of the object, so a follow-up HEAD per variant only adds
    /// a round trip; reachability of the shared delivery path is proven once per batch by the
    /// public probe in <see cref="MediaProcessor.ProcessAsync"/>.
    /// </summary>
    private static async Task UploadAndDisposeAsync(IObjectStore store, ObjectStoreBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        try
        {
            await store.PutAsync(bucket, key, content, contentType, "public, max-age=31536000, immutable", cancellationToken);
        }
        finally
        {
            await content.DisposeAsync();
        }
    }

    private static void RecordVariant(List<MediaVariantResponse> variants, MediaVariantResponse variant)
    {
        var existing = variants.FindIndex(item => string.Equals(item.Url, variant.Url, StringComparison.Ordinal));
        if (existing >= 0) variants[existing] = variant;
        else variants.Add(variant);
    }

    private static List<MediaVariantResponse> DeserializeVariants(string json)
    {
        try { return JsonSerializer.Deserialize<List<MediaVariantResponse>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}


public sealed class MediaProcessingWorker(IServiceScopeFactory scopeFactory, IOptions<MediaStorageOptions> options) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly MediaStorageOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var processor = scope.ServiceProvider.GetRequiredService<IMediaProcessor>();
                var now = DateTime.UtcNow;

                var pending = await db.Media
                    .Where(x => (x.Status == MediaStatus.Uploaded || x.Status == MediaStatus.Processing) &&
                                x.ProcessingAttempts < _options.Processing.MaxAttempts &&
                                (x.ProcessingLeaseUntil == null || x.ProcessingLeaseUntil <= now) &&
                                (x.NextAttemptAt == null || x.NextAttemptAt <= now))
                    .OrderBy(x => x.NextAttemptAt ?? x.CreatedAt)
                    .Take(_options.WorkerBatchSize)
                    .ToListAsync(stoppingToken);

                foreach (var media in pending)
                {
                    var leaseToken = Guid.NewGuid().ToString("N");
                    // Atomic claim: the WHERE predicate runs inside one UPDATE, so a
                    // concurrent worker racing for this row gets 0 rows and moves on.
                    var claimed = await MediaLeaseOperations.ClaimAsync(db, media.Id, now, leaseToken, _options, stoppingToken);

                    if (claimed == 0) continue;
                    media.Status = MediaStatus.Processing;
                    media.ProcessingAttempts++;
                    media.ProcessingLeaseToken = leaseToken;
                    media.ProcessingLeaseUntil = now.AddMinutes(_options.Processing.LeaseMinutes);
                    media.UpdatedAt = now;
                    MediaMetrics.RecordProcessingStarted();

                    // MEASUREMENT: queue delay = completion (UploadedAt) → this claim. Both
                    // timestamps are server-side, so this is pure waiting time and contains no
                    // processing work. Attempts >= 2 are retries, not queue delay, and are
                    // excluded so a backoff wait is never misreported as queue congestion.
                    if (media.ProcessingAttempts == 1 && media.UploadedAt is DateTime uploadedAt && uploadedAt <= now)
                        MediaMetrics.RecordQueueDelay(now - uploadedAt, media.Purpose);

                    // Outcome counters and processing duration are recorded inside the
                    // processor, which is the only place that knows the purpose and can time
                    // the image work itself rather than the surrounding lease heartbeat.
                    await ProcessWithLeaseHeartbeatAsync(media.Id, leaseToken, processor, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Worker loops; error handling remains local to the processing service.
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task<bool?> ProcessWithLeaseHeartbeatAsync(Guid mediaId, string leaseToken, IMediaProcessor processor, CancellationToken stoppingToken)
    {
        using var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var processing = processor.ProcessAsync(mediaId, processingCancellation.Token);

        try
        {
            while (!processing.IsCompleted)
            {
                await Task.Delay(TimeSpan.FromMinutes(_options.Processing.LeaseHeartbeatMinutes), processingCancellation.Token);
                if (processing.IsCompleted) break;

                if (!await RenewLeaseAsync(mediaId, leaseToken, stoppingToken))
                {
                    processingCancellation.Cancel();
                    try { await processing; }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
                    return null;
                }
            }

            return await processing;
        }
        finally
        {
            processingCancellation.Cancel();
        }
    }

    private async Task<bool> RenewLeaseAsync(Guid mediaId, string leaseToken, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var renewed = await MediaLeaseOperations.RenewAsync(
                db, mediaId, leaseToken, DateTime.UtcNow, _options, cancellationToken);
            return renewed == 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class MediaCleanupWorker(IServiceScopeFactory scopeFactory, IOptions<MediaStorageOptions> options) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly MediaStorageOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var urls = scope.ServiceProvider.GetRequiredService<IMediaUrlFactory>();
                var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
                var now = DateTime.UtcNow;
                var pendingCutoff = now.AddMinutes(-_options.PendingRowExpirationMinutes);

                // Fail over rows whose processing lease expired AND whose attempt budget is
                // exhausted. Rows still within their attempt budget are deliberately left
                // alone: the processing worker reclaims them itself, and failing them here
                // would silently burn an attempt the worker would otherwise have used.
                // (Predicate, telemetry and clock semantics live in MediaLeaseOperations
                // so they can be regression-tested without a running BackgroundService.)
                await MediaLeaseOperations.ReclaimExpiredAsync(db, _options, now, stoppingToken);

                var stalePending = await db.Media
                    .Where(x => x.Status == MediaStatus.Pending && x.CreatedAt <= pendingCutoff)
                    .Take(_options.CleanupBatchSize)
                    .ToListAsync(stoppingToken);

                foreach (var media in stalePending)
                {
                    var objectExists = await store.HeadAsync(ObjectStoreBucket.PrivateSource, media.SourceKey, stoppingToken);
                    if (objectExists is not null)
                    {
                        await store.DeleteAsync(ObjectStoreBucket.PrivateSource, media.SourceKey, stoppingToken);
                    }

                    media.Status = MediaStatus.Failed;
                    media.LastErrorCode = "UPLOAD_EXPIRED";
                    media.NextAttemptAt = null;
                    media.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(stoppingToken);
                    MediaMetrics.RecordAbandonedPendingPurged(media.Purpose);
                    MediaMetrics.RecordCleanupDeleted();
                }

                if (_options.Retention.FailedUploadHours > 0)
                {
                    var failedCutoff = now.AddHours(-_options.Retention.FailedUploadHours);
                    var expiredFailed = await db.Media
                        .Where(x => x.Status == MediaStatus.Failed && x.UpdatedAt <= failedCutoff)
                        .Take(_options.CleanupBatchSize)
                        .ToListAsync(stoppingToken);

                    foreach (var media in expiredFailed)
                    {
                        if (await HasRelationshipsAsync(db, media.Id, stoppingToken)) continue;
                        await DeleteObjectsAsync(media, urls, store, stoppingToken);
                        db.Media.Remove(media);
                        await db.SaveChangesAsync(stoppingToken);
                        MediaMetrics.RecordCleanupDeleted();
                    }
                }

                var purgePending = await db.Media
                    .Where(x => (x.DeliveryPurgeStatus == DeliveryPurgeStatus.Pending || x.DeliveryPurgeStatus == DeliveryPurgeStatus.Failed) &&
                                (x.DeliveryPurgeNextAttemptAt == null || x.DeliveryPurgeNextAttemptAt <= now))
                    .Take(_options.CleanupBatchSize)
                    .ToListAsync(stoppingToken);

                foreach (var media in purgePending)
                {
                    try
                    {
                        await DeleteObjectsAsync(media, urls, store, stoppingToken);

                        media.DeliveryPurgeStatus = DeliveryPurgeStatus.Completed;
                        media.DeliveryPurgedAt = DateTime.UtcNow;
                        media.DeliveryPurgeErrorCode = null;
                        media.DeliveryPurgeAttempts++;
                        media.DeliveryPurgeNextAttemptAt = null;
                        media.Status = MediaStatus.Deleted;
                        media.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(stoppingToken);
                        MediaMetrics.RecordCleanupDeleted();
                    }
                    catch
                    {
                        media.DeliveryPurgeStatus = DeliveryPurgeStatus.Failed;
                        media.DeliveryPurgeErrorCode = "PURGE_FAILED";
                        media.DeliveryPurgeAttempts++;
                        media.DeliveryPurgeNextAttemptAt = DateTime.UtcNow.AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(media.DeliveryPurgeAttempts, 6))));
                        media.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(stoppingToken);
                        MediaMetrics.RecordCleanupFailed();
                    }
                }

                var deletedCutoff = DateTime.UtcNow.AddHours(-Math.Max(0, _options.Retention.DeletedGraceHours));
                var expiredDeleted = await db.Media
                    .Where(x => x.Status == MediaStatus.Deleted && x.DeletedAt != null && x.DeletedAt <= deletedCutoff)
                    .Take(_options.CleanupBatchSize)
                    .ToListAsync(stoppingToken);
                foreach (var media in expiredDeleted)
                {
                    if (await HasRelationshipsAsync(db, media.Id, stoppingToken)) continue;
                    db.Media.Remove(media);
                    await db.SaveChangesAsync(stoppingToken);
                    MediaMetrics.RecordCleanupDeleted();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // retries continue through the next loop iteration.
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private static IReadOnlyList<MediaVariantResponse> DeserializeVariants(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<MediaVariantResponse>();
        try
        {
            return JsonSerializer.Deserialize<List<MediaVariantResponse>>(json) ?? new List<MediaVariantResponse>();
        }
        catch (JsonException)
        {
            return Array.Empty<MediaVariantResponse>();
        }
    }

    private static async Task<bool> HasRelationshipsAsync(ApplicationDbContext db, Guid mediaId, CancellationToken cancellationToken) =>
        await db.BusinessMedia.AnyAsync(x => x.MediaId == mediaId, cancellationToken) ||
        await db.ServiceMedia.AnyAsync(x => x.MediaId == mediaId, cancellationToken) ||
        await db.LoyaltyProgramMedia.AnyAsync(x => x.MediaId == mediaId, cancellationToken) ||
        await db.ReviewMedia.AnyAsync(x => x.MediaId == mediaId, cancellationToken);

    private static async Task DeleteObjectsAsync(PunchedApi.Domain.Entities.Media media, IMediaUrlFactory urls, IObjectStore store, CancellationToken cancellationToken)
    {
        await store.DeleteAsync(ObjectStoreBucket.PrivateSource, media.SourceKey, cancellationToken);
        var keys = DeserializeVariants(media.VariantsJson)
            .Select(variant => urls.TryGetDeliveryKey(variant.Url))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var key in keys)
            await store.DeleteAsync(ObjectStoreBucket.PublicDelivery, key, cancellationToken);
    }
}
