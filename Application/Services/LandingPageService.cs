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
/// Controlled landing-page configuration: presentation/visibility/ordering/overrides.
/// No row means platform defaults (existing businesses keep working, no backfill).
/// Owner writes are version-guarded and media-ownership validated.
/// </summary>
public partial class LandingPageService : ILandingPageService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ApplicationDbContext _context;
    private readonly IBusinessScopeResolver _businessScopeResolver;
    private readonly IModuleEntitlementService _moduleEntitlementService;
    private readonly ITenantContext? _tenant;
    private readonly ILogger<LandingPageService> _logger;

    public LandingPageService(
        ApplicationDbContext context,
        IBusinessScopeResolver businessScopeResolver,
        IModuleEntitlementService moduleEntitlementService,
        ILogger<LandingPageService> logger,
        ITenantContext? tenant = null)
    {
        _context = context;
        _businessScopeResolver = businessScopeResolver;
        _moduleEntitlementService = moduleEntitlementService;
        _logger = logger;
        _tenant = tenant;
    }

    private Guid? TenantBusinessId => _tenant?.IsActive == true ? _tenant.BusinessId : null;

    public async Task<ApiResponse<LandingPageConfigResponse>> GetForOwnerAsync(Guid ownerId)
    {
        var businessId = await _businessScopeResolver.GetOwnedBusinessIdAsync(ownerId);
        if (businessId == null)
            return ApiResponse<LandingPageConfigResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var row = await _context.BusinessLandingPageConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.BusinessId == businessId.Value);

        if (row == null)
        {
            return ApiResponse<LandingPageConfigResponse>.Ok(new LandingPageConfigResponse
            {
                BusinessId = businessId.Value,
                Version = 0,
                IsDefault = true,
                UpdatedAt = null,
                Config = LandingPageDefaults.Create(),
            });
        }

        return ApiResponse<LandingPageConfigResponse>.Ok(new LandingPageConfigResponse
        {
            BusinessId = businessId.Value,
            Version = row.Version,
            IsDefault = false,
            UpdatedAt = row.UpdatedAt,
            Config = LandingPageDefaults.Merge(ParseConfig(row.ConfigJson)),
        });
    }

    public async Task<ApiResponse<LandingPageConfigResponse>> UpdateForOwnerAsync(Guid ownerId, UpdateLandingPageRequest request)
    {
        var businessId = await _businessScopeResolver.GetOwnedBusinessIdAsync(ownerId);
        if (businessId == null)
            return ApiResponse<LandingPageConfigResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var config = LandingPageDefaults.Merge(request.Config);

        Guid? heroMediaId = request.Config?.Hero?.BackgroundMediaId;
        if (heroMediaId.HasValue)
        {
            var media = await _context.Media.AsNoTracking().FirstOrDefaultAsync(m => m.Id == heroMediaId.Value);
            if (media == null || media.BusinessId != businessId.Value || media.Purpose != MediaPurposes.BusinessCover)
                return ApiResponse<LandingPageConfigResponse>.Fail("MEDIA_NOT_FOUND", "The hero image could not be found for this business.");
            if (media.Status != MediaStatus.Ready)
                return ApiResponse<LandingPageConfigResponse>.Fail("MEDIA_NOT_READY", "Only ready images can be used as the hero background.");
        }

        var row = await _context.BusinessLandingPageConfigs
            .FirstOrDefaultAsync(x => x.BusinessId == businessId.Value);

        if (row == null)
        {
            if (request.Version != 0)
                return ApiResponse<LandingPageConfigResponse>.Fail("STALE_VERSION", "This page was updated elsewhere. Reload and try again.");
            row = new BusinessLandingPageConfig
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId.Value,
                Version = 1,
                UpdatedAt = DateTime.UtcNow,
                HeroBackgroundMediaId = heroMediaId,
                ConfigJson = JsonSerializer.Serialize(config, JsonOptions),
                CreatedAt = DateTime.UtcNow,
            };
            _context.BusinessLandingPageConfigs.Add(row);
        }
        else
        {
            if (row.Version != request.Version)
                return ApiResponse<LandingPageConfigResponse>.Fail("STALE_VERSION", "This page was updated elsewhere. Reload and try again.");
            row.Version++;
            row.UpdatedAt = DateTime.UtcNow;
            row.HeroBackgroundMediaId = heroMediaId;
            row.ConfigJson = JsonSerializer.Serialize(config, JsonOptions);
        }

        await _context.SaveChangesAsync();

        return ApiResponse<LandingPageConfigResponse>.Ok(new LandingPageConfigResponse
        {
            BusinessId = businessId.Value,
            Version = row.Version,
            IsDefault = false,
            UpdatedAt = row.UpdatedAt,
            Config = config,
        });
    }
}
