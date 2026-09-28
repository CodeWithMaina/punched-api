using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Media;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

public sealed partial class MediaService
{
    public async Task<ApiResponse<MediaResponse>> AssignBusinessFieldAsync(Guid userId, string field, Guid mediaId, CancellationToken cancellationToken)
    {
        var purpose = field switch { "logo" => MediaPurposes.BusinessLogo, "cover" => MediaPurposes.BusinessCover, _ => "" };
        if (!MediaPolicy.AllowsRelationship(purpose, field == "logo" ? MediaRelationshipKinds.BusinessLogo : MediaRelationshipKinds.BusinessCover))
            return Fail<MediaResponse>("INVALID_MEDIA_PURPOSE", "Invalid business media field.");
        var businessId = await _businessContext.GetBusinessIdAsync();
        if (!businessId.HasValue || _businessContext.GetRole() != "Business") return Fail<MediaResponse>("FORBIDDEN", "Only the business owner can change this field.");
        var target = await _db.Businesses.FirstOrDefaultAsync(x => x.Id == businessId, cancellationToken);
        if (target == null) return Fail<MediaResponse>("TARGET_NOT_FOUND", "Business not found.");
        if (mediaId == Guid.Empty)
        {
            if (field == "logo") target.LogoMediaId = null; else target.CoverMediaId = null;
            await _db.SaveChangesAsync(cancellationToken);
            return ApiResponse<MediaResponse>.Ok(new MediaResponse { Purpose = purpose, Status = "Cleared" });
        }
        var media = await RequireOwnedBusinessMediaAsync(userId, mediaId, purpose, cancellationToken);
        if (!media.Success) return Fail<MediaResponse>(media.Error!.Code, media.Error.Message);
        if (media.Data!.Status != MediaStatus.Ready) return Fail<MediaResponse>("MEDIA_NOT_READY", "Only ready media can be assigned.");
        if (field == "logo") target.LogoMediaId = mediaId; else target.CoverMediaId = mediaId;
        await _db.SaveChangesAsync(cancellationToken);
        return ApiResponse<MediaResponse>.Ok(Map(media.Data));
    }

