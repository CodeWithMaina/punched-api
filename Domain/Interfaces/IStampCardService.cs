using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Stamp card management for business owners. Stamp cards are children of
/// loyalty programs.
///
/// Card design authoring lives in <see cref="ICardDesignService"/> (Admin only);
/// stamp cards may only reference a design that
/// <see cref="ICardDesignService.ValidateSelectionAsync"/> accepts.
/// </summary>
public interface IStampCardService
{
    // Stamp cards (children of loyalty programs)
    Task<ApiResponse<List<StampCardResponse>>> GetProgramStampCardsAsync(Guid ownerId, Guid programId);
    Task<ApiResponse<StampCardResponse>> GetStampCardAsync(Guid ownerId, Guid stampCardId);
    Task<ApiResponse<StampCardResponse>> CreateStampCardAsync(Guid ownerId, Guid programId, CreateStampCardRequest request);
    Task<ApiResponse<StampCardResponse>> UpdateStampCardAsync(Guid ownerId, Guid stampCardId, UpdateStampCardRequest request);
    Task<ApiResponse<StampCardResponse>> UpdateStampCardStatusAsync(Guid ownerId, Guid stampCardId, UpdateStampCardStatusRequest request);
    Task<ApiResponse<StampCardResponse>> DuplicateStampCardAsync(Guid ownerId, Guid stampCardId, DuplicateStampCardRequest? request);
    Task<ApiResponse<bool>> DeleteStampCardAsync(Guid ownerId, Guid stampCardId);
}