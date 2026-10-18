
using System.Text.Json;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Modules;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

public partial class BusinessService
{
    private static readonly JsonSerializerOptions BootstrapJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ApiResponse<StorefrontBootstrapResponse>> GetStorefrontBootstrapBySlugAsync(string slug)
    {
        var normalized = (slug ?? string.Empty).Trim().ToLowerInvariant();
        if (!BusinessSlugPolicy.IsValidFormat(normalized) || BusinessSlugPolicy.IsReserved(normalized))
            return ApiResponse<StorefrontBootstrapResponse>.Fail("SLUG_INVALID", "That business address is not valid.");

        // Parses the stored landing-page JSON config from the database into the
        // structured LandingPageConfigDto. Never throws — degraded config is
        // silently ignored and defaults from LandingPageDefaults are used instead.
        LandingPageConfigDto? ParseBootstrapLandingConfig(string? rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return null;
            try { return JsonSerializer.Deserialize<LandingPageConfigDto>(rawJson, BootstrapJsonOptions); }
            catch (JsonException) { return null; }
        }

            var live = await _context.Businesses.AsNoTracking()
                .Where(b => b.Slug == normalized && !b.IsDeleted)
                .Select(b => new { b.Id, Slug = b.Slug! })
                .FirstOrDefaultAsync();
            Guid businessId; string canonicalSlug; bool moved;
            if (live != null) { businessId = live.Id; canonicalSlug = live.Slug; moved = false; }
            else
            {
                var history = await (
                    from h in _context.BusinessSlugHistories.AsNoTracking()
                    join b in _context.Businesses.AsNoTracking() on h.BusinessId equals b.Id
                    where h.Slug == normalized && !b.IsDeleted
                    select new { h.BusinessId, Slug = b.Slug! }).FirstOrDefaultAsync();
                if (history == null || string.IsNullOrEmpty(history.Slug))
                    return ApiResponse<StorefrontBootstrapResponse>.Fail("NOT_FOUND", "Business not found.");
                businessId = history.BusinessId; canonicalSlug = history.Slug; moved = true;
            }
            if (TenantBusinessId is Guid activeTenantId && businessId != activeTenantId)
                return ApiResponse<StorefrontBootstrapResponse>.Fail("NOT_FOUND", "Business not found.");
            var business = await _context.Businesses.AsNoTracking()
                .Where(b => b.Id == businessId && !b.IsDeleted)
                .Select(b => new
                {
                    b.Id, b.Slug, b.Name, b.Category, b.Location, b.Description,
                    b.LogoUrl, b.LogoMediaId, b.CoverMediaId, b.PhoneNumber, b.Email,
                    ActiveProgram = b.LoyaltyPrograms
                        .Where(p => p.IsActive && p.Status == ProgramStatus.Active)
                        .OrderByDescending(p => p.CreatedAt)
                        .Select(p => new { p.Id, p.Name, p.StampsRequired, p.RewardDescription, p.DefaultEnrollmentStamps })
                        .FirstOrDefault(),
                    HasReferral = b.ReferralProgram != null && b.ReferralProgram.IsActive,
                }).FirstOrDefaultAsync();
            if (business == null)
                return ApiResponse<StorefrontBootstrapResponse>.Fail("NOT_FOUND", "Business not found.");
            var moduleKeys = await _moduleEntitlementService.GetEffectiveModuleKeysAsync(businessId);
            var capabilities = CustomerCapabilityCatalog.Resolve(moduleKeys,
                hasActiveLoyaltyProgram: business.ActiveProgram != null,
                hasActiveReferralProgram: business.HasReferral);
            var galleryMediaIds = await _context.BusinessMedia.AsNoTracking()
                .Where(x => x.BusinessId == businessId).Select(x => x.MediaId).ToListAsync();
            var readyGalleryIds = galleryMediaIds.Count == 0 ? new List<Guid>()
                : await _context.Media.AsNoTracking()
                    .Where(m => galleryMediaIds.Contains(m.Id) && m.Status == MediaStatus.Ready)
                    .Select(m => m.Id).ToListAsync();
            var landingRow = await _context.BusinessLandingPageConfigs.AsNoTracking()
                .Where(x => x.BusinessId == businessId)
                .Select(x => new { x.HeroBackgroundMediaId, x.ConfigJson }).FirstOrDefaultAsync();
            var logoMediaId = business.LogoMediaId;
            var heroMediaId = landingRow?.HeroBackgroundMediaId ?? business.CoverMediaId;
            string? logoVariantsJson = null; string? heroVariantsJson = null;
            if (logoMediaId.HasValue || heroMediaId.HasValue)
            {
                var ids = new List<Guid>();
                if (logoMediaId.HasValue) ids.Add(logoMediaId.Value);
                if (heroMediaId.HasValue && heroMediaId != logoMediaId) ids.Add(heroMediaId.Value);
                var rows = await _context.Media.AsNoTracking()
                    .Where(m => ids.Contains(m.Id) && m.BusinessId == businessId &&
                        m.Status == MediaStatus.Ready && m.Visibility == MediaVisibility.Public &&
                        (m.Purpose == MediaPurposes.BusinessLogo || m.Purpose == MediaPurposes.BusinessCover))
                    .Select(m => new { m.Id, m.Purpose, m.VariantsJson }).ToListAsync();
                if (logoMediaId.HasValue)
                    logoVariantsJson = rows.FirstOrDefault(r => r.Id == logoMediaId.Value)?.VariantsJson;
                if (heroMediaId.HasValue)
                    heroVariantsJson = rows.FirstOrDefault(r => r.Id == heroMediaId.Value)?.VariantsJson;
            }
            if (string.IsNullOrWhiteSpace(heroVariantsJson)) heroMediaId = null;
            var config = LandingPageDefaults.Merge(
                landingRow == null ? null : ParseBootstrapLandingConfig(landingRow.ConfigJson));

            return ApiResponse<StorefrontBootstrapResponse>.Ok(new StorefrontBootstrapResponse
            {
                BusinessId = businessId,
                Slug = canonicalSlug,
                Moved = moved,
                Business = new PublicBusinessProfileResponse
                {
                    Id = business.Id,
                    Name = business.Name,
                    Category = business.Category,
                    Location = business.Location,
                    Description = business.Description,
                    LogoUrl = business.LogoUrl,
                    PhoneNumber = business.PhoneNumber,
                    Email = business.Email,
                    LoyaltyProgram = business.ActiveProgram != null
                        ? new PublicLoyaltyProgramSummary
                        {
                            Name = business.ActiveProgram.Name,
                            StampsRequired = business.ActiveProgram.StampsRequired,
                            RewardDescription = business.ActiveProgram.RewardDescription,
                            DefaultEnrollmentStamps = business.ActiveProgram.DefaultEnrollmentStamps,
                        }
                        : null,
                    HasReferralProgram = business.HasReferral,
                    Capabilities = capabilities.ToDictionary(k => k.Key, k => k.Value),
                },
                LandingPage = new PublicLandingPageResponse
                {
                    BusinessId = businessId,
                    HeroTitle = config.Hero.TitleOverride ?? business.Name,
                    HeroTagline = config.Hero.TaglineOverride ?? business.Description ?? string.Empty,
                    HeroDescription = config.Hero.DescriptionOverride,
                    ShowLogo = config.Hero.ShowLogo,
                    HeroBackgroundMediaId = heroMediaId,
                    HeroBackgroundVariants = new List<ServiceImageVariantResponse>(),
                    Navigation = new Dictionary<string, bool>
                    {
                        ["services"] = config.Navigation.Services,
                        ["loyalty"] = config.Navigation.Loyalty,
                        ["about"] = config.Navigation.About,
                        ["reviews"] = config.Navigation.Reviews,
                        ["contact"] = config.Navigation.Contact,
                        ["gallery"] = config.Navigation.Gallery,
                        ["booking"] = config.Navigation.Booking,
                    },
                    Sections = config.Sections
                        .Where(kv => kv.Value != null && kv.Value.Enabled)
                        .Select(kv => new PublicLandingPageSection
                        {
                            Key = kv.Key,
                            Order = kv.Value.Order,
                            Title = kv.Value.Title,
                            Description = kv.Value.Description,
                        })
                        .ToList(),
                    PrimaryCta = config.PrimaryCta,
                    SecondaryCta = config.SecondaryCta,
                },
            });
    }

