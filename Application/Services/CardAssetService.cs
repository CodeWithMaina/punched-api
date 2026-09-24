using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Assets;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Loyalty;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Card branding asset lifecycle, plus the read/validation half of the contract.
///
/// Every method resolves the caller's business from the authenticated principal
/// via <see cref="ILoyaltyScopeResolver"/> — there is no code path in which a
/// client-supplied business, owner or asset identifier selects a tenant.
///
/// Authorization split (§10):
/// <list type="bullet">
/// <item><c>loyalty.manage</c> (Business owner only) — upload and soft-delete.</item>
/// <item><c>loyalty.stamp</c> (Business + Staff) — list, read metadata, stream content.</item>
/// </list>
/// Staff therefore get the artwork they need to serve customers without gaining
/// the ability to replace a business's branding.
/// </summary>
public partial class CardAssetService : ICardAssetService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ApplicationDbContext _context;
    private readonly ILoyaltyScopeResolver _scopeResolver;
    private readonly ICardAssetStorage _storage;
    private readonly CardAssetSettings _settings;
    private readonly ILogger<CardAssetService> _logger;

    public CardAssetService(
        IUnitOfWork unitOfWork,
        ApplicationDbContext context,
        ILoyaltyScopeResolver scopeResolver,
        ICardAssetStorage storage,
        IOptions<CardAssetSettings> settings,
        ILogger<CardAssetService> logger)
    {
        _unitOfWork = unitOfWork;
        _context = context;
        _scopeResolver = scopeResolver;
        _storage = storage;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ApiResponse<List<CardAssetResponse>>> ListAsync(Guid userId, string? purpose = null)
    {
        var scope = await _scopeResolver.ResolveAsync(userId, "loyalty.stamp");
        if (!scope.Success)
            return ApiResponse<List<CardAssetResponse>>.Fail(scope.ErrorCode!, scope.ErrorMessage!);

        var businessId = scope.Actor!.BusinessId;

        var query = _context.CardAssets
            .AsNoTracking()
            .Where(a => a.BusinessId == businessId && a.Status == CardAssetStatus.Active);

        if (!string.IsNullOrWhiteSpace(purpose))
        {
            var normalized = purpose.Trim().ToUpperInvariant();
            if (!CardAssetPurposes.IsSupported(normalized))
                return ApiResponse<List<CardAssetResponse>>.Fail(
                    "INVALID_ASSET_PURPOSE",
                    $"purpose must be one of: {string.Join(", ", CardAssetPurposes.All)}.");

            query = query.Where(a => a.Purpose == normalized);
        }

        var assets = await query
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();

        return ApiResponse<List<CardAssetResponse>>.Ok(assets.Select(Map).ToList());
    }

    /// <inheritdoc />
    public async Task<ApiResponse<CardAssetResponse>> GetAsync(Guid userId, Guid assetId)
    {
        var scope = await _scopeResolver.ResolveAsync(userId, "loyalty.stamp");
        if (!scope.Success)
            return ApiResponse<CardAssetResponse>.Fail(scope.ErrorCode!, scope.ErrorMessage!);

        var asset = await _context.CardAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == assetId && a.BusinessId == scope.Actor!.BusinessId);

        // Deliberately identical response for "does not exist" and "belongs to
        // another tenant": a distinct 403 would confirm the id exists (§20).
        if (asset == null)
            return ApiResponse<CardAssetResponse>.Fail("NOT_FOUND", "The asset could not be found.");

        return ApiResponse<CardAssetResponse>.Ok(Map(asset));
    }

    /// <inheritdoc />
    public async Task<ApiResponse<bool>> DeleteAsync(Guid userId, Guid assetId)
    {
        var scope = await _scopeResolver.ResolveAsync(userId, "loyalty.manage");
        if (!scope.Success)
            return ApiResponse<bool>.Fail(scope.ErrorCode!, scope.ErrorMessage!);

        var businessId = scope.Actor!.BusinessId;

        var asset = await _context.CardAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.BusinessId == businessId && a.Status == CardAssetStatus.Active);

        if (asset == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "The asset could not be found.");

        // Soft delete only. The payload and row are retained so every design that
        // already references this asset keeps rendering (§17) — the asset simply
        // stops being offered for new selections.
        asset.Status = CardAssetStatus.Deleted;
        asset.DeletedAt = DateTime.UtcNow;
        asset.UpdatedAt = asset.DeletedAt.Value;
        _unitOfWork.CardAssets.Update(asset);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation(
            "ASSET_DELETED AssetId={AssetId} BusinessId={BusinessId} Actor={ActorId}",
            asset.Id, businessId, scope.Actor.UserId);

        return ApiResponse<bool>.Ok(true);
    }
}
