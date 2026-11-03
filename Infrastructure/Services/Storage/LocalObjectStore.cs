using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Media;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Infrastructure.Services.Storage;

/// <summary>
/// The extra capabilities the HTTP layer needs from the filesystem provider only:
/// locating a stored delivery object (so it can be streamed with range and
/// conditional-request support) and storing an object body that arrived over the
/// signed upload endpoint. Implemented solely by <see cref="LocalObjectStore"/> and
/// injected as an optional dependency (an empty <c>IEnumerable</c> under the
/// object-store provider), so the API never fails to start because a
/// development-only service is absent — and the production provider is never
/// constructed on a code path that cannot use it.
/// </summary>
public interface ILocalMediaStore
{
    /// <summary>Absolute path for a delivery key, or null when it does not exist.</summary>
    string? TryResolveDeliveryPath(string key);

    /// <summary>Writes an uploaded body into the private source bucket.</summary>
    Task StoreSourceAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);
}

/// <summary>
/// Filesystem <see cref="IObjectStore"/> for local development and CI.
///
/// Security properties mirror <see cref="LocalCardAssetStorage"/>:
/// <list type="bullet">
/// <item><b>Buckets are separate directories.</b> A private source object and a
/// public delivery object can never collide, and a delivery read is confined to
/// the delivery root — it cannot reach a customer's original upload.</item>
/// <item><b>Keys are re-validated at use time.</b> Rooted paths, traversal
/// segments, backslashes and NUL are rejected, then the resolved path is asserted
/// to still be inside the bucket root, so a hostile key read back out of the
/// database cannot escape.</item>
/// <item><b>Writes are atomic-ish.</b> Content lands in a temp file and is moved
/// into place, so a crashed upload never leaves a half-written object that later
/// reads would serve.</item>
/// <item><b>Upload grants are signed and short-lived.</b> The PUT endpoint only
/// accepts a key/content-type/expiry triple signed by the server.</item>
/// <item><b>The root is never web-served.</b> Payloads are reachable only through
/// the media controller, which applies the same key rules.</item>
/// </list>
/// </summary>
public sealed class LocalObjectStore : IObjectStore, ILocalMediaStore
{
    private const string PrivateSourceDirectory = "private-source";
    private const string PublicDeliveryDirectory = "public-delivery";

    private readonly MediaStorageOptions _options;
    private readonly ILocalMediaUrlSigner _signer;
    private readonly ILogger<LocalObjectStore> _logger;
    private readonly string _rootFullPath;

    public LocalObjectStore(
        IOptions<MediaStorageOptions> options,
        ILocalMediaUrlSigner signer,
        ILogger<LocalObjectStore> logger)
    {
        _options = options.Value;
        _signer = signer;
        _logger = logger;

        // Fail fast and loudly on a misconfigured local provider rather than
        // surfacing an opaque conflict to the first upload attempt.
        if (!_options.UsesLocalStore)
            throw new InvalidOperationException("MediaStorage:Provider must be 'local' for LocalObjectStore.");
        _options.Validate(requireR2: false);

        _rootFullPath = Path.GetFullPath(_options.StorageRoot);
        Directory.CreateDirectory(Path.Combine(_rootFullPath, PrivateSourceDirectory));
        Directory.CreateDirectory(Path.Combine(_rootFullPath, PublicDeliveryDirectory));
    }

    /// <summary>The absolute root payloads are written to (diagnostics/tests).</summary>
    public string RootPath => _rootFullPath;

