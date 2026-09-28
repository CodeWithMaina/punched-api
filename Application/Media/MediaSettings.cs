namespace PunchedApi.Application.Media;

using PunchedApi.Domain.Entities;

public sealed class MediaStorageOptions
{
    public const string SectionName = "MediaStorage";
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
    public int CleanupBatchSize { get; set; } = 25;
    public int WorkerBatchSize { get; set; } = 4;
    public MediaProcessingOptions Processing { get; set; } = new();
    public MediaRetentionOptions Retention { get; set; } = new();
    public MediaLimitOptions Limits { get; set; } = MediaLimitOptions.CreateDefaults();

    public void Validate(bool requireR2)
    {
        if (PresignMinutes is < 1 or > 7) throw new InvalidOperationException("MediaStorage:PresignMinutes must be between 1 and 7.");
        if (PendingRowExpirationMinutes < 1 || PendingObjectLifecycleHours < 1) throw new InvalidOperationException("Media pending expiry must be positive.");
        if (Processing.LeaseMinutes < 1 || Processing.LeaseHeartbeatMinutes < 1 || Processing.LeaseHeartbeatMinutes >= Processing.LeaseMinutes) throw new InvalidOperationException("Media lease heartbeat must be positive and shorter than the lease.");
        if (Processing.MaxAttempts < 1) throw new InvalidOperationException("Media processing attempts must be positive.");
        if (Limits.SupportedInputMimeTypes.Length == 0) throw new InvalidOperationException("At least one input MIME type is required.");
        foreach (var purpose in Limits.Purposes.Values)
            if (purpose.MaxBytes < 1 || purpose.MaxWidth < 1 || purpose.MaxHeight < 1 || purpose.MaxPixels < 1)
                throw new InvalidOperationException("Media purpose limits must be positive.");
        if (!requireR2) return;
        if (!string.Equals(Provider, "r2", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("MediaStorage:Provider must be r2 in production.");
        if (string.IsNullOrWhiteSpace(AccountId) || string.IsNullOrWhiteSpace(AccessKeyId) || string.IsNullOrWhiteSpace(SecretAccessKey)) throw new InvalidOperationException("R2 credentials must be injected at runtime.");
        if (string.IsNullOrWhiteSpace(PrivateBucket) || string.IsNullOrWhiteSpace(PublicBucket) || string.IsNullOrWhiteSpace(PublicBaseUrl) || string.IsNullOrWhiteSpace(ServiceUrl)) throw new InvalidOperationException("R2 buckets, service URL, and public base URL are required.");
        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var publicUri) || publicUri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("MediaStorage:PublicBaseUrl must be HTTPS.");
        if (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var serviceUri) || serviceUri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("MediaStorage:ServiceUrl must be HTTPS.");
    }
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
            [MediaPurposes.UserAvatar] = new() { MinWidth = 256, MinHeight = 256, MaxBytes = 5_242_880, MaxWidth = 8_000, MaxHeight = 8_000, MaxPixels = 24_000_000 },
            [MediaPurposes.BusinessLogo] = new() { MinWidth = 128, MinHeight = 128, MaxBytes = 5_242_880, MaxWidth = 8_000, MaxHeight = 8_000, MaxPixels = 24_000_000 },
            [MediaPurposes.BusinessCover] = new() { MinWidth = 1200, MinHeight = 400, MaxBytes = 10_485_760, MaxWidth = 12_000, MaxHeight = 12_000, MaxPixels = 40_000_000 },
            [MediaPurposes.BusinessGallery] = new() { MinWidth = 640, MinHeight = 640, MaxBytes = 12_582_912, MaxWidth = 12_000, MaxHeight = 12_000, MaxPixels = 40_000_000 },
            [MediaPurposes.ServiceImage] = new() { MinWidth = 640, MinHeight = 426, MaxBytes = 8_388_608, MaxWidth = 10_000, MaxHeight = 10_000, MaxPixels = 30_000_000 },
            [MediaPurposes.LoyaltyProgramImage] = new() { MaxBytes = 8_388_608, MaxWidth = 10_000, MaxHeight = 10_000, MaxPixels = 30_000_000 },
            [MediaPurposes.ReviewImage] = new() { MinWidth = 480, MinHeight = 480, MaxBytes = 6_291_456, MaxWidth = 10_000, MaxHeight = 10_000, MaxPixels = 30_000_000 }
        }
    };
}