    /// <summary>
    /// Canonical owner-shell tab order. Pruned to the modules this business
    /// actually has access to; enabled modules missing from the list are
    /// appended so a new catalog key can never silently vanish from navigation.
    /// </summary>
    private static readonly string[] ShellModuleOrder =
    {
        "appointments", "customers", "stamps", "loyalty", "programs", "rewards",
        "referral", "staff", "serviceCatalog", "attendance", "payments",
        "reviews", "gallery", "storefront", "analytics", "notifications",
        "customCardDesign", "settings",
    };

    /// <summary>
    /// Owner-dashboard bootstrap: ONE call returning the minimum data every
    /// dashboard shell render needs — business identity + program flags,
    /// effective module entitlements, owner-role permissions, plan status,
    /// navigation tabs and reminder settings — so cold dashboard loads stop
    /// fanning out to /businesses/me and /me/modules independently.
    /// Resolved entirely server-side from the authenticated owner (the client
    /// never supplies a business id).
    /// </summary>
    public async Task<ApiResponse<DashboardBootstrapResponse>> GetDashboardBootstrapAsync(Guid ownerId)
    {
        // Owner → business mapping is memory-cached (60 s positive TTL), so the
        // hot path pays no extra DB round trip purely for tenant resolution.
        var businessId = await _businessScopeResolver.GetOwnedBusinessIdAsync(ownerId);
        if (businessId == null)
            return ApiResponse<DashboardBootstrapResponse>.Fail("NOT_FOUND", "No business found for this account.");

        // Single projected, no-tracking query: identity + program flags +
        // reminder policy (optional navigation = LEFT JOIN). Read-only shell.
        var row = await _context.Businesses
            .AsNoTracking()
            .Where(b => b.Id == businessId.Value && !b.IsDeleted)
            .Select(b => new
            {
                b.Id, b.Name, b.Category, b.Location, b.Description, b.LogoUrl,
                b.PhoneNumber, b.Email, b.Slug,
                HasLoyalty = b.LoyaltyPrograms.Any(p => p.IsActive && p.Status == ProgramStatus.Active),
                HasReferral = b.ReferralProgram != null && b.ReferralProgram.IsActive,
                PolicyEnabled = b.ReminderPolicy == null ? (bool?)null : b.ReminderPolicy.Enabled,
                PolicyOffsetsJson = b.ReminderPolicy == null ? null : b.ReminderPolicy.OffsetMinutesJson,
                PolicyChannelsJson = b.ReminderPolicy == null ? null : b.ReminderPolicy.ChannelsJson,
            })
            .FirstOrDefaultAsync();
        if (row == null)
            return ApiResponse<DashboardBootstrapResponse>.Fail("NOT_FOUND", "Business not found.");

        // Entitlements are cached per business (60 s) inside the entitlement
        // service; HasAccess mirrors exactly what [RequireModule] enforces, so
        // the shell can gate widgets on the same answer the API will give.
        var entitlements = await _moduleEntitlementService.GetBusinessModulesAsync(businessId.Value, ownerId);
        var enabledModules = entitlements.Modules
            .Where(m => m.HasAccess)
            .Select(m => m.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        // Owner-role permission codes from the static matrix (no I/O).
        var permissions = PermissionMatrix.PermissionsForRole("Business")
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var tabs = ShellModuleOrder.Where(enabledModules.Contains).ToList();
        tabs.AddRange(enabledModules.Where(k => !tabs.Contains(k)));

        return ApiResponse<DashboardBootstrapResponse>.Ok(new DashboardBootstrapResponse
        {
            Business = new DashboardBusinessBootstrap
            {
                Id = row.Id,
                Name = row.Name,
                Category = row.Category,
                Location = row.Location,
                Description = row.Description,
                LogoUrl = row.LogoUrl,
                PhoneNumber = row.PhoneNumber,
                Email = row.Email,
                Slug = row.Slug ?? string.Empty,
                HasLoyaltyProgram = row.HasLoyalty,
                HasReferralProgram = row.HasReferral,
            },
            ModuleKeys = enabledModules,
            PermissionCodes = permissions,
            Plan = entitlements.CurrentPlan == null
                ? null
                : new CallerPlanInfo
                {
                    Key = entitlements.CurrentPlan.Key,
                    Name = entitlements.CurrentPlan.Name,
                    Status = entitlements.SubscriptionStatus ?? string.Empty,
                    EndsAt = entitlements.SubscriptionEndsAt,
                },
            Navigation = new DashboardNavigationBootstrap
            {
                Tabs = tabs,
                EnabledModules = enabledModules,
                Permissions = permissions,
            },
            Settings = new BusinessSettingsBootstrap
            {
                // Absent policy row = platform defaults (see BusinessReminderPolicy).
                ReminderPolicyEnabled = row.PolicyEnabled ?? true,
                ReminderOffsetMinutes = row.PolicyOffsetsJson == null ? null : ParseOffsetMinutes(row.PolicyOffsetsJson),
                ReminderChannels = row.PolicyChannelsJson == null ? null : ParseChannels(row.PolicyChannelsJson),
            },
        });
    }
}
