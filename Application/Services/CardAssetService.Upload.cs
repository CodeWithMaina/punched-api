using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Assets;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

/// <summary>
/// Upload, content delivery, reference validation and mapping for card assets.
///
/// The upload path is the highest-risk surface in this module (untrusted bytes
/// crossing a trust boundary), so it is deliberately linear and fully validated
/// before anything is persisted:
/// <code>
/// read with a hard bound → signature inspection → dimension/pixel budget
/// → quota → store → persist metadata
/// </code>
/// </summary>
public partial class CardAssetService
{
    /// <inheritdoc />
    public async Task<ApiResponse<CardAssetResponse>> UploadAsync(
        Guid userId,
        UploadCardAssetRequest request,
        Stream content,
        string? originalFileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var scope = await _scopeResolver.ResolveAsync(userId, "loyalty.manage");
        if (!scope.Success)
            return ApiResponse<CardAssetResponse>.Fail(scope.ErrorCode!, scope.ErrorMessage!);

        var businessId = scope.Actor!.BusinessId;

        var purpose = string.IsNullOrWhiteSpace(request?.Purpose)
            ? CardAssetPurposes.Artwork
            : request!.Purpose!.Trim().ToUpperInvariant();

        if (!CardAssetPurposes.IsSupported(purpose))
            return ApiResponse<CardAssetResponse>.Fail(
                "INVALID_ASSET_PURPOSE",
                $"purpose must be one of: {string.Join(", ", CardAssetPurposes.All)}.");

        // Storage-abuse guard: cap the number of live assets per tenant.
        var liveAssets = await _context.CardAssets
            .CountAsync(a => a.BusinessId == businessId && a.Status == CardAssetStatus.Active, cancellationToken);

        if (liveAssets >= _settings.MaxAssetsPerBusiness)
            return ApiResponse<CardAssetResponse>.Fail(
                "ASSET_QUOTA_EXCEEDED",
                $"This business has reached the limit of {_settings.MaxAssetsPerBusiness} stored assets.");

        // ── Read the payload with a hard bound ──────────────────────────────
        // The limit is enforced while reading, so a client that understates the
        // length and then streams forever is cut off instead of being buffered.
        byte[] bytes;
        try
        {
            bytes = await ReadBoundedAsync(content, _settings.MaxFileSizeBytes, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return ApiResponse<CardAssetResponse>.Fail(
                CardAssetBinaryValidator.TooLarge,
                $"The uploaded file exceeds the {_settings.MaxFileSizeBytes / 1024} KB limit.");
        }

        // ── Signature-based validation (never the extension or declared type) ─
        var inspection = CardAssetBinaryValidator.Inspect(bytes, _settings);
        if (!inspection.IsValid || inspection.Detected == null)
            return ApiResponse<CardAssetResponse>.Fail(
                inspection.ErrorCode!, inspection.Message ?? "The uploaded file is not a supported image.");

        var format = inspection.Detected;
        var assetId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        CardAssetWriteResult write;
        try
        {
            await using var payload = new MemoryStream(bytes, writable: false);
            write = await _storage.WriteAsync(businessId, assetId, format.Extension, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store card asset for business {BusinessId}.", businessId);
            return ApiResponse<CardAssetResponse>.Fail(
                "ASSET_STORE_FAILED", "The asset could not be stored. Please try again.");
        }

        var asset = new CardAsset
        {
            Id = assetId,
            BusinessId = businessId,
            UploadedByUserId = scope.Actor.UserId,
            Purpose = purpose,
            Kind = CardAssetKind.Image,
            ContentType = format.ContentType,
            FileExtension = format.Extension,
            SizeBytes = write.SizeBytes,
            Width = format.Width,
            Height = format.Height,
            StorageKey = write.StorageKey,
            Sha256 = write.Sha256,
            Status = CardAssetStatus.Active,
            OriginalFileName = SanitizeDisplayFileName(originalFileName, format.Extension),
            CreatedAt = now,
            UpdatedAt = now
        };

        await _unitOfWork.CardAssets.AddAsync(asset);

        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            // The unique storage-key index is the last line of defence against a
            // duplicate write. Clean up the orphaned payload and fail closed.
            _logger.LogError(ex, "Failed to persist card asset {AssetId}.", assetId);
            await _storage.DeleteAsync(write.StorageKey, cancellationToken);
            return ApiResponse<CardAssetResponse>.Fail(
                "ASSET_STORE_FAILED", "The asset could not be stored. Please try again.");
        }

        // Structured audit event (§32): identifiers and measured facts only —
        // never the payload, and never a customer identifier.
        _logger.LogInformation(
            "ASSET_UPLOADED AssetId={AssetId} BusinessId={BusinessId} Purpose={Purpose} " +
            "Format={Format} Bytes={Bytes} Width={Width} Height={Height} Actor={ActorId}",
            asset.Id, businessId, purpose, format.Name, write.SizeBytes, format.Width, format.Height, scope.Actor.UserId);

        return ApiResponse<CardAssetResponse>.Ok(Map(asset));
    }

    /// <inheritdoc />
    public async Task<ApiResponse<CardAssetContentResult>> OpenContentAsync(
        Guid userId, Guid assetId, CancellationToken cancellationToken = default)
    {
        var scope = await _scopeResolver.ResolveAsync(userId, "loyalty.stamp");
        if (!scope.Success)
            return ApiResponse<CardAssetContentResult>.Fail(scope.ErrorCode!, scope.ErrorMessage!);

        // Deleted assets are still served: a design that referenced the asset before
        // it was removed must keep rendering (§17). Only the picker hides it.
        var asset = await _context.CardAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == assetId && a.BusinessId == scope.Actor!.BusinessId, cancellationToken);

        if (asset == null)
            return ApiResponse<CardAssetContentResult>.Fail("NOT_FOUND", "The asset could not be found.");

        var payload = await _storage.OpenReadAsync(asset.StorageKey, cancellationToken);
        if (payload == null)
        {
            _logger.LogWarning(
                "ASSET_PAYLOAD_MISSING AssetId={AssetId} BusinessId={BusinessId}", asset.Id, asset.BusinessId);
            return ApiResponse<CardAssetContentResult>.Fail("NOT_FOUND", "The asset could not be found.");
        }

        return ApiResponse<CardAssetContentResult>.Ok(new CardAssetContentResult
        {
            Content = payload.Content,
            ContentType = asset.ContentType,
            Length = payload.SizeBytes,
            FileName = $"{asset.Id:N}.{asset.FileExtension}"
        });
    }

