namespace PunchedApi.Application.Media;

using PunchedApi.Domain.Entities;

public sealed class MediaStorageOptions
{
    public const string SectionName = "MediaStorage";

    /// <summary>Object-store provider. <c>r2</c> (production) or <c>local</c> (dev/CI filesystem).</summary>
    public const string ProviderR2 = "r2";
    public const string ProviderLocal = "local";

    public string Provider { get; set; } = "r2";
    public string AccountId { get; set; } = string.Empty;
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;
    public string PrivateBucket { get; set; } = string.Empty;
    public string PublicBucket { get; set; } = string.Empty;
    public string PublicBaseUrl { get; set; } = string.Empty;
    public string ServiceUrl { get; set; } = string.Empty;
    public string Region { get; set; } = "auto";
    public int PresignMinutes { get; set; } = 5;
    public int PendingRowExpirationMinutes { get; set; } = 15;
    public int PendingObjectLifecycleHours { get; set; } = 24;

    /// <summary>
    /// Ceiling on the post-processing reachability probe of the first delivered
    /// variant. The worker processes its batch sequentially, so an unbounded probe
    /// (the default <see cref="HttpClient"/> timeout is 100 seconds) lets one slow
    /// or unreachable delivery URL stall every other queued upload behind it.
    /// A bounded probe turns that stall into a normal, retryable failure.
    /// </summary>
    public int DeliveryProbeTimeoutSeconds { get; set; } = 15;
    public int CleanupBatchSize { get; set; } = 25;
    public int WorkerBatchSize { get; set; } = 4;

    /// <summary>
    /// How many claimed rows the processing worker handles concurrently. 0 (the
    /// default) resolves to <c>min(WorkerBatchSize, ProcessorCount)</c>. The batch
    /// used to be drained strictly one-at-a-time, so a single slow encode — or one
    /// slow database round trip — delayed every other queued upload behind it.
    /// </summary>
    public int WorkerParallelism { get; set; }
    public List<int> VariantWidths { get; set; } = [320, 640, 1280];

    /// <summary>
    /// Filesystem root for the <c>local</c> provider. Private source objects and
    /// public delivery objects live in separate sub-directories underneath it, so
    /// a delivery read can never reach a source object. Never web-served: payloads
    /// are only reachable through the media controller.
    /// </summary>
    public string StorageRoot { get; set; } = "App_Data/media";

    /// <summary>
    /// Absolute base URL of this API, used by the <c>local</c> provider to build the
    /// signed upload URL and the public delivery URL. Must point at the running API
    /// (e.g. <c>http://localhost:5091</c>) so the browser and the processing worker
    /// resolve the same endpoints.
    /// </summary>
    public string LocalBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional HMAC key for <c>local</c> upload grants. When empty a random
    /// per-process key is generated, which is safe (a restart simply invalidates
    /// outstanding grants) and avoids a weak committed default.
    /// </summary>
    public string LocalSigningKey { get; set; } = string.Empty;

