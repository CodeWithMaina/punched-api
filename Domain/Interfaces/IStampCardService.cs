using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Stamp card + card design management for business owners.
/// Stamp cards are children of loyalty programs (campaigns); card designs are
/// reusable HTML templates owned by the business.
/// </summary>
public interface IStampCardService
{
    // Stamp cards (children of campaigns)
    Task<ApiResponse<List<StampCardResponse>>> GetProgramStampCardsAsync(Guid ownerId, Guid programId);
    Task<ApiResponse<StampCardResponse>> GetStampCardAsync(Guid ownerId, Guid stampCardId);
    Task<ApiResponse<StampCardResponse>> CreateStampCardAsync(Guid ownerId, Guid programId, CreateStampCardRequest request);
    Task<ApiResponse<StampCardResponse>> UpdateStampCardAsync(Guid ownerId, Guid stampCardId, UpdateStampCardRequest request);
    Task<ApiResponse<StampCardResponse>> UpdateStampCardStatusAsync(Guid ownerId, Guid stampCardId, UpdateStampCardStatusRequest request);
    Task<ApiResponse<StampCardResponse>> DuplicateStampCardAsync(Guid ownerId, Guid stampCardId, DuplicateStampCardRequest? request);
    Task<ApiResponse<bool>> DeleteStampCardAsync(Guid ownerId, Guid stampCardId);

    // Card designs (reusable per business)
    Task<ApiResponse<List<CardDesignResponse>>> GetCardDesignsAsync(Guid ownerId);
    Task<ApiResponse<CardDesignResponse>> GetCardDesignAsync(Guid ownerId, Guid designId);
    Task<ApiResponse<CardDesignResponse>> CreateCardDesignAsync(Guid ownerId, CreateCardDesignRequest request);
    Task<ApiResponse<CardDesignResponse>> UpdateCardDesignAsync(Guid ownerId, Guid designId, UpdateCardDesignRequest request);
    Task<ApiResponse<bool>> DeleteCardDesignAsync(Guid ownerId, Guid designId);

    // Preview (shared rendering pipeline)
    Task<ApiResponse<PreviewCardDesignResponse>> PreviewCardDesignAsync(Guid ownerId, PreviewCardDesignRequest request);
}