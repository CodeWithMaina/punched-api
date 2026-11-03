using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Public, anonymous read of a business's published photo gallery.
///
/// <para>Owner-side gallery management already exists
/// (<see cref="IMediaService.AttachGalleryAsync"/> etc.). This is the
/// customer-facing read: it resolves the business's ordered gallery media into
/// ready image variants, and it is deliberately the only place a public visitor
/// can learn a gallery image's URLs — the browser never builds a storage URL
/// itself.</para>
/// </summary>
public interface IStorefrontGalleryService
{
    /// <summary>
    /// The business's published gallery, ordered for display.
    ///
    /// <para>Returns <c>NOT_FOUND</c> for an unknown/deleted business or a
    /// business that is not the active tenant, and <c>MODULE_DISABLED</c> when
    /// the business has no <c>gallery</c> entitlement — so the storefront can
    /// tell "no gallery" from "no such business".</para>
    /// </summary>
    Task<ApiResponse<PublicGalleryResponse>> GetPublicGalleryAsync(
        Guid businessId, CancellationToken cancellationToken = default);
}
