using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Filters;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Upload and delivery of loyalty card branding assets (logo, background, stamp
/// icons, artwork). Base route: <c>/v1/card-assets</c>.
///
/// Security posture (§7, §8, §9, §21):
/// <list type="bullet">
/// <item><b>No tenancy in the contract.</b> There is no business id in any route
/// or body — the tenant is derived from the authenticated principal, so a
/// cross-tenant write is not expressible through this API.</item>
/// <item><b>Two rate-limit buckets.</b> Mutating uploads use the tight
/// <c>asset-upload</c> policy; reads use the general policy, so upload flooding is
/// bounded per user independently of normal traffic.</item>
/// <item><b>Uploads are never trusted.</b> The controller only forwards the raw
/// stream and the (display-only) filename; everything security-relevant —
/// format, dimensions, content type, storage key — is decided server-side.</item>
/// <item><b>Content responses are hardened.</b> The stored (detected) content type
/// is sent with <c>nosniff</c> and a restrictive <c>Content-Disposition</c>, so a
/// payload can never be interpreted as HTML by a browser.</item>
/// </list>
/// </summary>
[ApiController]
[Route("v1/card-assets")]
[Produces("application/json")]
[Authorize(Roles = "Business,Staff")]
[RequireModule("loyalty")]
public class CardAssetController : ControllerBase
{
    /// <summary>Largest upload this endpoint will even accept from the transport layer.</summary>
    private const long RequestSizeLimitBytes = 8 * 1024 * 1024;

    private readonly ICardAssetService _cardAssetService;

    public CardAssetController(ICardAssetService cardAssetService)
    {
        _cardAssetService = cardAssetService;
    }

    /// <summary>
    /// Uploads a branded image for the caller's business. Accepts PNG, JPEG, GIF
    /// and WebP. SVG is rejected by design.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(RequestSizeLimitBytes)]
    [EnableRateLimiting("asset-upload")]
    [ProducesResponseType(typeof(ApiResponse<CardAssetResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Upload(
        IFormFile? file, [FromForm] string? purpose, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (file == null || file.Length == 0)
            return BadRequest(ApiResponse<CardAssetResponse>.Fail("ASSET_REQUIRED", "Attach an image file."));

        await using var stream = file.OpenReadStream();

        var result = await _cardAssetService.UploadAsync(
            userId.Value,
            new UploadCardAssetRequest { Purpose = purpose },
            stream,
            file.FileName,
            cancellationToken);

        if (result.Success) return StatusCode(StatusCodes.Status201Created, result);
        return MapFailure(result);
    }

    /// <summary>Lists the caller's business's live assets, optionally filtered by purpose.</summary>
    [HttpGet]
    [EnableRateLimiting("general")]
    [ProducesResponseType(typeof(ApiResponse<List<CardAssetResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string? purpose = null)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _cardAssetService.ListAsync(userId.Value, purpose);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Reads one asset's metadata (tenant-scoped).</summary>
    [HttpGet("{assetId:guid}")]
    [EnableRateLimiting("general")]
    [ProducesResponseType(typeof(ApiResponse<CardAssetResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid assetId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _cardAssetService.GetAsync(userId.Value, assetId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Streams an asset's bytes. Only ever served for assets owned by the caller's
    /// business, with the signature-detected content type and no-sniff headers.
    /// </summary>
    [HttpGet("{assetId:guid}/content")]
    [EnableRateLimiting("general")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Content(Guid assetId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _cardAssetService.OpenContentAsync(userId.Value, assetId, cancellationToken);
        if (!result.Success || result.Data == null)
            return NotFound(result);

        // Hardening: the content type comes from storage (signature-detected), never
        // from the client, and nosniff stops a crafted payload being sniffed into an
        // active document.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        Response.Headers["Cache-Control"] = "private, max-age=3600";

        return File(result.Data.Content, result.Data.ContentType);
    }

    /// <summary>Soft-deletes an asset: it stops being selectable but keeps rendering.</summary>
    [HttpDelete("{assetId:guid}")]
    [EnableRateLimiting("general")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(Guid assetId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _cardAssetService.DeleteAsync(userId.Value, assetId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Maps a service failure to a status code without ever distinguishing
    /// "another tenant's resource" from "does not exist".
    /// </summary>
    private IActionResult MapFailure<T>(ApiResponse<T> result) => result.Error?.Code switch
    {
        "UNAUTHORIZED" => Unauthorized(result),
        "NOT_FOUND" => NotFound(result),
        "NOT_LINKED" or "FORBIDDEN" or "MODULE_DISABLED" => StatusCode(StatusCodes.Status403Forbidden, result),
        "ASSET_TOO_LARGE" or "ASSET_DIMENSIONS_EXCEEDED" => StatusCode(StatusCodes.Status413PayloadTooLarge, result),
        "UNSUPPORTED_ASSET_FORMAT" or "SVG_NOT_SUPPORTED" or "ASSET_MALFORMED" =>
            StatusCode(StatusCodes.Status415UnsupportedMediaType, result),
        "ASSET_QUOTA_EXCEEDED" => StatusCode(StatusCodes.Status429TooManyRequests, result),
        _ => BadRequest(result)
    };

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}
