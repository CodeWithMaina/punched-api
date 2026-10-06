using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Modules;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

public partial class LandingPageService
{
    public async Task<ApiResponse<PublicLandingPageResponse>> GetPublicAsync(Guid businessId)
    {
        if (TenantBusinessId is Guid activeTenantId && businessId != activeTenantId)
            return ApiResponse<PublicLandingPageResponse>.Fail("NOT_FOUND", "Business not found.");

        try
        {
            var business = await _context.Businesses
                .AsNoTracking()
                .Include(b => b.LoyaltyPrograms)
                .Include(b => b.ReferralProgram)
                .FirstOrDefaultAsync(b => b.Id == businessId && !b.IsDeleted);
            if (business == null)
                return ApiResponse<PublicLandingPageResponse>.Fail("NOT_FOUND", "Business not found.");

            var row = await _context.BusinessLandingPageConfigs
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.BusinessId == businessId);
            var config = LandingPageDefaults.Merge(row == null ? null : ParseConfig(row.ConfigJson));

            var moduleKeys = await _moduleEntitlementService.GetEffectiveModuleKeysAsync(businessId);
            var activeProgram = business.LoyaltyPrograms
                .Where(p => p.IsActive && p.Status == ProgramStatus.Active)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefault();
            var hasReferral = business.ReferralProgram is { IsActive: true };
            var capabilities = CustomerCapabilityCatalog.Resolve(
                moduleKeys,
                hasActiveLoyaltyProgram: activeProgram != null,
                hasActiveReferralProgram: hasReferral);

            Guid? heroMediaId = row?.HeroBackgroundMediaId ?? business.CoverMediaId;
            var heroVariants = await GetReadyCoverVariantsAsync(businessId, heroMediaId);
            if (heroVariants.Count == 0) heroMediaId = null;

            var response = new PublicLandingPageResponse
            {
                BusinessId = businessId,
                HeroTitle = PickTitle(config, business),
                HeroTagline = PickTagline(config, business),
                HeroDescription = PickDescription(config, business),
                ShowLogo = config.Hero.ShowLogo,
                HeroBackgroundMediaId = heroMediaId,
                HeroBackgroundVariants = heroVariants,
                Navigation = FilterNavigation(config.Navigation, capabilities),
                Sections = FilterSections(config.Sections, capabilities),
                PrimaryCta = config.PrimaryCta,
                SecondaryCta = config.SecondaryCta,
            };
            return ApiResponse<PublicLandingPageResponse>.Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading public landing page for business {BusinessId}", businessId);
            return ApiResponse<PublicLandingPageResponse>.Fail("LOAD_FAILED", "Failed to load landing page.");
        }
    }

    private static string PickTitle(LandingPageConfigDto config, Business business) =>
        string.IsNullOrWhiteSpace(config.Hero.TitleOverride) ? business.Name : config.Hero.TitleOverride!.Trim();

    private static string PickTagline(LandingPageConfigDto config, Business business)
    {
        if (!string.IsNullOrWhiteSpace(config.Hero.TaglineOverride)) return config.Hero.TaglineOverride!.Trim();
        return FirstSentence(business.Description) ?? $"{business.Category} in {business.Location}";
    }

    private static string? PickDescription(LandingPageConfigDto config, Business business) =>
        string.IsNullOrWhiteSpace(config.Hero.DescriptionOverride) ? business.Description : config.Hero.DescriptionOverride!.Trim();
}
