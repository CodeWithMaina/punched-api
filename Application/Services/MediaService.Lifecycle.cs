using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Media;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

public sealed partial class MediaService
{
    private async Task<ApiResponse<MediaUploadGrantResponse>> GrantAsync(Domain.Entities.Media media, CancellationToken cancellationToken)
    {
        var expires = media.UploadGrantExpiresAt ?? DateTime.UtcNow.AddMinutes(_options.PresignMinutes);
        var grant = await _store.PresignPutAsync(ObjectStoreBucket.PrivateSource, media.SourceKey, media.DeclaredMimeType ?? "application/octet-stream", expires, cancellationToken);
        return ApiResponse<MediaUploadGrantResponse>.Ok(new MediaUploadGrantResponse
        {
            MediaId = media.Id, Status = media.Status.ToString(), UploadUrl = grant.Url.ToString(),
            RequiredHeaders = grant.RequiredHeaders.ToDictionary(x => x.Key, x => x.Value), UploadGrantExpiresAt = grant.ExpiresAt
        });
    }

    public async Task<ApiResponse<MediaUploadGrantResponse>> RegrantAsync(Guid userId, Guid mediaId, CancellationToken cancellationToken)
    {
        var media = await FindScopedAsync(userId, mediaId, cancellationToken);
        if (media == null) return Fail<MediaUploadGrantResponse>("MEDIA_NOT_FOUND", "The media could not be found.");
        var mutation = await AuthorizeMutationAsync(media);
        if (!mutation.Success) return Fail<MediaUploadGrantResponse>(mutation.Error!.Code, mutation.Error.Message);
        if (media.Status != MediaStatus.Pending) return Fail<MediaUploadGrantResponse>("MEDIA_STATE_CONFLICT", "Only pending media can receive a grant.");
        if (await _store.HeadAsync(ObjectStoreBucket.PrivateSource, media.SourceKey, cancellationToken) != null)
            return Fail<MediaUploadGrantResponse>("MEDIA_UPLOAD_MISSING", "An object already exists; complete it instead.");
        media.UploadAttempt++;
        media.UploadGrantExpiresAt = DateTime.UtcNow.AddMinutes(_options.PresignMinutes);
        media.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return await GrantAsync(media, cancellationToken);
    }

    public async Task<ApiResponse<MediaResponse>> CompleteAsync(Guid userId, Guid mediaId, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            return Fail<MediaResponse>("VALIDATION_ERROR", "A valid Idempotency-Key is required.");
        var hash = Hash("POST", "/v1/media/{id}/complete", userId, new { id = mediaId });
        var replay = await _idempotency.TryGetAsync(idempotencyKey, userId, hash);
        if (replay.Conflict) return Fail<MediaResponse>("IDEMPOTENCY_CONFLICT", "The idempotency key was used with a different request.");
        if (replay.Found && replay.ResponseJson != null)
        {
            try { return ApiResponse<MediaResponse>.Ok(JsonSerializer.Deserialize<MediaResponse>(replay.ResponseJson)!); }
            catch (JsonException) { return Fail<MediaResponse>("MEDIA_STATE_CONFLICT", "The completion replay is invalid."); }
        }
        var media = await FindScopedAsync(userId, mediaId, cancellationToken);
        if (media == null) return Fail<MediaResponse>("MEDIA_NOT_FOUND", "The media could not be found.");
        var mutation = await AuthorizeMutationAsync(media);
        if (!mutation.Success) return Fail<MediaResponse>(mutation.Error!.Code, mutation.Error.Message);
        if (media.Status is MediaStatus.Uploaded or MediaStatus.Processing or MediaStatus.Ready)
            return await CompleteReplayAsync(idempotencyKey, userId, hash, media, cancellationToken);
        if (media.Status != MediaStatus.Pending) return Fail<MediaResponse>("MEDIA_STATE_CONFLICT", "The media cannot be completed now.");
        var metadata = await _store.HeadAsync(ObjectStoreBucket.PrivateSource, media.SourceKey, cancellationToken);
        if (metadata == null) return Fail<MediaResponse>("MEDIA_UPLOAD_MISSING", "The uploaded object was not found.");
        if (media.ExpectedSizeBytes.HasValue && metadata.SizeBytes != media.ExpectedSizeBytes.Value)
            return Fail<MediaResponse>("MEDIA_SIZE_MISMATCH", "The uploaded object size does not match the declared size.");
        media.Status = MediaStatus.Uploaded;
        media.NextAttemptAt = DateTime.UtcNow;
        media.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return await CompleteReplayAsync(idempotencyKey, userId, hash, media, cancellationToken);
    }