    /// <inheritdoc />
    public async Task<CardAssetReferenceCheck> ValidateReferencesAsync(Guid businessId, IEnumerable<Guid> assetIds)
    {
        var ids = assetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (ids.Count == 0) return CardAssetReferenceCheck.Valid;

        var owned = await _context.CardAssets
            .AsNoTracking()
            .Where(a => a.BusinessId == businessId && a.Status == CardAssetStatus.Active && ids.Contains(a.Id))
            .Select(a => a.Id)
            .ToListAsync();

        // One message for "not yours" and "does not exist" — the response must
        // never confirm that another tenant's asset id is real (§20, §9).
        return CardAssetReferences.Evaluate(ids, owned);
    }

    /// <summary>
    /// Buffers a stream while enforcing a byte ceiling. Throws
    /// <see cref="InvalidOperationException"/> when the ceiling is exceeded so the
    /// caller can map it to a stable error code.
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(Stream source, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read <= 0) break;

            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException("The uploaded payload exceeds the configured limit.");

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Produces a safe display label from a client-supplied filename.
    ///
    /// Only the final path segment is kept, separators/control characters/angle
    /// brackets are removed, the length is capped, and the *detected* extension is
    /// appended. This value is presentation metadata only — it is never joined to a
    /// path and never used as a storage key, so filename injection and path
    /// traversal are structurally impossible rather than filtered.
    /// </summary>
    internal static string? SanitizeDisplayFileName(string? originalFileName, string detectedExtension)
    {
        if (string.IsNullOrWhiteSpace(originalFileName)) return null;

        // Discard any directory component (handles both slash styles explicitly so
        // the behaviour is identical on every platform).
        var lastSeparator = originalFileName.LastIndexOfAny(new[] { '/', '\\' });
        var candidate = lastSeparator >= 0 ? originalFileName[(lastSeparator + 1)..] : originalFileName;

        var cleaned = new string(candidate
            .Where(ch => !char.IsControl(ch) && ch is not ('<' or '>' or ':' or '"' or '|' or '?' or '*'))
            .ToArray())
            .Trim();

        if (cleaned.Length == 0) return null;

        var withoutExtension = Path.GetFileNameWithoutExtension(cleaned);
        if (string.IsNullOrWhiteSpace(withoutExtension)) withoutExtension = "asset";

        var label = $"{withoutExtension}.{detectedExtension}";
        return label.Length <= 255 ? label : label[..255];
    }

    /// <summary>Maps an entity to its API projection.</summary>
    internal static CardAssetResponse Map(CardAsset asset) => new()
    {
        Id = asset.Id,
        BusinessId = asset.BusinessId,
        Purpose = asset.Purpose,
        ContentType = asset.ContentType,
        FileExtension = asset.FileExtension,
        SizeBytes = asset.SizeBytes,
        Width = asset.Width,
        Height = asset.Height,
        Sha256 = asset.Sha256,
        Status = asset.Status == CardAssetStatus.Active ? "active" : "deleted",
        OriginalFileName = asset.OriginalFileName,
        ContentUrl = CardAssetUrls.ContentPath(asset.Id),
        CreatedAt = asset.CreatedAt,
        UpdatedAt = asset.UpdatedAt
    };
}
