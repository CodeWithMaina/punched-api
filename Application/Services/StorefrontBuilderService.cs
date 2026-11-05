using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Modules;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Storefront Builder owner operations. The draft is the working copy the editor
/// mutates; the published config is what the public storefront renders. A save
/// only ever writes the draft, and a publish copies draft → published atomically,
/// so a failed save or publish can never corrupt the live storefront. The business
/// is always derived from the caller's membership, never a client-supplied id.
/// </summary>
public partial class StorefrontBuilderService : IStorefrontBuilderService
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ApplicationDbContext _context;
    private readonly IBusinessScopeResolver _businessScopeResolver;
    private readonly IModuleEntitlementService? _moduleEntitlementService;
    private readonly ITenantContext? _tenant;
    private readonly ILogger<StorefrontBuilderService> _logger;

    public StorefrontBuilderService(
        ApplicationDbContext context,
        IBusinessScopeResolver businessScopeResolver,
        ILogger<StorefrontBuilderService> logger,
        ITenantContext? tenant = null,
        IModuleEntitlementService? moduleEntitlementService = null)
    {
        _context = context;
        _businessScopeResolver = businessScopeResolver;
        _logger = logger;
        _tenant = tenant;
        _moduleEntitlementService = moduleEntitlementService;
    }

    private Guid? TenantBusinessId => _tenant?.IsActive == true ? _tenant.BusinessId : null;

    public async Task<ApiResponse<StorefrontBuilderStateResponse>> GetForOwnerAsync(Guid ownerId)
    {
        var businessId = await _businessScopeResolver.GetOwnedBusinessIdAsync(ownerId);
        if (businessId == null)
            return ApiResponse<StorefrontBuilderStateResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var row = await _context.BusinessLandingPageConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.BusinessId == businessId.Value);

        return ApiResponse<StorefrontBuilderStateResponse>.Ok(BuildState(businessId.Value, row));
    }

    public async Task<ApiResponse<StorefrontBuilderStateResponse>> SaveDraftAsync(Guid ownerId, SaveStorefrontDraftRequest request)
    {
        var businessId = await _businessScopeResolver.GetOwnedBusinessIdAsync(ownerId);
        if (businessId == null)
            return ApiResponse<StorefrontBuilderStateResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var config = StorefrontBuilderDefaults.Merge(request.Config);
        var mediaError = await ValidateMediaAsync(businessId.Value, config);
        if (mediaError != null)
            return ApiResponse<StorefrontBuilderStateResponse>.Fail("MEDIA_NOT_FOUND", mediaError);

        var row = await _context.BusinessLandingPageConfigs
            .FirstOrDefaultAsync(x => x.BusinessId == businessId.Value);

        if (row == null)
        {
            row = new BusinessLandingPageConfig
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId.Value,
                Version = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ConfigJson = "{}",
            };
            _context.BusinessLandingPageConfigs.Add(row);
        }
        else
        {
            row.Version++;
            row.UpdatedAt = DateTime.UtcNow;
        }

        row.TemplateId = config.TemplateId;
        row.DraftConfigJson = JsonSerializer.Serialize(config, JsonOptions);
        row.HeroBackgroundMediaId = config.Hero.BackgroundMediaId ?? row.HeroBackgroundMediaId;

        await _context.SaveChangesAsync();
        return ApiResponse<StorefrontBuilderStateResponse>.Ok(BuildState(businessId.Value, row));
    }

    public async Task<ApiResponse<StorefrontBuilderStateResponse>> PublishAsync(Guid ownerId, PublishStorefrontRequest request)
    {
        var businessId = await _businessScopeResolver.GetOwnedBusinessIdAsync(ownerId);
        if (businessId == null)
            return ApiResponse<StorefrontBuilderStateResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var row = await _context.BusinessLandingPageConfigs
            .FirstOrDefaultAsync(x => x.BusinessId == businessId.Value);
        if (row == null)
            return ApiResponse<StorefrontBuilderStateResponse>.Fail("NOT_FOUND", "Save a draft before publishing.");

        var source = request.Config != null
            ? StorefrontBuilderDefaults.Merge(request.Config)
            : StorefrontBuilderDefaults.Merge(ParseConfig(row.DraftConfigJson));

        var mediaError = await ValidateMediaAsync(businessId.Value, source);
        if (mediaError != null)
            return ApiResponse<StorefrontBuilderStateResponse>.Fail("MEDIA_NOT_FOUND", mediaError);

        // Publish is the only write that touches the live config, and it writes the
        // whole validated payload in one SaveChanges — a failure leaves the
        // previously published storefront untouched.
        row.DraftConfigJson = JsonSerializer.Serialize(source, JsonOptions);
        row.PublishedConfigJson = JsonSerializer.Serialize(source, JsonOptions);
        row.TemplateId = source.TemplateId;
        row.HeroBackgroundMediaId = source.Hero.BackgroundMediaId ?? row.HeroBackgroundMediaId;
        row.IsPublished = true;
        row.PublishedAt = DateTime.UtcNow;
        row.PublishedVersion = row.Version + 1;
        row.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return ApiResponse<StorefrontBuilderStateResponse>.Ok(BuildState(businessId.Value, row));
    }
}