    public async Task<ApiResponse<MediaResponse>> AssignUserAvatarAsync(Guid userId, Guid mediaId, CancellationToken cancellationToken)
    {
        var user = await _db.Users.FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);
        if (user == null) return Fail<MediaResponse>("TARGET_NOT_FOUND", "User not found.");
        if (mediaId == Guid.Empty)
        {
            user.AvatarMediaId = null; await _db.SaveChangesAsync(cancellationToken);
            return ApiResponse<MediaResponse>.Ok(new MediaResponse { Purpose = MediaPurposes.UserAvatar, Status = "Cleared" });
        }
        var media = await _db.Media.FirstOrDefaultAsync(x => x.Id == mediaId && x.OwnerUserId == userId && x.Purpose == MediaPurposes.UserAvatar, cancellationToken);
        if (media == null) return Fail<MediaResponse>("MEDIA_NOT_FOUND", "The media could not be found.");
        if (media.Status != MediaStatus.Ready) return Fail<MediaResponse>("MEDIA_NOT_READY", "Only ready media can be assigned.");
        user.AvatarMediaId = mediaId; media.Visibility = MediaVisibility.Public; await _db.SaveChangesAsync(cancellationToken);
        return ApiResponse<MediaResponse>.Ok(Map(media));
    }

    public async Task<ApiResponse<MediaResponse>> GetPublicProjectionAsync(Guid mediaId, CancellationToken cancellationToken)
    {
        var media = await _db.Media.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
        if (media == null) return Fail<MediaResponse>("MEDIA_NOT_FOUND", "The media could not be found.");
        if (media.Status != MediaStatus.Ready) return ApiResponse<MediaResponse>.Ok(Map(media));
        if (media.Purpose == MediaPurposes.ReviewImage)
        {
            var published = await (from rm in _db.ReviewMedia.AsNoTracking() join r in _db.Reviews.AsNoTracking() on rm.ReviewId equals r.Id where rm.MediaId == mediaId && r.Status == ReviewStatuses.Published select rm.Id).AnyAsync(cancellationToken);
            if (!published) return ApiResponse<MediaResponse>.Ok(new MediaResponse { Id = media.Id, Purpose = media.Purpose, Status = media.Status.ToString(), Visibility = MediaVisibility.Private.ToString(), CreatedAt = media.CreatedAt, UpdatedAt = media.UpdatedAt });
        }
        return ApiResponse<MediaResponse>.Ok(Map(media));
    }

    public async Task<ApiResponse<bool>> MarkReviewMediaPurgeRequiredAsync(Guid reviewId, CancellationToken cancellationToken)
    {
        var ids = await _db.ReviewMedia.Where(x => x.ReviewId == reviewId).Select(x => x.MediaId).ToListAsync(cancellationToken);
        if (ids.Count == 0) return ApiResponse<bool>.Ok(true);
        await _db.Media.Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.DeliveryPurgeStatus, DeliveryPurgeStatus.Pending)
            .SetProperty(x => x.DeliveryPurgeNextAttemptAt, DateTime.UtcNow)
            .SetProperty(x => x.DeliveryPurgeErrorCode, "REVIEW_NOT_PUBLISHED"), cancellationToken);
        return ApiResponse<bool>.Ok(true);
    }

    public async Task<ApiResponse<BusinessMediaResponse>> AttachGalleryAsync(Guid userId, Guid mediaId, int? sortOrder, bool featured, CancellationToken cancellationToken)
    {
        var media = await RequireOwnedBusinessMediaAsync(userId, mediaId, MediaPurposes.BusinessGallery, cancellationToken);
        if (!media.Success) return Fail<BusinessMediaResponse>(media.Error!.Code, media.Error.Message);
        if (media.Data!.Status != MediaStatus.Ready) return Fail<BusinessMediaResponse>("MEDIA_NOT_READY", "Only ready media can be attached.");
        var businessId = (await _businessContext.GetBusinessIdAsync())!.Value;
        if (featured) await _db.BusinessMedia.Where(x => x.BusinessId == businessId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsFeatured, false), cancellationToken);
        var row = await _db.BusinessMedia.FirstOrDefaultAsync(x => x.BusinessId == businessId && x.MediaId == mediaId, cancellationToken);
        if (row == null) _db.BusinessMedia.Add(row = new BusinessMedia { BusinessId = businessId, MediaId = mediaId, SortOrder = sortOrder ?? 0, IsFeatured = featured });
        else { row.SortOrder = sortOrder ?? row.SortOrder; row.IsFeatured = featured; }
        await _db.SaveChangesAsync(cancellationToken);
        return ApiResponse<BusinessMediaResponse>.Ok(new BusinessMediaResponse { BusinessId = businessId, MediaId = mediaId, Role = "Gallery", SortOrder = row.SortOrder, Featured = row.IsFeatured, Media = Map(media.Data) });
    }

    public async Task<ApiResponse<bool>> ReorderGalleryAsync(Guid userId, IReadOnlyList<Guid> mediaIds, CancellationToken cancellationToken)
    {
        var businessId = await _businessContext.GetBusinessIdAsync();
        if (!businessId.HasValue || _businessContext.GetRole() != "Business") return Fail<bool>("FORBIDDEN", "Only the business owner can reorder gallery media.");
        var rows = await _db.BusinessMedia.Where(x => x.BusinessId == businessId && mediaIds.Contains(x.MediaId)).ToListAsync(cancellationToken);
        if (rows.Count != mediaIds.Distinct().Count()) return Fail<bool>("VALIDATION_ERROR", "Gallery order contains an invalid media ID.");
        for (var i = 0; i < mediaIds.Count; i++) rows.Single(x => x.MediaId == mediaIds[i]).SortOrder = i;
        await _db.SaveChangesAsync(cancellationToken); return ApiResponse<bool>.Ok(true);
    }

    public async Task<ApiResponse<bool>> DetachRelationshipAsync(Guid userId, string relationship, Guid targetId, Guid mediaId, CancellationToken cancellationToken)
    {
        var businessId = await _businessContext.GetBusinessIdAsync();
        var media = await _db.Media.FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
        if (media == null) return ApiResponse<bool>.Ok(true);
        var authorized = media.OwnerUserId == userId || (businessId.HasValue && media.BusinessId == businessId && _businessContext.GetRole() == "Business");
        if (!authorized) return Fail<bool>("FORBIDDEN", "You are not authorized to detach this media.");
        switch (relationship)
        {
            case "business-gallery": _db.BusinessMedia.RemoveRange(_db.BusinessMedia.Where(x => x.BusinessId == targetId && x.MediaId == mediaId)); break;
            case "service": _db.ServiceMedia.RemoveRange(_db.ServiceMedia.Where(x => x.ServiceCatalogItemId == targetId && x.MediaId == mediaId)); break;
            case "loyalty-program": _db.LoyaltyProgramMedia.RemoveRange(_db.LoyaltyProgramMedia.Where(x => x.LoyaltyProgramId == targetId && x.MediaId == mediaId)); break;
            case "review": _db.ReviewMedia.RemoveRange(_db.ReviewMedia.Where(x => x.ReviewId == targetId && x.MediaId == mediaId)); break;
            default: return Fail<bool>("VALIDATION_ERROR", "Unsupported media relationship.");
        }
        await _db.SaveChangesAsync(cancellationToken); return ApiResponse<bool>.Ok(true);
    }

    public async Task<ApiResponse<ServiceMediaResponse>> AttachServiceAsync(Guid userId, Guid serviceId, Guid mediaId, int sortOrder, CancellationToken cancellationToken)
    {
        var media = await RequireOwnedBusinessMediaAsync(userId, mediaId, MediaPurposes.ServiceImage, cancellationToken);
        if (!media.Success) return Fail<ServiceMediaResponse>(media.Error!.Code, media.Error.Message);
        var service = await _db.ServiceCatalogItems.FirstOrDefaultAsync(x => x.Id == serviceId && x.BusinessId == media.Data!.BusinessId, cancellationToken);
        if (service == null) return Fail<ServiceMediaResponse>("TARGET_NOT_FOUND", "The service target is not available.");
        if (media.Data!.Status != MediaStatus.Ready) return Fail<ServiceMediaResponse>("MEDIA_NOT_READY", "Only ready media can be attached.");
        var row = await _db.ServiceMedia.FirstOrDefaultAsync(x => x.ServiceCatalogItemId == serviceId && x.MediaId == mediaId, cancellationToken);
        if (row == null) _db.ServiceMedia.Add(row = new ServiceMedia { ServiceCatalogItemId = serviceId, MediaId = mediaId, SortOrder = sortOrder });
        else row.SortOrder = sortOrder;
        await _db.SaveChangesAsync(cancellationToken);
        return ApiResponse<ServiceMediaResponse>.Ok(new ServiceMediaResponse { ServiceId = serviceId, MediaId = mediaId, SortOrder = row.SortOrder, Media = Map(media.Data) });
    }

    public async Task<ApiResponse<LoyaltyProgramMediaResponse>> AttachProgramAsync(Guid userId, Guid programId, Guid mediaId, int sortOrder, CancellationToken cancellationToken)
    {
        var media = await RequireOwnedBusinessMediaAsync(userId, mediaId, MediaPurposes.LoyaltyProgramImage, cancellationToken);
        if (!media.Success) return Fail<LoyaltyProgramMediaResponse>(media.Error!.Code, media.Error.Message);
        var program = await _db.LoyaltyPrograms.FirstOrDefaultAsync(x => x.Id == programId && x.BusinessId == media.Data!.BusinessId, cancellationToken);
        if (program == null) return Fail<LoyaltyProgramMediaResponse>("TARGET_NOT_FOUND", "The program target is not available.");
        if (media.Data!.Status != MediaStatus.Ready) return Fail<LoyaltyProgramMediaResponse>("MEDIA_NOT_READY", "Only ready media can be attached.");
        var row = await _db.LoyaltyProgramMedia.FirstOrDefaultAsync(x => x.LoyaltyProgramId == programId && x.MediaId == mediaId, cancellationToken);
        if (row == null) _db.LoyaltyProgramMedia.Add(row = new LoyaltyProgramMedia { LoyaltyProgramId = programId, MediaId = mediaId, SortOrder = sortOrder });
        else row.SortOrder = sortOrder;
        await _db.SaveChangesAsync(cancellationToken);
        return ApiResponse<LoyaltyProgramMediaResponse>.Ok(new LoyaltyProgramMediaResponse { ProgramId = programId, MediaId = mediaId, SortOrder = row.SortOrder, Media = Map(media.Data) });
    }

    public async Task<ApiResponse<ReviewMediaResponse>> AttachReviewAsync(Guid userId, Guid reviewId, Guid mediaId, int sortOrder, CancellationToken cancellationToken)
    {
        var media = await _db.Media.FirstOrDefaultAsync(x => x.Id == mediaId && x.Purpose == MediaPurposes.ReviewImage && x.UploadedByUserId == userId, cancellationToken);
        if (media == null) return Fail<ReviewMediaResponse>("MEDIA_NOT_FOUND", "The media could not be found.");
        var review = await _db.Reviews.FirstOrDefaultAsync(x => x.Id == reviewId && x.CustomerId == userId && x.BusinessId == media.BusinessId, cancellationToken);
        if (review == null) return Fail<ReviewMediaResponse>("TARGET_NOT_FOUND", "The review target is not available.");
        if (media.Status != MediaStatus.Ready) return Fail<ReviewMediaResponse>("MEDIA_NOT_READY", "Only ready media can be attached.");
        var row = await _db.ReviewMedia.FirstOrDefaultAsync(x => x.ReviewId == reviewId && x.MediaId == mediaId, cancellationToken);
        if (row == null) _db.ReviewMedia.Add(row = new ReviewMedia { ReviewId = reviewId, MediaId = mediaId, SortOrder = sortOrder });
        else row.SortOrder = sortOrder;
        await _db.SaveChangesAsync(cancellationToken);
        return ApiResponse<ReviewMediaResponse>.Ok(new ReviewMediaResponse { ReviewId = reviewId, MediaId = mediaId, SortOrder = row.SortOrder, Media = Map(media) });
    }
}

