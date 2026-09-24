namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Binary storage for card-asset payloads. Implemented by
/// <c>LocalCardAssetStorage</c> today; the abstraction exists so a cloud object
/// store can be dropped in without touching the asset service or the domain.
///
/// Contract:
/// <list type="bullet">
/// <item>Callers never supply a path. They supply the tenant and asset identity,
/// and the implementation derives the storage key itself — the original filename
/// is never part of a key (§7).</item>
/// <item>Keys are opaque strings; the service persists whatever the
/// implementation returns and uses it for every later read.</item>
/// <item>Implementation must be safe against traversal: a key that resolves
/// outside the configured root is a hard failure, not a clamped path.</item>
/// </list>
/// </summary>
public interface ICardAssetStorage
{
    /// <summary>
    /// Persists a verified payload and returns the generated storage key plus the
    /// byte length and SHA-256 of what was actually written.
    /// </summary>
    Task<CardAssetWriteResult> WriteAsync(
        Guid businessId, Guid assetId, string extension, Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a stored payload for reading, or null when the key does not exist.
    /// </summary>
    Task<CardAssetPayload?> OpenReadAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>True when the key resolves to an existing stored payload.</summary>
    Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>
    /// Best-effort physical removal. Never throws for a missing key — callers treat
    /// physical deletion as a cleanup concern, not a transactional one.
    /// </summary>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}

/// <summary>
/// Result of a successful <see cref="ICardAssetStorage.WriteAsync"/>: the generated
/// key plus the measured properties of the stored bytes.
/// </summary>
/// <param name="StorageKey">Opaque key to persist on the asset row.</param>
/// <param name="SizeBytes">Bytes written.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the written payload.</param>
public sealed record CardAssetWriteResult(string StorageKey, long SizeBytes, string Sha256);

/// <summary>An open, readable stored payload.</summary>
/// <param name="Content">Stream owned by the caller (must be disposed).</param>
/// <param name="SizeBytes">Known length when available.</param>
public sealed record CardAssetPayload(Stream Content, long SizeBytes) : IAsyncDisposable
{
    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await Content.DisposeAsync();
}
