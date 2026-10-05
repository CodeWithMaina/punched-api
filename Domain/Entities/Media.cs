namespace PunchedApi.Domain.Entities;

public static class MediaPurposes
{
    public const string BusinessLogo = "business-logo";
    public const string BusinessCover = "business-cover";
    public const string BusinessGallery = "business-gallery";
    public const string UserAvatar = "user-avatar";
    public const string ServiceImage = "service-image";
    public const string LoyaltyProgramImage = "loyalty-program-image";
    public const string ReviewImage = "review-image";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        BusinessLogo, BusinessCover, BusinessGallery, UserAvatar, ServiceImage, LoyaltyProgramImage, ReviewImage
    };
}

public enum MediaStatus { Pending = 0, Uploaded = 1, Processing = 2, Ready = 3, Failed = 4, Deleting = 5, Deleted = 6 }
public enum MediaVisibility { Private = 0, Public = 1 }
public enum DeliveryPurgeStatus { NotRequired = 0, Pending = 1, Completed = 2, Failed = 3 }

/// <summary>Server-owned image upload metadata. Source keys never leave the API.</summary>
public class Media : BaseEntity
{
    public Guid? BusinessId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid UploadedByUserId { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string Provider { get; set; } = "r2";
    public string SourceKey { get; set; } = string.Empty;
    public MediaStatus Status { get; set; } = MediaStatus.Pending;
    public MediaVisibility Visibility { get; set; } = MediaVisibility.Private;
    public DateTime? UploadGrantExpiresAt { get; set; }
    public int UploadAttempt { get; set; } = 1;
    public long? ExpectedSizeBytes { get; set; }
    public string? DeclaredMimeType { get; set; }
    public string? DetectedMimeType { get; set; }
    public long? SourceSizeBytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? Sha256 { get; set; }
    public string? OriginalFileName { get; set; }
    public string? ProcessingLeaseToken { get; set; }
    public DateTime? ProcessingLeaseUntil { get; set; }
    public int ProcessingAttempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastErrorCode { get; set; }
    public string? ProcessingRecipe { get; set; }
    public string VariantsJson { get; set; } = "[]";
    public DeliveryPurgeStatus DeliveryPurgeStatus { get; set; } = DeliveryPurgeStatus.NotRequired;
    public int DeliveryPurgeAttempts { get; set; }
    public DateTime? DeliveryPurgeNextAttemptAt { get; set; }
    public string? DeliveryPurgeErrorCode { get; set; }
    public DateTime? DeliveryPurgedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }

    public virtual Business? Business { get; set; }
    public virtual User? OwnerUser { get; set; }
    public virtual User UploadedByUser { get; set; } = null!;
}

public class BusinessMedia : BaseEntity
{
    public Guid BusinessId { get; set; }
    public Guid MediaId { get; set; }
    public string Role { get; set; } = "Gallery";
    public int SortOrder { get; set; }
    public bool IsFeatured { get; set; }
    public Business Business { get; set; } = null!;
    public Media Media { get; set; } = null!;
}

public class ServiceMedia : BaseEntity
{
    public Guid ServiceCatalogItemId { get; set; }
    public Guid MediaId { get; set; }
    public string Role { get; set; } = "Primary";
    public int SortOrder { get; set; }
    public ServiceCatalogItem ServiceCatalogItem { get; set; } = null!;
    public Media Media { get; set; } = null!;
}

public class LoyaltyProgramMedia : BaseEntity
{
    public Guid LoyaltyProgramId { get; set; }
    public Guid MediaId { get; set; }
    public string Role { get; set; } = "Primary";
    public int SortOrder { get; set; }
    public LoyaltyProgram LoyaltyProgram { get; set; } = null!;
    public Media Media { get; set; } = null!;
}

public class ReviewMedia : BaseEntity
{
    public Guid ReviewId { get; set; }
    public Guid MediaId { get; set; }
    public int SortOrder { get; set; }
    public Review Review { get; set; } = null!;
    public Media Media { get; set; } = null!;
}
