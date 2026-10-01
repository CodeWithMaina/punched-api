using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
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
    IOptions<MediaStorageOptions> options) : IMediaProcessor
{
    private readonly ApplicationDbContext _db = db;
    private readonly IObjectStore _store = store;
    private readonly IMediaKeyFactory _keys = keys;
    private readonly IMediaUrlFactory _urls = urls;
    private readonly IMediaValidator _validator = validator;
    private readonly MediaStorageOptions _options = options.Value;

    public async Task<bool> ProcessAsync(Guid mediaId, CancellationToken cancellationToken)
    {
        var media = await _db.Media.FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
        if (media is null) return false;

        if (media.Status is MediaStatus.Ready or MediaStatus.Failed or MediaStatus.Deleted or MediaStatus.Deleting)
            return media.Status == MediaStatus.Ready;

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

                var variants = new List<MediaVariantResponse>();
                var variantWidths = _options.VariantWidths.Count > 0 ? _options.VariantWidths : [320, 640, 1280];

                foreach (var variantWidth in variantWidths)
                {
                    var scale = variantWidth > 0 && width > variantWidth ? (double)variantWidth / width : 1d;
                    var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
                    var targetHeight = Math.Max(1, (int)Math.Round(height * scale));

                    var encodedWebp = EncodeScaledBitmap(normalized, targetWidth, targetHeight, "webp");
                    if (encodedWebp.Length == 0) throw new InvalidOperationException("DERIVATIVE_GENERATION_FAILED");

                    var webpKey = _keys.CreateDeliveryKey(media.Id, media.Purpose.Replace("-", "-"), targetWidth, "webp");
                    await _store.PutAsync(ObjectStoreBucket.PublicDelivery, webpKey, new MemoryStream(encodedWebp), "image/webp", "public, max-age=31536000", cancellationToken);
                    variants.Add(new MediaVariantResponse
                    {
                        Url = _urls.CreatePublicUrl(webpKey),
                        Width = targetWidth,
                        Height = targetHeight,
                        Format = "webp",
                        Transform = "public"
                    });

                    var encodedJpg = EncodeScaledBitmap(normalized, targetWidth, targetHeight, "jpg");
                    if (encodedJpg.Length == 0) throw new InvalidOperationException("DERIVATIVE_GENERATION_FAILED");

                    var jpgKey = _keys.CreateDeliveryKey(media.Id, media.Purpose.Replace("-", "-"), targetWidth, "jpg");
                    await _store.PutAsync(ObjectStoreBucket.PublicDelivery, jpgKey, new MemoryStream(encodedJpg), "image/jpeg", "public, max-age=31536000", cancellationToken);
                    variants.Add(new MediaVariantResponse
                    {
                        Url = _urls.CreatePublicUrl(jpgKey),
                        Width = targetWidth,
                        Height = targetHeight,
                        Format = "jpeg",
                        Transform = "public"
                    });
                }

                media.Status = MediaStatus.Ready;
                media.Visibility = MediaVisibility.Public;
                media.DetectedMimeType = validation.DetectedMimeType;
                media.SourceSizeBytes = validation.SizeBytes;
                media.Width = validation.Width;
                media.Height = validation.Height;
                media.Sha256 = validation.Sha256;
                media.VariantsJson = JsonSerializer.Serialize(variants);
                media.LastErrorCode = null;
                media.NextAttemptAt = null;
                media.ProcessingLeaseToken = null;
                media.ProcessingLeaseUntil = null;
                media.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                return true;
            }
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
            return false;
        }
    }

    private static string NormalizeErrorCode(Exception ex)
    {
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
            _ => "PROCESSING_FAILED"
        };
        return code;
    }

    private static byte[] EncodeScaledBitmap(SKBitmap source, int width, int height, string format)
    {
        if (width <= 0 || height <= 0) throw new InvalidOperationException("INVALID_IMAGE");

        using var scaled = source.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888), SKSamplingOptions.Default);
        if (scaled is null) throw new InvalidOperationException("DERIVATIVE_GENERATION_FAILED");

        using var image = SKImage.FromBitmap(scaled);
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
}

public sealed class MediaMetrics
{
    public static readonly Meter Meter = new("Punched.Media");
    private static readonly Counter<long> UploadCreated = Meter.CreateCounter<long>("media.upload.created");
    private static readonly Counter<long> UploadCompleted = Meter.CreateCounter<long>("media.upload.completed");
    private static readonly Counter<long> ProcessingStarted = Meter.CreateCounter<long>("media.processing.started");
    private static readonly Counter<long> ProcessingSucceeded = Meter.CreateCounter<long>("media.processing.succeeded");
    private static readonly Counter<long> ProcessingFailed = Meter.CreateCounter<long>("media.processing.failed");
    private static readonly Counter<long> ProcessingRetry = Meter.CreateCounter<long>("media.processing.retry");
    private static readonly Counter<long> CleanupDeleted = Meter.CreateCounter<long>("media.cleanup.deleted");
    private static readonly Counter<long> CleanupFailed = Meter.CreateCounter<long>("media.cleanup.failed");
    private static readonly Histogram<double> ProcessingDuration = Meter.CreateHistogram<double>("media.processing.duration.ms");

