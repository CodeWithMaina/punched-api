using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Controlled landing-page configuration (presentation/visibility/ordering only).
/// Absence of a row means platform defaults — existing businesses keep working.
/// </summary>
public interface ILandingPageService
{
    /// <summary>Owner view: merged defaults + stored overrides, with version.</summary>
    Task<ApiResponse<LandingPageConfigResponse>> GetForOwnerAsync(Guid ownerId);

    /// <summary>Owner save: validates, checks version + media ownership, persists.</summary>
    Task<ApiResponse<LandingPageConfigResponse>> UpdateForOwnerAsync(Guid ownerId, UpdateLandingPageRequest request);

    /// <summary>Anonymous sanitized projection, capability-filtered.</summary>
    Task<ApiResponse<PublicLandingPageResponse>> GetPublicAsync(Guid businessId);
}
