using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// The Storefront Builder: owner-scoped draft/published configuration plus the
/// public, sanitized projection of the published storefront.
///
/// <para>Owner writes are tenant-scoped (the business is derived from the caller's
/// membership, never a client-supplied id) and media-ownership validated. The
/// public read is anonymous and narrows to the business named in the request.</para>
/// </summary>
public interface IStorefrontBuilderService
{
    /// <summary>Owner builder state: draft + published + publication metadata.</summary>
    Task<ApiResponse<StorefrontBuilderStateResponse>> GetForOwnerAsync(Guid ownerId);

    /// <summary>Replaces the owner's working draft. Never touches the published config.</summary>
    Task<ApiResponse<StorefrontBuilderStateResponse>> SaveDraftAsync(Guid ownerId, SaveStorefrontDraftRequest request);

    /// <summary>Publishes the supplied config (or the current draft when omitted).</summary>
    Task<ApiResponse<StorefrontBuilderStateResponse>> PublishAsync(Guid ownerId, PublishStorefrontRequest request);

    /// <summary>Takes the storefront offline without deleting the configuration.</summary>
    Task<ApiResponse<StorefrontBuilderStateResponse>> UnpublishAsync(Guid ownerId);

    /// <summary>Discards the draft, restoring it from the published configuration.</summary>
    Task<ApiResponse<StorefrontBuilderStateResponse>> ResetDraftAsync(Guid ownerId);

    /// <summary>Applies a template's design to the draft (content preserved).</summary>
    Task<ApiResponse<StorefrontBuilderStateResponse>> ApplyTemplateAsync(Guid ownerId, string templateId);

    /// <summary>
    /// The published storefront for a business, or null when it has none / is
    /// unpublished. Used by the public landing-page and bootstrap projections.
    /// </summary>
    Task<PublicStorefrontDto?> GetPublishedForBusinessAsync(Guid businessId, CancellationToken cancellationToken = default);

    /// <summary>Anonymous public read of the published storefront.</summary>
    Task<ApiResponse<PublicStorefrontDto>> GetPublicAsync(Guid businessId, CancellationToken cancellationToken = default);
}
