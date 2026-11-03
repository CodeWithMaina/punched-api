using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Modules;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Public gallery read — see <see cref="IStorefrontGalleryService"/>.
///
/// <para><b>What makes an image public:</b> the owner attaching it to the
/// business gallery (<c>business_media</c>), the media row belonging to the same
/// business, being for the <c>business-gallery</c> purpose, and having finished
/// processing (<see cref="MediaStatus.Ready"/>). A media row that was uploaded
/// but never attached, or that belongs to another business, is unreachable
/// here — there is no way to ask for it.</para>
///
/// <para><b>Storage is never exposed:</b> only the processed variant URLs the
/// pipeline already produced are returned. The object-store source key, the
/// provider and any credential stay server-side.</para>
/// </summary>
public sealed class StorefrontGalleryService : IStorefrontGalleryService
{
    private const string ModuleKey = "gallery";

    private readonly ApplicationDbContext _db;
    private readonly IModuleEntitlementService _moduleEntitlements;
    private readonly ILogger<StorefrontGalleryService> _logger;
    private readonly ITenantContext? _tenant;

    public StorefrontGalleryService(
        ApplicationDbContext db,
        IModuleEntitlementService moduleEntitlements,
        ILogger<StorefrontGalleryService> logger,
        ITenantContext? tenant = null)
    {
        _db = db;
        _moduleEntitlements = moduleEntitlements;
        _logger = logger;
        _tenant = tenant;
    }

    /// <summary>Active tenant business id, or null on the platform root.</summary>
    private Guid? TenantBusinessId => _tenant?.IsActive == true ? _tenant.BusinessId : null;

    public async Task<ApiResponse<PublicGalleryResponse>> GetPublicGalleryAsync(
        Guid businessId, CancellationToken cancellationToken = default)
    {
        if (businessId == Guid.Empty)
            return ApiResponse<PublicGalleryResponse>.Fail("NOT_FOUND", "Business not found.");

        // Tenant narrowing: on a tenant host only the active tenant's gallery is
        // exposed, so a subdomain cannot enumerate another business's photos.
        if (TenantBusinessId is Guid activeTenantId && businessId != activeTenantId)
            return ApiResponse<PublicGalleryResponse>.Fail("NOT_FOUND", "Business not found.");

        try
        {
            var business = await _db.Businesses.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == businessId && !b.IsDeleted, cancellationToken);
            if (business == null)
                return ApiResponse<PublicGalleryResponse>.Fail("NOT_FOUND", "Business not found.");

            // The gallery module owns whether there is a gallery at all. Same
            // effective-entitlement lookup the owner surfaces use, applied to the
            // business the request named (an anonymous caller has no token for
            // [RequireModule] to read).
            if (!await _moduleEntitlements.IsModuleEnabledAsync(businessId, ModuleKey))
            {
                _logger.LogInformation(
                    "Public gallery request for business {BusinessId} refused: module '{ModuleKey}' is not enabled.",
                    businessId, ModuleKey);
                return ApiResponse<PublicGalleryResponse>.Fail(
                    "MODULE_DISABLED", $"The '{ModuleKey}' module is not enabled for this business.");
            }

            var rows = await (from relationship in _db.BusinessMedia.AsNoTracking()
                              join media in _db.Media.AsNoTracking() on relationship.MediaId equals media.Id
                              where relationship.BusinessId == businessId
                                    // Published only: an image the owner has
                                    // attached but hidden is not public, even
                                    // though its media row is ready.
                                    && relationship.IsVisible
                                    && media.BusinessId == businessId
                                    && media.Purpose == MediaPurposes.BusinessGallery
                                    && media.Status == MediaStatus.Ready
                              orderby relationship.SortOrder, relationship.CreatedAt
                              select new { relationship, media }).ToListAsync(cancellationToken);

            var images = rows.Select(item => new PublicGalleryImageResponse
            {
                MediaId = item.relationship.MediaId,
                Caption = item.relationship.Caption,
                SortOrder = item.relationship.SortOrder,
                Featured = item.relationship.IsFeatured,
                Width = item.media.Width,
                Height = item.media.Height,
                Variants = ParsePublicVariants(item.media.VariantsJson)
            }).ToList();

            return ApiResponse<PublicGalleryResponse>.Ok(new PublicGalleryResponse
            {
                BusinessId = businessId,
                Images = images
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error loading public gallery for business {BusinessId}", businessId);
            return ApiResponse<PublicGalleryResponse>.Fail("LOAD_FAILED", "Failed to load the gallery.");
        }
    }

    /// <summary>
    /// The ready variants a browser can actually render. Anything else (a
    /// processing thumbnail, an unknown format) is dropped rather than handed to
    /// the client as a URL it cannot use.
    /// </summary>
    private static IReadOnlyList<MediaVariantResponse> ParsePublicVariants(string? variantsJson)
    {
        if (string.IsNullOrWhiteSpace(variantsJson)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<MediaVariantResponse>>(variantsJson) ?? [])
                .Where(variant => !string.IsNullOrWhiteSpace(variant.Url)
                                  && variant.Width > 0
                                  && variant.Format is "webp" or "jpeg")
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