    /// <summary>True when the filesystem provider is configured.</summary>
    public bool UsesLocalStore => string.Equals(Provider, ProviderLocal, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maximum number of business-gallery attachments per business. Enforced atomically on
    /// attach (business-scoped). A value &lt;= 0 disables the limit (unlimited). This is a
    /// product default, not a subscription entitlement: it is a single global configuration
    /// value with no per-tier differentiation.
    /// </summary>
    public int MaxGalleryImages { get; set; } = 30;


    /// <summary>
    /// Maximum number of images attached to one service. Enforced atomically on
    /// attach and on the ordered image-set write, so a service can never end up
    /// with more images than the storefront and gallery layouts expect. A value
    /// &lt;= 0 disables the limit (unlimited). Like <see cref="MaxGalleryImages"/>
    /// this is a single global product default, not a per-tier entitlement.
    /// </summary>
    public int MaxServiceImages { get; set; } = 3;
    public MediaProcessingOptions Processing { get; set; } = new();
    public MediaRetentionOptions Retention { get; set; } = new();
    public MediaLimitOptions Limits { get; set; } = MediaLimitOptions.CreateDefaults();

    public void Validate(bool requireR2)
    {
        if (PresignMinutes is < 1 or > 7) throw new InvalidOperationException("MediaStorage:PresignMinutes must be between 1 and 7.");
        if (DeliveryProbeTimeoutSeconds is < 1 or > 120) throw new InvalidOperationException("MediaStorage:DeliveryProbeTimeoutSeconds must be between 1 and 120.");
        if (PendingRowExpirationMinutes < 1 || PendingObjectLifecycleHours < 1) throw new InvalidOperationException("Media pending expiry must be positive.");
        if (Processing.LeaseMinutes < 1 || Processing.LeaseHeartbeatMinutes < 1 || Processing.LeaseHeartbeatMinutes >= Processing.LeaseMinutes) throw new InvalidOperationException("Media lease heartbeat must be positive and shorter than the lease.");
        if (Processing.MaxAttempts < 1) throw new InvalidOperationException("Media processing attempts must be positive.");
        if (MaxGalleryImages < 0) throw new InvalidOperationException("MediaStorage:MaxGalleryImages must be zero or greater (0 = unlimited).");
        if (MaxServiceImages < 0) throw new InvalidOperationException("MediaStorage:MaxServiceImages must be zero or greater (0 = unlimited).");
        if (WorkerParallelism < 0) throw new InvalidOperationException("MediaStorage:WorkerParallelism must be zero or greater (0 = derive from the batch size).");
        if (WorkerBatchSize < 1) throw new InvalidOperationException("MediaStorage:WorkerBatchSize must be at least 1.");
        if (Limits.SupportedInputMimeTypes.Length == 0) throw new InvalidOperationException("At least one input MIME type is required.");
        foreach (var purpose in Limits.Purposes.Values)
            if (purpose.MaxBytes < 1 || purpose.MaxWidth < 1 || purpose.MaxHeight < 1 || purpose.MaxPixels < 1)
                throw new InvalidOperationException("Media purpose limits must be positive.");

        // The filesystem provider is a development/CI affordance: it has no buckets
        // and no cloud credentials, but it does need a base URL the browser and the
        // processing worker can both reach, and it must not be selected in production.
        if (UsesLocalStore)
        {
            if (string.IsNullOrWhiteSpace(LocalBaseUrl) || !Uri.TryCreate(LocalBaseUrl, UriKind.Absolute, out var localUri)
                || (localUri.Scheme != Uri.UriSchemeHttp && localUri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("MediaStorage:LocalBaseUrl must be an absolute http(s) URL when Provider is 'local'.");
            if (string.IsNullOrWhiteSpace(PublicBaseUrl))
                throw new InvalidOperationException("MediaStorage:PublicBaseUrl is required (point it at {LocalBaseUrl}/v1/media/delivery when Provider is 'local').");
            return;
        }

        if (!requireR2) return;
        if (!string.Equals(Provider, ProviderR2, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("MediaStorage:Provider must be r2 or local.");
        if (string.IsNullOrWhiteSpace(AccountId) || string.IsNullOrWhiteSpace(AccessKeyId) || string.IsNullOrWhiteSpace(SecretAccessKey)) throw new InvalidOperationException("R2 credentials must be injected at runtime.");
        if (string.IsNullOrWhiteSpace(PrivateBucket) || string.IsNullOrWhiteSpace(PublicBucket) || string.IsNullOrWhiteSpace(PublicBaseUrl) || string.IsNullOrWhiteSpace(ServiceUrl)) throw new InvalidOperationException("R2 buckets, service URL, and public base URL are required.");
        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var publicUri) || publicUri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("MediaStorage:PublicBaseUrl must be HTTPS.");
        if (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var serviceUri) || serviceUri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("MediaStorage:ServiceUrl must be HTTPS.");

        // The committed configuration ships placeholders so a developer cannot
        // accidentally point a local run at a real bucket. A placeholder that
        // reaches production is worse than a missing value: the API would report
        // itself healthy while every delivered image 404s, because the host is
        // reserved (RFC 2606 `.invalid`) or is the literal `ACCOUNT_ID` token.
        if (IsPlaceholder(PublicBaseUrl)) throw new InvalidOperationException("MediaStorage:PublicBaseUrl is still a placeholder. Set it to the R2 custom domain or the r2.dev delivery URL.");
        if (IsPlaceholder(ServiceUrl)) throw new InvalidOperationException("MediaStorage:ServiceUrl is still a placeholder. Set it to https://<accountId>.r2.cloudflarestorage.com.");
    }

    /// <summary>
    /// True for the committed placeholder endpoints only — an RFC 2606 reserved
    /// <c>.invalid</c> host or the literal <c>ACCOUNT_ID</c> token. Deliberately
    /// narrow: a real staging or production host must never trip this.
    /// </summary>
    private static bool IsPlaceholder(string value) =>
        value.Contains("ACCOUNT_ID", StringComparison.OrdinalIgnoreCase)
        || (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase));
}

public sealed class MediaProcessingOptions
{
    public int LeaseMinutes { get; set; } = 5;
    public int LeaseHeartbeatMinutes { get; set; } = 1;
    public int MaxAttempts { get; set; } = 3;
}

public sealed class MediaRetentionOptions
{
    public int DeletedGraceHours { get; set; }
    public int FailedUploadHours { get; set; }
    public int QuarantineHours { get; set; }
}

public sealed class MediaPurposeLimit
{
    public long MaxBytes { get; set; }
    public int MinWidth { get; set; }
    public int MinHeight { get; set; }
    public int MaxWidth { get; set; }
    public int MaxHeight { get; set; }
    public long MaxPixels { get; set; }
}

public sealed class MediaLimitOptions
{
    public string[] SupportedInputMimeTypes { get; set; } = ["image/jpeg", "image/png", "image/webp"];
    public Dictionary<string, MediaPurposeLimit> Purposes { get; set; } = new(StringComparer.Ordinal);

    public static MediaLimitOptions CreateDefaults() => new()
    {
        Purposes = new Dictionary<string, MediaPurposeLimit>(StringComparer.Ordinal)
        {
            // No minimum dimensions: the pipeline never upscales, so small images are stored as-is.
            [MediaPurposes.UserAvatar] = new() { MaxBytes = 5_242_880, MaxWidth = 8_000, MaxHeight = 8_000, MaxPixels = 24_000_000 },
            [MediaPurposes.BusinessLogo] = new() { MaxBytes = 5_242_880, MaxWidth = 8_000, MaxHeight = 8_000, MaxPixels = 24_000_000 },
            [MediaPurposes.BusinessCover] = new() { MaxBytes = 10_485_760, MaxWidth = 12_000, MaxHeight = 12_000, MaxPixels = 40_000_000 },
            [MediaPurposes.BusinessGallery] = new() { MaxBytes = 12_582_912, MaxWidth = 12_000, MaxHeight = 12_000, MaxPixels = 40_000_000 },
            [MediaPurposes.ServiceImage] = new() { MaxBytes = 8_388_608, MaxWidth = 10_000, MaxHeight = 10_000, MaxPixels = 30_000_000 },
            [MediaPurposes.LoyaltyProgramImage] = new() { MaxBytes = 8_388_608, MaxWidth = 10_000, MaxHeight = 10_000, MaxPixels = 30_000_000 },
            [MediaPurposes.ReviewImage] = new() { MaxBytes = 6_291_456, MaxWidth = 10_000, MaxHeight = 10_000, MaxPixels = 30_000_000 }
        }
    };
}