    public static void RecordUploadCreated() => UploadCreated.Add(1);
    public static void RecordUploadCompleted() => UploadCompleted.Add(1);
    public static void RecordProcessingStarted() => ProcessingStarted.Add(1);
    public static void RecordProcessingSucceeded() => ProcessingSucceeded.Add(1);
    public static void RecordProcessingFailed() => ProcessingFailed.Add(1);
    public static void RecordProcessingRetry() => ProcessingRetry.Add(1);
    public static void RecordCleanupDeleted() => CleanupDeleted.Add(1);
    public static void RecordCleanupFailed() => CleanupFailed.Add(1);
    public static void RecordProcessingDuration(TimeSpan elapsed) => ProcessingDuration.Record(elapsed.TotalMilliseconds);
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
                    var claimed = await db.Media
                        .Where(x => x.Id == media.Id &&
                                    (x.Status == MediaStatus.Uploaded || x.Status == MediaStatus.Processing) &&
                                    x.ProcessingAttempts < _options.Processing.MaxAttempts &&
                                    (x.ProcessingLeaseUntil == null || x.ProcessingLeaseUntil <= now))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(x => x.Status, MediaStatus.Processing)
                            .SetProperty(x => x.ProcessingLeaseToken, leaseToken)
                            .SetProperty(x => x.ProcessingLeaseUntil, now.AddMinutes(_options.Processing.LeaseMinutes))
                            .SetProperty(x => x.ProcessingAttempts, x => x.ProcessingAttempts + 1)
                            .SetProperty(x => x.UpdatedAt, now), stoppingToken);

                    if (claimed == 0) continue;
                    MediaMetrics.RecordProcessingStarted();
                    var started = DateTime.UtcNow;
                    var success = await processor.ProcessAsync(media.Id, stoppingToken);
                    if (success)
                    {
                        MediaMetrics.RecordProcessingSucceeded();
                    }
                    else if (media.ProcessingAttempts >= _options.Processing.MaxAttempts)
                    {
                        MediaMetrics.RecordProcessingFailed();
                    }
                    else
                    {
                        MediaMetrics.RecordProcessingRetry();
                    }
                    MediaMetrics.RecordProcessingDuration(DateTime.UtcNow - started);
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
                var pendingCutoff = DateTime.UtcNow.AddMinutes(-_options.PendingRowExpirationMinutes);
                await db.Media
                    .Where(x => x.Status == MediaStatus.Processing &&
                                x.ProcessingAttempts >= _options.Processing.MaxAttempts &&
                                x.ProcessingLeaseUntil != null && x.ProcessingLeaseUntil <= DateTime.UtcNow)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Status, MediaStatus.Failed)
                        .SetProperty(x => x.LastErrorCode, "PROCESSING_LEASE_EXPIRED")
                        .SetProperty(x => x.ProcessingLeaseToken, (string?)null)
                        .SetProperty(x => x.ProcessingLeaseUntil, (DateTime?)null)
                        .SetProperty(x => x.NextAttemptAt, (DateTime?)null)
                        .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), stoppingToken);
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
                    MediaMetrics.RecordCleanupDeleted();
                }

                var purgePending = await db.Media
                    .Where(x => x.DeliveryPurgeStatus == DeliveryPurgeStatus.Pending && x.DeliveryPurgeNextAttemptAt <= DateTime.UtcNow)
                    .Take(_options.CleanupBatchSize)
                    .ToListAsync(stoppingToken);

                foreach (var media in purgePending)
                {
                    try
                    {
                        var variants = DeserializeVariants(media.VariantsJson);
                        var keys = variants
                            .Select(x => urls.TryGetDeliveryKey(x.Url))
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Select(x => x!)
                            .Distinct()
                            .ToArray();

                        foreach (var key in keys)
                        {
                            await store.DeleteAsync(ObjectStoreBucket.PublicDelivery, key, stoppingToken);
                        }

                        media.DeliveryPurgeStatus = DeliveryPurgeStatus.Completed;
                        media.DeliveryPurgedAt = DateTime.UtcNow;
                        media.DeliveryPurgeErrorCode = null;
                        media.DeliveryPurgeAttempts++;
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
                        media.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(stoppingToken);
                        MediaMetrics.RecordCleanupFailed();
                    }
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
}
