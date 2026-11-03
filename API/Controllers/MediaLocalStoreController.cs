using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using PunchedApi.Application.Media;
using PunchedApi.Infrastructure.Services.Storage;

namespace PunchedApi.API.Controllers;

/// <summary>
/// The two HTTP endpoints the filesystem media provider needs: a signed PUT that
/// stands in for an object-store presigned upload, and a public delivery route
/// that stands in for the CDN.
///
/// Both are inert unless <c>MediaStorage:Provider</c> is <c>local</c> — the
/// dependency is injected as an <c>IEnumerable</c>, so production (which resolves
/// the R2 store) never constructs the filesystem provider and every route here
/// answers 404.
///
/// Security:
/// <list type="bullet">
/// <item>The upload route is anonymous but only accepts a key/content-type/expiry
/// triple signed by the server, so it is exactly as scoped as a presigned URL —
/// no bearer token is required, and none is accepted.</item>
/// <item>Delivery can only reach objects under the <c>delivery/</c> prefix in the
/// public bucket, so a customer's private source upload is unreachable even if its
/// key were known.</item>
/// </list>
/// </summary>
[ApiController]
[AllowAnonymous]
[Produces("application/json")]
[EnableRateLimiting("general")]
public sealed class MediaLocalStoreController : ControllerBase
{
    /// <summary>Hard ceiling on a single uploaded body; per-purpose limits are enforced later.</summary>
    private const long MaxUploadBytes = 33_554_432;

    private readonly IEnumerable<ILocalMediaStore> _localStores;
    private readonly ILocalMediaUrlSigner _signer;
    private readonly ILogger<MediaLocalStoreController> _logger;

    public MediaLocalStoreController(
        IEnumerable<ILocalMediaStore> localStores,
        ILocalMediaUrlSigner signer,
        ILogger<MediaLocalStoreController> logger)
    {
        _localStores = localStores;
        _signer = signer;
        _logger = logger;
    }

    /// <summary>Emulates a presigned PUT: writes one body to one signed key.</summary>
    [HttpPut("v1/media/local/upload/{token}")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Upload(
        string token,
        [FromQuery] long exp,
        [FromQuery] string? sig,
        CancellationToken cancellationToken)
    {
        var store = _localStores.FirstOrDefault();
        if (store is null) return NotFound();

        var key = DecodeToken(token);
        if (key is null) return BadRequest(Error("INVALID_UPLOAD_TARGET", "The upload target is not valid."));

        var contentType = Request.ContentType?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(contentType))
            return BadRequest(Error("INVALID_CONTENT_TYPE", "A Content-Type header is required."));

        // A forged, mismatched or expired grant is a 403 so the client's existing
        // "regrant and retry once" path handles it exactly as it would for R2.
        if (!_signer.Validate(key, contentType, exp, sig ?? string.Empty))
            return StatusCode(StatusCodes.Status403Forbidden,
                Error("UPLOAD_GRANT_INVALID", "The upload grant is invalid or has expired."));

        if (Request.ContentLength is > MaxUploadBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                Error("MEDIA_TOO_LARGE", "The uploaded object is larger than the permitted maximum."));

        await store.StoreSourceAsync(key, Request.Body, contentType, cancellationToken);
        return Ok(new { success = true, data = new { key } });
    }

    /// <summary>
    /// Serves a processed delivery object. Range requests, <c>ETag</c> and
    /// <c>Last-Modified</c> are honoured, so a browser revalidates with a 304 and
    /// the processing worker's reachability probe sees the real content type.
    /// </summary>
    [HttpGet("v1/media/delivery/{**key}")]
    [HttpHead("v1/media/delivery/{**key}")]
    public IActionResult Deliver(string key)
    {
        var store = _localStores.FirstOrDefault();
        if (store is null) return NotFound();

        var path = store.TryResolveDeliveryPath(key);
        if (path is null) return NotFound();

        var info = new FileInfo(path);
        var contentType = LocalObjectStore.ContentTypeFor(key);

        // Delivery objects are immutable and content-addressed by key, so they can
        // be cached indefinitely; the ETag lets a repeat visit skip the body.
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";

        return PhysicalFile(
            path,
            contentType,
            lastModified: new DateTimeOffset(info.LastWriteTimeUtc),
            entityTag: new EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\""),
            enableRangeProcessing: true);
    }

    private static string? DecodeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var key = System.Text.Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
            // Reject anything that is not a plausible pending source key before it
            // reaches the store, so a malformed token cannot probe the filesystem.
            return key.StartsWith("pending/", StringComparison.Ordinal) ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static object Error(string code, string message) =>
        new { success = false, data = (object?)null, error = new { code, message } };
}
