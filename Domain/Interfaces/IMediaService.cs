using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

public interface IMediaService
{
    Task<ApiResponse<MediaUploadGrantResponse>> CreateUploadAsync(Guid userId, CreateMediaUploadRequest request, string idempotencyKey, CancellationToken cancellationToken);
    Task<ApiResponse<MediaUploadGrantResponse>> RegrantAsync(Guid userId, Guid mediaId, CancellationToken cancellationToken);
    Task<ApiResponse<MediaResponse>> CompleteAsync(Guid userId, Guid mediaId, string idempotencyKey, CancellationToken cancellationToken);
    Task<ApiResponse<MediaResponse>> GetAsync(Guid userId, Guid mediaId, CancellationToken cancellationToken);
    Task<ApiResponse<MediaResponse>> RetryAsync(Guid userId, Guid mediaId, CancellationToken cancellationToken);
    Task<ApiResponse<bool>> DeleteAsync(Guid userId, Guid mediaId, CancellationToken cancellationToken);
    Task<ApiResponse<MediaResponse>> AssignBusinessFieldAsync(Guid userId, string field, Guid mediaId, CancellationToken cancellationToken);
    Task<ApiResponse<MediaResponse>> AssignUserAvatarAsync(Guid userId, Guid mediaId, CancellationToken cancellationToken);
    Task<ApiResponse<BusinessMediaResponse>> AttachGalleryAsync(Guid userId, Guid mediaId, int? sortOrder, bool featured, CancellationToken cancellationToken);
    Task<ApiResponse<List<BusinessMediaResponse>>> GetBusinessGalleryAsync(Guid userId, CancellationToken cancellationToken);
    Task<ApiResponse<ServiceMediaResponse>> AttachServiceAsync(Guid userId, Guid serviceId, Guid mediaId, int sortOrder, CancellationToken cancellationToken);
    Task<ApiResponse<LoyaltyProgramMediaResponse>> AttachProgramAsync(Guid userId, Guid programId, Guid mediaId, int sortOrder, CancellationToken cancellationToken);
    Task<ApiResponse<ReviewMediaResponse>> AttachReviewAsync(Guid userId, Guid reviewId, Guid mediaId, int sortOrder, CancellationToken cancellationToken);
    Task<ApiResponse<bool>> DetachRelationshipAsync(Guid userId, string relationship, Guid targetId, Guid mediaId, CancellationToken cancellationToken);
    Task<ApiResponse<bool>> ReorderGalleryAsync(Guid userId, IReadOnlyList<Guid> mediaIds, CancellationToken cancellationToken);
    Task<ApiResponse<MediaResponse>> GetPublicProjectionAsync(Guid mediaId, CancellationToken cancellationToken);
    Task<ApiResponse<bool>> MarkReviewMediaPurgeRequiredAsync(Guid reviewId, CancellationToken cancellationToken);
}
