using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Modules;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

public partial class StorefrontBuilderService
{
    public async Task<PublicStorefrontDto?> GetPublishedForBusinessAsync(Guid businessId, CancellationToken cancellationToken = default)
    {
        // Tenant narrowing: a resolved tenant may only read its own business.
        if (TenantBusinessId is Guid activeTenantId && businessId != activeTenantId)
            return null;

        var row = await _context.BusinessLandingPageConfigs
            .AsNoTracking()
            .Where(x => x.BusinessId == businessId)
            .Select(x => new { x.IsPublished, x.PublishedConfigJson, x.PublishedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (row == null || !row.IsPublished || string.IsNullOrWhiteSpace(row.PublishedConfigJson))
            return null;

        var config = StorefrontBuilderDefaults.Merge(ParseConfig(row.PublishedConfigJson));

        // Capability filtering: never expose a section the business is not entitled
        // to, even if it is still in the stored configuration.
        var capabilities = await ResolveCapabilitiesAsync(businessId);
        if (capabilities != null)
        {
            config.Sections = config.Sections
                .Where(s => s.Enabled && StorefrontSectionCatalog.CapabilityAllows(s.Key, capabilities))
                .ToList();
        }
        else
        {
            config.Sections = config.Sections.Where(s => s.Enabled).ToList();
        }

        var dto = new PublicStorefrontDto
        {
            TemplateId = config.TemplateId,
            IsPublished = true,
            PublishedAt = row.PublishedAt,
            Theme = config.Theme,
            Header = config.Header,
            Hero = config.Hero,
            Sections = config.Sections,
            Footer = config.Footer,
            Pages = config.Pages,
            Seo = config.Seo,
        };

        await AttachVariantsAsync(businessId, dto, config, cancellationToken);
        return dto;
    }

    public async Task<ApiResponse<PublicStorefrontDto>> GetPublicAsync(Guid businessId, CancellationToken cancellationToken = default)
    {
        try
        {
            var dto = await GetPublishedForBusinessAsync(businessId, cancellationToken);
            if (dto == null)
                return ApiResponse<PublicStorefrontDto>.Fail("NOT_FOUND", "This storefront is not published.");
            return ApiResponse<PublicStorefrontDto>.Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading published storefront for business {BusinessId}", businessId);
            return ApiResponse<PublicStorefrontDto>.Fail("LOAD_FAILED", "Failed to load the storefront.");
        }
    }

    private async Task<IReadOnlyDictionary<string, bool>?> ResolveCapabilitiesAsync(Guid businessId)
    {
        if (_moduleEntitlementService == null) return null;
        var moduleKeys = await _moduleEntitlementService.GetEffectiveModuleKeysAsync(businessId);
        return CustomerCapabilityCatalog.Resolve(
            moduleKeys, hasActiveLoyaltyProgram: true, hasActiveReferralProgram: false);
    }

    private async Task AttachVariantsAsync(
        Guid businessId, PublicStorefrontDto dto, StorefrontConfigDto config, CancellationToken cancellationToken)
    {
        var ids = CollectMediaIds(config);
        if (ids.Count == 0) return;

        var rows = await _context.Media.AsNoTracking()
            .Where(m => ids.Contains(m.Id) && m.BusinessId == businessId &&
                        m.Status == MediaStatus.Ready && m.Visibility == MediaVisibility.Public)
            .Select(m => new { m.Id, m.VariantsJson })
            .ToListAsync(cancellationToken);

        var byId = rows.ToDictionary(r => r.Id, r => ParseVariants(r.VariantsJson));

        dto.ThemeBackgroundVariants = Variants(byId, config.Theme.BackgroundImageMediaId);
        dto.HeaderLogoVariants = Variants(byId, config.Header.LogoMediaId);
        dto.HeroBackgroundVariants = Variants(byId, config.Hero.BackgroundMediaId);
        dto.FooterLogoVariants = Variants(byId, config.Footer.LogoMediaId);
        dto.OgImageVariants = Variants(byId, config.Seo.OgImageMediaId);
        dto.FaviconVariants = Variants(byId, config.Seo.FaviconMediaId);
        dto.HeroCarouselVariants = config.Hero.CarouselMediaIds
            .Select(id => Variants(byId, id))
            .ToList();
    }

    private static List<ServiceImageVariantResponse> Variants(
        IReadOnlyDictionary<Guid, List<ServiceImageVariantResponse>> byId, Guid? mediaId)
        => mediaId.HasValue && byId.TryGetValue(mediaId.Value, out var list) ? list : new();

    private static List<ServiceImageVariantResponse> ParseVariants(string? variantsJson)
    {
        if (string.IsNullOrWhiteSpace(variantsJson)) return new();
        try
        {
            return (JsonSerializer.Deserialize<List<MediaVariantResponse>>(variantsJson, JsonOptions) ?? new())
                .Where(v => v.Format is "webp" or "jpeg")
                .Select(v => new ServiceImageVariantResponse { Url = v.Url, Width = v.Width, Height = v.Height, Format = v.Format })
                .ToList();
        }
        catch (JsonException) { return new(); }
    }
}
