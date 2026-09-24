using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Upload, listing, lifecycle and delivery of a business's card branding assets.
///
/// Security invariants this interface is contracted to uphold (§7, §8, §9):
/// <list type="bullet">
/// <item><b>No client-supplied tenancy.</b> Every method takes the authenticated
/// user id; the business is resolved server-side from that principal. There is no
/// <c>businessId</c> parameter to trust — by construction.</item>
/// <item><b>No client-supplied format.</b> <see cref="UploadAsync"/> ignores the
/// caller's filename extension and content type; the stored type comes from the
/// detected file signature.</item>
/// <item><b>Tenant isolation on every read.</b> An asset id belonging to another
/// business resolves to <c>NOT_FOUND</c>, never to data and never to a distinct
/// "forbidden" signal that would confirm the id exists.</item>
/// </list>
/// </summary>
public interface ICardAssetService
{
    /// <summary>
    /// Validates and stores an uploaded image for the caller's business.
    /// </summary>
    /// <param name="userId">Authenticated user (Business owner). Never a request value.</param>
    /// <param name="request">Purpose (and nothing else that matters — format is detected).</param>
    /// <param name="content">Raw upload stream; buffered and bounded by the service.</param>
    /// <param name="originalFileName">
    /// Client-supplied filename, used *only* as a sanitized display label. It never
    /// becomes a storage path or a URL segment.
    /// </param>
    /// <param name="cancellationToken">Request cancellation.</param>
    Task<ApiResponse<CardAssetResponse>> UploadAsync(
        Guid userId,
        UploadCardAssetRequest request,
        Stream content,
        string? originalFileName,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the caller's business's live assets, optionally filtered by purpose.</summary>
    Task<ApiResponse<List<CardAssetResponse>>> ListAsync(Guid userId, string? purpose = null);

    /// <summary>Reads one asset's metadata, scoped to the caller's business.</summary>
    Task<ApiResponse<CardAssetResponse>> GetAsync(Guid userId, Guid assetId);

    /// <summary>
    /// Soft-deletes an asset. The row and payload are retained so designs that
    /// reference the asset keep resolving to a stable, auditable record instead of
    /// rendering as a broken image (§17).
    /// </summary>
    Task<ApiResponse<bool>> DeleteAsync(Guid userId, Guid assetId);

    /// <summary>
    /// Opens an asset's payload for delivery. The content type returned is the one
    /// stored from signature detection — never the client's declared type.
    /// </summary>
    Task<ApiResponse<CardAssetContentResult>> OpenContentAsync(
        Guid userId, Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Server-side ownership check for a set of asset ids referenced by a design.
    /// Used by the design validators so a business can never point its card at
    /// another tenant's asset (or at a deleted one).
    /// </summary>
    Task<CardAssetReferenceCheck> ValidateReferencesAsync(Guid businessId, IEnumerable<Guid> assetIds);
}

/// <summary>Outcome of validating the asset references inside a design.</summary>
/// <param name="IsValid">True when every referenced asset belongs to the business and is live.</param>
/// <param name="ErrorCode">Stable error code when invalid.</param>
/// <param name="Message">Non-sensitive explanation.</param>
public sealed record CardAssetReferenceCheck(bool IsValid, string? ErrorCode, string? Message)
{
    /// <summary>All references are valid (including the empty set).</summary>
    public static readonly CardAssetReferenceCheck Valid = new(true, null, null);

    /// <summary>A reference failed the check.</summary>
    public static CardAssetReferenceCheck Invalid(string code, string message) => new(false, code, message);
}