    /// <inheritdoc />
    public string? TryResolveDeliveryPath(string key)
    {
        // Only public delivery objects are addressable, and only under the
        // delivery root: a source object can never be served, even if its key
        // somehow reached this endpoint.
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith("delivery/", StringComparison.Ordinal)) return null;
        try
        {
            var path = ResolvePath(ObjectStoreBucket.PublicDelivery, key);
            return File.Exists(path) ? path : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task StoreSourceAsync(string key, Stream content, string contentType, CancellationToken cancellationToken) =>
        PutAsync(ObjectStoreBucket.PrivateSource, key, content, contentType, "private, max-age=0", cancellationToken);

    /// <summary>
    /// Mints a signed PUT URL the browser can use exactly like a presigned object
    /// store grant. Only the private source bucket accepts uploads.
    /// </summary>
    public Task<PresignedObjectWrite> PresignPutAsync(
        ObjectStoreBucket bucket, string key, string contentType, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bucket != ObjectStoreBucket.PrivateSource)
            throw new InvalidOperationException("Only private source objects can be uploaded.");
        ValidateKey(key);

        var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(key));
        var signature = _signer.Sign(key, contentType, expiresAt);
        var url = $"{_options.LocalBaseUrl.TrimEnd('/')}/v1/media/local/upload/{token}" +
                  $"?exp={expiresAt.ToUnixTimeSeconds()}&sig={Uri.EscapeDataString(signature)}";

        return Task.FromResult(new PresignedObjectWrite(
            new Uri(url),
            new Dictionary<string, string> { ["Content-Type"] = contentType },
            expiresAt));
    }

    public Task<ObjectMetadata?> HeadAsync(ObjectStoreBucket bucket, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(ResolvePath(bucket, key));
        return Task.FromResult<ObjectMetadata?>(info.Exists ? new ObjectMetadata(info.Length, ContentTypeFor(key)) : null);
    }

    public Task<ObjectReadResult?> GetBoundedAsync(ObjectStoreBucket bucket, string key, long maxBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        var path = ResolvePath(bucket, key);
        var info = new FileInfo(path);
        if (!info.Exists) return Task.FromResult<ObjectReadResult?>(null);
        if (info.Length > maxBytes) throw new InvalidDataException("OBJECT_TOO_LARGE");

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult<ObjectReadResult?>(new ObjectReadResult(stream, new ObjectMetadata(info.Length, ContentTypeFor(key))));
    }

    public async Task PutAsync(ObjectStoreBucket bucket, string key, Stream content, string contentType, string cacheControl, CancellationToken cancellationToken)
    {
        var fullPath = ResolvePath(bucket, key);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(target, cancellationToken);
                await target.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    public Task DeleteAsync(ObjectStoreBucket bucket, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { TryDeleteFile(ResolvePath(bucket, key)); }
        catch (Exception ex)
        {
            // Physical deletion is best effort: the authoritative lifecycle lives on
            // the media row, so a failure here must not fail the business operation.
            _logger.LogWarning(ex, "Could not remove stored media object {Key}.", key);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Maps an opaque key to an absolute path inside the bucket root, refusing
    /// anything that could escape it. Throws rather than clamping, so a corrupt key
    /// is loud instead of silently serving the wrong file.
    /// </summary>
    internal string ResolvePath(ObjectStoreBucket bucket, string key)
    {
        ValidateKey(key);
        var bucketRoot = Path.Combine(_rootFullPath, BucketDirectory(bucket));
        Directory.CreateDirectory(bucketRoot);

        var candidate = Path.GetFullPath(Path.Combine(bucketRoot, key));
        var rootWithSeparator = bucketRoot.EndsWith(Path.DirectorySeparatorChar)
            ? bucketRoot
            : bucketRoot + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The media object key resolves outside its bucket root.");

        return candidate;
    }

    internal static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("A media object key is required.");
        if (Path.IsPathRooted(key) || key.Contains('\0') || key.Contains('\\'))
            throw new InvalidOperationException("The media object key is not a relative path.");
        if (key.Split('/').Any(segment => segment is ".." or "." or ""))
            throw new InvalidOperationException("The media object key contains a traversal segment.");
    }

    /// <summary>
    /// Delivery keys always end in the encoded format, so the content type is
    /// derived rather than stored — one less file per object on the hot read path.
    /// </summary>
    internal static string ContentTypeFor(string key) => Path.GetExtension(key).ToLowerInvariant() switch
    {
        ".webp" => "image/webp",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        _ => "application/octet-stream"
    };

    private static string BucketDirectory(ObjectStoreBucket bucket) => bucket switch
    {
        ObjectStoreBucket.PrivateSource => PrivateSourceDirectory,
        ObjectStoreBucket.PublicDelivery => PublicDeliveryDirectory,
        _ => throw new ArgumentOutOfRangeException(nameof(bucket))
    };

    private static void TryDeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
