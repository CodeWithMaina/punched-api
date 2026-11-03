namespace PunchedApi.Application.Media;

public static class MediaRelationshipKinds
{
    public const string BusinessLogo = "business-logo-field";
    public const string BusinessCover = "business-cover-field";
    public const string BusinessGallery = "business-gallery";
    public const string UserAvatar = "user-avatar-field";
    public const string Service = "service";
    public const string LoyaltyProgram = "loyalty-program";
    public const string Review = "review";
}

/// <summary>Single compatibility authority for Media purpose, owner and relationship rules.</summary>
public static class MediaPolicy
{
    public enum OwnerScope { Business, Personal }

    public static OwnerScope RequiredOwner(string purpose) => purpose switch
    {
        Domain.Entities.MediaPurposes.UserAvatar => OwnerScope.Personal,
        Domain.Entities.MediaPurposes.BusinessLogo or
        Domain.Entities.MediaPurposes.BusinessCover or
        Domain.Entities.MediaPurposes.BusinessGallery or
        Domain.Entities.MediaPurposes.ServiceImage or
        Domain.Entities.MediaPurposes.LoyaltyProgramImage or
        Domain.Entities.MediaPurposes.ReviewImage => OwnerScope.Business,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unsupported media purpose.")
    };

    public static bool HasValidOwner(string purpose, Guid? businessId, Guid? ownerUserId) =>
        RequiredOwner(purpose) == OwnerScope.Business
            ? businessId.HasValue && !ownerUserId.HasValue
            : ownerUserId.HasValue && !businessId.HasValue;

    public static bool AllowsRelationship(string purpose, string relationship) =>
        (purpose, relationship) switch
        {
            (Domain.Entities.MediaPurposes.BusinessLogo, MediaRelationshipKinds.BusinessLogo) => true,
            (Domain.Entities.MediaPurposes.BusinessCover, MediaRelationshipKinds.BusinessCover) => true,
            (Domain.Entities.MediaPurposes.BusinessGallery, MediaRelationshipKinds.BusinessGallery) => true,
            (Domain.Entities.MediaPurposes.UserAvatar, MediaRelationshipKinds.UserAvatar) => true,
            (Domain.Entities.MediaPurposes.ServiceImage, MediaRelationshipKinds.Service) => true,
            (Domain.Entities.MediaPurposes.LoyaltyProgramImage, MediaRelationshipKinds.LoyaltyProgram) => true,
            (Domain.Entities.MediaPurposes.ReviewImage, MediaRelationshipKinds.Review) => true,
            _ => false
        };

    public static void RequireRelationship(string purpose, string relationship)
    {
        if (!AllowsRelationship(purpose, relationship))
            throw new InvalidOperationException($"Media purpose '{purpose}' cannot be assigned through '{relationship}'.");
    }

    /// <summary>
    /// Whether a media row may back one of a service's images.
    ///
    /// <para>Shared by the single-image attach on <c>MediaService</c> and the
    /// ordered image-set write on <c>ServiceCatalogService</c>, so the two paths
    /// can never disagree about what is attachable — a service image must belong
    /// to the same business as the service, carry the service-image purpose and
    /// have finished uploading.</para>
    /// </summary>
    public static bool IsServiceImageAttachable(Guid serviceBusinessId, Domain.Entities.Media? media) =>
        media != null
        && media.Purpose == Domain.Entities.MediaPurposes.ServiceImage
        && media.BusinessId == serviceBusinessId
        && media.Status is Domain.Entities.MediaStatus.Uploaded
            or Domain.Entities.MediaStatus.Processing
            or Domain.Entities.MediaStatus.Ready;

    /// <summary>
    /// Whether one more image fits on a service. <paramref name="maxServiceImages"/>
    /// &lt;= 0 means unlimited; otherwise the cap is a hard ceiling that every
    /// write path enforces server-side.
    /// </summary>
    public static bool HasServiceImageCapacity(int attachedCount, int maxServiceImages) =>
        maxServiceImages <= 0 || attachedCount < maxServiceImages;
}
