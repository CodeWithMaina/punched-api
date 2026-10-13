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
        // MEASUREMENT: timed inside the service so it covers the read plus projection only,
        // not ASP.NET middleware. Outcome is tagged so cache-miss/not-found costs are visible.
        var startedAt = DateTime.UtcNow;
        var media = await _db.Media.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mediaId, cancellationToken);
        if (media == null)
        {
            MediaMetrics.RecordRetrievalDuration(DateTime.UtcNow - startedAt, "unknown", "not_found");
            return Fail<MediaResponse>("MEDIA_NOT_FOUND", "The media could not be found.");
        }
        // Each branch records its own outcome so the histogram separates "delivered variants"
        // from "not ready yet" and "gated review image", which have very different costs.
        if (media.Status != MediaStatus.Ready)
        {
            MediaMetrics.RecordRetrievalDuration(DateTime.UtcNow - startedAt, media.Purpose, "not_ready");
            return ApiResponse<MediaResponse>.Ok(Map(media));
        }
        if (media.Purpose == MediaPurposes.ReviewImage)
        {
            var published = await (from rm in _db.ReviewMedia.AsNoTracking() join r in _db.Reviews.AsNoTracking() on rm.ReviewId equals r.Id where rm.MediaId == mediaId && r.Status == ReviewStatuses.Published select rm.Id).AnyAsync(cancellationToken);
            if (!published)
            {
                MediaMetrics.RecordRetrievalDuration(DateTime.UtcNow - startedAt, media.Purpose, "gated");
                return ApiResponse<MediaResponse>.Ok(new MediaResponse { Id = media.Id, Purpose = media.Purpose, Status = media.Status.ToString(), Visibility = MediaVisibility.Private.ToString(), CreatedAt = media.CreatedAt, UpdatedAt = media.UpdatedAt });
            }
        }
        MediaMetrics.RecordRetrievalDuration(DateTime.UtcNow - startedAt, media.Purpose, "success");
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
        var businessId = await _businessContext.GetBusinessIdAsync();
        if (!businessId.HasValue || _businessContext.GetRole() is not ("Business" or "Staff"))
            return Fail<BusinessMediaResponse>("FORBIDDEN", "Business media management is not authorized.");

        // Serialize concurrent attaches for the same business so the capacity check and the
        // insert cannot interleave (Postgres row lock; skipped for non-relational test providers).
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await LockBusinessForUpdateAsync(businessId.Value, cancellationToken);

        var row = await _db.BusinessMedia.FirstOrDefaultAsync(x => x.BusinessId == businessId && x.MediaId == mediaId, cancellationToken);
        // Idempotency: an already-attached image updates in place and never consumes capacity.
        if (row == null && !await HasGalleryCapacityAsync(businessId.Value, cancellationToken))
            return Fail<BusinessMediaResponse>("MEDIA_QUOTA_EXCEEDED", $"The gallery is limited to {_options.MaxGalleryImages} images. Remove an image before adding another.");

        if (featured) await _db.BusinessMedia.Where(x => x.BusinessId == businessId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsFeatured, false), cancellationToken);
        if (row == null) _db.BusinessMedia.Add(row = new BusinessMedia { BusinessId = businessId.Value, MediaId = mediaId, SortOrder = sortOrder ?? 0, IsFeatured = featured });
        else { row.SortOrder = sortOrder ?? row.SortOrder; row.IsFeatured = featured; }
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ApiResponse<BusinessMediaResponse>.Ok(new BusinessMediaResponse { BusinessId = businessId.Value, MediaId = mediaId, Role = "Gallery", SortOrder = row.SortOrder, Featured = row.IsFeatured, Media = Map(media.Data) });
    }

    /// <summary>
    /// True when the gallery can accept one more attachment. Unlimited when
    /// <see cref="MediaStorageOptions.MaxGalleryImages"/> &lt;= 0. Counts attachment rows for
    /// the business only (not orphaned media objects in storage), so a detach frees capacity.
    /// </summary>
    private async Task<bool> HasGalleryCapacityAsync(Guid businessId, CancellationToken cancellationToken)
    {
        if (_options.MaxGalleryImages <= 0) return true;
        var used = await _db.BusinessMedia.CountAsync(x => x.BusinessId == businessId, cancellationToken);
        return used < _options.MaxGalleryImages;
    }

    /// <summary>PostgreSQL row lock (FOR UPDATE) on the business row. Skipped for SQLite/non-relational providers.</summary>
    private async Task LockBusinessForUpdateAsync(Guid businessId, CancellationToken cancellationToken)
    {
        var provider = _db.Database.ProviderName;
        if (provider == null || provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)) return;
        await _db.Database.ExecuteSqlRawAsync("SELECT id FROM businesses WHERE id = {0} FOR UPDATE", businessId);
    }

    public async Task<ApiResponse<GalleryCapacityResponse>> GetBusinessGalleryCapacityAsync(Guid userId, CancellationToken cancellationToken)
    {
        var businessId = await _businessContext.GetBusinessIdAsync();
        if (!businessId.HasValue || _businessContext.GetRole() is not ("Business" or "Staff"))
            return Fail<GalleryCapacityResponse>("FORBIDDEN", "Business media management is not authorized.");
        var used = await _db.BusinessMedia.CountAsync(x => x.BusinessId == businessId.Value, cancellationToken);
        var limit = _options.MaxGalleryImages > 0 ? _options.MaxGalleryImages : (int?)null;
        return ApiResponse<GalleryCapacityResponse>.Ok(new GalleryCapacityResponse
        {
            Limit = limit,
            Used = used,
            Remaining = limit.HasValue ? Math.Max(0, limit.Value - used) : null
        });
    }

    public async Task<ApiResponse<List<BusinessMediaResponse>>> GetBusinessGalleryAsync(Guid userId, CancellationToken cancellationToken)
    {
        var businessId = await _businessContext.GetBusinessIdAsync();
        if (!businessId.HasValue || _businessContext.GetRole() is not ("Business" or "Staff"))
            return Fail<List<BusinessMediaResponse>>("FORBIDDEN", "Business media management is not authorized.");

        var rows = await (from relationship in _db.BusinessMedia.AsNoTracking()
                          join media in _db.Media.AsNoTracking() on relationship.MediaId equals media.Id
                          where relationship.BusinessId == businessId.Value && media.Status == MediaStatus.Ready
                          orderby relationship.SortOrder, relationship.CreatedAt
                          select new { relationship, media }).ToListAsync(cancellationToken);

        return ApiResponse<List<BusinessMediaResponse>>.Ok(rows.Select(item => new BusinessMediaResponse
        {
            BusinessId = item.relationship.BusinessId,
            MediaId = item.relationship.MediaId,
            Role = item.relationship.Role,
            SortOrder = item.relationship.SortOrder,
            Featured = item.relationship.IsFeatured,
            Media = Map(item.media)
        }).ToList());
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
            case "business-gallery":
                var galleryBusinessId = targetId == Guid.Empty ? businessId : targetId;
                var galleryRows = await _db.BusinessMedia.Where(x => x.BusinessId == galleryBusinessId).ToListAsync(cancellationToken);
                var removedFeatured = galleryRows.FirstOrDefault(x => x.MediaId == mediaId && x.IsFeatured);
                _db.BusinessMedia.RemoveRange(galleryRows.Where(x => x.MediaId == mediaId));
                // Featured-image invariant: when the featured image is removed, promote the next
                // remaining image (lowest sort order) so the gallery never silently loses its
                // featured image. Does not apply to service/loyalty/review relationships.
                if (removedFeatured != null)
                {
                    var nextFeatured = galleryRows
                        .Where(x => x.MediaId != mediaId)
                        .OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt)
                        .FirstOrDefault();
                    if (nextFeatured != null) nextFeatured.IsFeatured = true;
                }
                break;
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
        if (media.Data!.Status is not (MediaStatus.Uploaded or MediaStatus.Processing or MediaStatus.Ready))
            return Fail<ServiceMediaResponse>("MEDIA_NOT_ATTACHABLE", "The upload must complete before it can be attached.");
        var row = await _db.ServiceMedia.FirstOrDefaultAsync(x => x.ServiceCatalogItemId == serviceId && x.MediaId == mediaId, cancellationToken);
        if (row == null)
        {
            var previousPrimary = await _db.ServiceMedia
                .Where(x => x.ServiceCatalogItemId == serviceId && x.Role == "Primary")
                .ToListAsync(cancellationToken);
            _db.ServiceMedia.RemoveRange(previousPrimary);
            _db.ServiceMedia.Add(row = new ServiceMedia { ServiceCatalogItemId = serviceId, MediaId = mediaId, SortOrder = sortOrder });
        }
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

