using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Assets;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Infrastructure.Services.Storage;

/// <summary>
/// Filesystem implementation of <see cref="ICardAssetStorage"/>.
///
/// Security properties:
/// <list type="bullet">
/// <item><b>Keys are generated, not derived from input.</b> A key is always
/// <c>{businessId:N}/{assetId:N}.{canonicalExtension}</c> — GUID "N" format is
/// hex-only, so no separator, dot-dot, drive letter or NUL can appear.</item>
/// <item><b>Every key is re-validated at use time.</b> <see cref="ResolvePath"/>
/// rejects rooted paths, traversal segments and known-invalid characters, then
/// asserts the fully-resolved path is still inside the root. This is defence in
/// depth: even a hostile value read back out of the database cannot escape the
/// storage root.</item>
/// <item><b>Writes are atomic-ish.</b> Content lands in a temp file and is moved
/// into place, so a crashed upload never leaves a half-written asset that later
/// reads would serve.</item>
/// <item><b>The root is never web-served.</b> Payloads are only reachable through
/// the controller, which applies authentication and a fixed content type.</item>
/// </list>
/// </summary>
public class LocalCardAssetStorage : ICardAssetStorage
{
    private readonly CardAssetSettings _settings;
    private readonly ILogger<LocalCardAssetStorage> _logger;
    private readonly string _rootFullPath;

    /// <summary>Creates the storage rooted at the configured directory.</summary>
    public LocalCardAssetStorage(
        IOptions<CardAssetSettings> settings,
        ILogger<LocalCardAssetStorage> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        var root = _settings.StorageRoot;
        if (string.IsNullOrWhiteSpace(root))
            root = "App_Data/card-assets";

        _rootFullPath = Path.GetFullPath(root);
        Directory.CreateDirectory(_rootFullPath);
    }

    /// <summary>The absolute root directory payloads are written to (for diagnostics/tests).</summary>
    public string RootPath => _rootFullPath;

    /// <inheritdoc />
    public async Task<CardAssetWriteResult> WriteAsync(
        Guid businessId, Guid assetId, string extension, Stream content, CancellationToken cancellationToken)
    {
        var safeExtension = SanitizeExtension(extension);
        var key = $"{businessId:N}/{assetId:N}.{safeExtension}";
        var fullPath = ResolvePath(key);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        long size;
        string hash;

        try
        {
            await using (var target = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(target, cancellationToken);
                await target.FlushAsync(cancellationToken);
                size = target.Length;
            }

            hash = await ComputeSha256Async(tempPath, cancellationToken);

            // Replace-into-place: an existing file (asset re-upload) is replaced
            // atomically, and a crash mid-copy leaves the previous version intact.
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        return new CardAssetWriteResult(key, size, hash);
    }

    /// <inheritdoc />
    public Task<CardAssetPayload?> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = ResolvePath(storageKey);
        if (!File.Exists(fullPath)) return Task.FromResult<CardAssetPayload?>(null);

        var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

        return Task.FromResult<CardAssetPayload?>(new CardAssetPayload(stream, stream.Length));
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(ResolvePath(storageKey)));
    }

    /// <inheritdoc />
    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            TryDeleteFile(ResolvePath(storageKey));
        }
        catch (Exception ex)
        {
            // Physical deletion is best effort: the authoritative lifecycle lives on
            // the asset row, so a failure here must not fail the business operation.
            _logger.LogWarning(ex, "Could not remove stored card asset payload {StorageKey}.", storageKey);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Maps an opaque key to an absolute path, refusing anything that could escape
    /// the configured root. Throws <see cref="InvalidOperationException"/> rather
    /// than returning a clamped path, so a corrupt key is loud instead of silent.
    /// </summary>
    internal string ResolvePath(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
            throw new InvalidOperationException("A card asset storage key is required.");

        if (Path.IsPathRooted(storageKey) || storageKey.Contains('\0'))
            throw new InvalidOperationException("The card asset storage key is not a relative path.");

        var normalized = storageKey.Replace('\\', '/');
        if (normalized.Split('/').Any(segment => segment is ".." or "."))
            throw new InvalidOperationException("The card asset storage key contains a traversal segment.");

        var candidate = Path.GetFullPath(Path.Combine(_rootFullPath, normalized));

        var rootWithSeparator = _rootFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootFullPath
            : _rootFullPath + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The card asset storage key resolves outside the storage root.");

        return candidate;
    }

    /// <summary>
    /// Deterministically sanitizes an extension taken from a *detected* format.
    /// Anything outside <c>[a-z0-9]</c> (max 5 chars) collapses to <c>bin</c>, so
    /// an extension can never introduce a path separator or a second extension.
    /// </summary>
    internal static string SanitizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return "bin";

        var cleaned = extension.Trim().TrimStart('.').ToLowerInvariant();
        if (cleaned.Length is 0 or > 5) return "bin";
        if (!cleaned.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9')) return "bin";

        return cleaned;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