    private async Task<ApiResponse<MediaResponse>> CompleteReplayAsync(string key, Guid userId, string hash, Domain.Entities.Media media, CancellationToken cancellationToken)
    {
        var response = Map(media);
        await _idempotency.StoreAsync(key, userId, hash, JsonSerializer.Serialize(response));
        return ApiResponse<MediaResponse>.Ok(response);
    }

    public async Task<ApiResponse<MediaResponse>> GetAsync(Guid userId, Guid mediaId, CancellationToken cancellationToken)
    {
        var media = await FindScopedAsync(userId, mediaId, cancellationToken);
        return media == null ? Fail<MediaResponse>("MEDIA_NOT_FOUND", "The media could not be found.") : ApiResponse<MediaResponse>.Ok(Map(media));
    }

    public async Task<ApiResponse<MediaResponse>> RetryAsync(Guid userId, Guid mediaId, CancellationToken cancellationToken)
    {
        var media = await FindScopedAsync(userId, mediaId, cancellationToken);
        if (media == null) return Fail<MediaResponse>("MEDIA_NOT_FOUND", "The media could not be found.");
        var mutation = await AuthorizeMutationAsync(media);
        if (!mutation.Success) return Fail<MediaResponse>(mutation.Error!.Code, mutation.Error.Message);
        if (media.Status != MediaStatus.Failed || media.ProcessingAttempts >= _options.Processing.MaxAttempts || media.NextAttemptAt > DateTime.UtcNow)
            return ApiResponse<MediaResponse>.Ok(Map(media));
        media.Status = MediaStatus.Uploaded;
        media.NextAttemptAt = DateTime.UtcNow;
        media.LastErrorCode = null;
        media.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return ApiResponse<MediaResponse>.Ok(Map(media));
    }

    public async Task<ApiResponse<bool>> DeleteAsync(Guid userId, Guid mediaId, CancellationToken cancellationToken)
    {
        var media = await FindScopedAsync(userId, mediaId, cancellationToken);
        if (media == null) return ApiResponse<bool>.Ok(true);
        var mutation = await AuthorizeMutationAsync(media);
        if (!mutation.Success) return ApiResponse<bool>.Fail(mutation.Error!.Code, mutation.Error.Message);
        if (await _db.BusinessMedia.AnyAsync(x => x.MediaId == mediaId, cancellationToken) || await _db.ServiceMedia.AnyAsync(x => x.MediaId == mediaId, cancellationToken) || await _db.LoyaltyProgramMedia.AnyAsync(x => x.MediaId == mediaId, cancellationToken) || await _db.ReviewMedia.AnyAsync(x => x.MediaId == mediaId, cancellationToken))
            return Fail<bool>("MEDIA_STATE_CONFLICT", "Detach the media before deleting it.");
        if (media.Status == MediaStatus.Deleting || media.Status == MediaStatus.Deleted) return ApiResponse<bool>.Ok(true);
        media.Status = MediaStatus.Deleting;
        media.DeletedAt = DateTime.UtcNow;
        media.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await _store.DeleteAsync(ObjectStoreBucket.PrivateSource, media.SourceKey, cancellationToken);
        var variants = ParseVariants(media.VariantsJson);
        var deliveryKeys = variants.Select(x => _urls.TryGetDeliveryKey(x.Url)).Where(x => x != null).Cast<string>().Distinct().ToArray();
        if (deliveryKeys.Length > 0)
        {
            foreach (var key in deliveryKeys) await _store.DeleteAsync(ObjectStoreBucket.PublicDelivery, key, cancellationToken);
            media.DeliveryPurgeStatus = DeliveryPurgeStatus.Pending;
            media.DeliveryPurgeNextAttemptAt = DateTime.UtcNow;
            media.DeliveryPurgeErrorCode = "DERIVATIVE_PURGE_REQUIRED";
            await _db.SaveChangesAsync(cancellationToken);
            return ApiResponse<bool>.Ok(true);
        }
        media.Status = MediaStatus.Deleted;
        await _db.SaveChangesAsync(cancellationToken);
        return ApiResponse<bool>.Ok(true);
    }
}

