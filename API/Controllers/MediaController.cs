using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

[ApiController]
[Route("v1/media")]
[Produces("application/json")]
[Authorize]
[EnableRateLimiting("general")]
public sealed class MediaController : ControllerBase
{
    private readonly IMediaService _media;
    public MediaController(IMediaService media) => _media = media;

    [HttpPost("uploads")]
    [EnableRateLimiting("media-upload")]
    public async Task<IActionResult> Create([FromBody] CreateMediaUploadRequest request, CancellationToken cancellationToken)
    {
        var userId = UserId();
        if (!userId.HasValue) return Unauthorized();
        var key = Request.Headers["Idempotency-Key"].FirstOrDefault();
        var result = await _media.CreateUploadAsync(userId.Value, request, key ?? string.Empty, cancellationToken);
        return Map(result, true);
    }

    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, CancellationToken cancellationToken)
    {
        var userId = UserId();
        if (!userId.HasValue) return Unauthorized();
        var key = Request.Headers["Idempotency-Key"].FirstOrDefault();
        return Map(await _media.CompleteAsync(userId.Value, id, key ?? string.Empty, cancellationToken));
    }

    [HttpPost("{id:guid}/upload-grant")]
    public async Task<IActionResult> Regrant(Guid id, CancellationToken cancellationToken)
    { var userId = UserId(); return userId == null ? Unauthorized() : Map(await _media.RegrantAsync(userId.Value, id, cancellationToken)); }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    { var userId = UserId(); return userId == null ? Unauthorized() : Map(await _media.GetAsync(userId.Value, id, cancellationToken)); }

    [HttpGet("public/{id:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPublic(Guid id, CancellationToken cancellationToken)
    { return Map(await _media.GetPublicProjectionAsync(id, cancellationToken)); }

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken)
    { var userId = UserId(); return userId == null ? Unauthorized() : Map(await _media.RetryAsync(userId.Value, id, cancellationToken)); }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    { var userId = UserId(); return userId == null ? Unauthorized() : Map(await _media.DeleteAsync(userId.Value, id, cancellationToken)); }

    [HttpPut("business/logo")]
    public Task<IActionResult> AssignLogo([FromBody] AssignMediaRequest request, CancellationToken cancellationToken) => AssignBusiness("logo", request, cancellationToken);
    [HttpDelete("business/logo")]
    public Task<IActionResult> ClearLogo(CancellationToken cancellationToken) => ClearBusiness("logo", cancellationToken);
    [HttpPut("business/cover")]
    public Task<IActionResult> AssignCover([FromBody] AssignMediaRequest request, CancellationToken cancellationToken) => AssignBusiness("cover", request, cancellationToken);
    [HttpDelete("business/cover")]
    public Task<IActionResult> ClearCover(CancellationToken cancellationToken) => ClearBusiness("cover", cancellationToken);
    [HttpPut("user/avatar")]
    public Task<IActionResult> AssignAvatar([FromBody] AssignMediaRequest request, CancellationToken cancellationToken) => AssignAvatarCore(request, cancellationToken);
    [HttpDelete("user/avatar")]
    public Task<IActionResult> ClearAvatar(CancellationToken cancellationToken) => HandleClearAvatar(cancellationToken);
    [HttpPost("business/gallery")]
    public Task<IActionResult> AttachGallery([FromBody] AttachGalleryMediaRequest request, CancellationToken cancellationToken) => HandleAttachGallery(request, cancellationToken);
    [HttpPatch("business/gallery/order")]
    public Task<IActionResult> ReorderGallery([FromBody] ReorderGalleryMediaRequest request, CancellationToken cancellationToken) => HandleReorderGallery(request, cancellationToken);
    [HttpDelete("business/gallery/{mediaId:guid}")]
    public Task<IActionResult> DetachGallery(Guid mediaId, CancellationToken cancellationToken) => Detach("business-gallery", Guid.Empty, mediaId, cancellationToken);
    [HttpPut("service/{serviceId:guid}")]
    public Task<IActionResult> AttachService(Guid serviceId, [FromBody] AttachRelationshipMediaRequest request, CancellationToken cancellationToken) => HandleAttachService(serviceId, request, cancellationToken);
    [HttpDelete("service/{serviceId:guid}/{mediaId:guid}")]
    public Task<IActionResult> DetachService(Guid serviceId, Guid mediaId, CancellationToken cancellationToken) => Detach("service", serviceId, mediaId, cancellationToken);
    [HttpPut("loyalty-program/{programId:guid}")]
    public Task<IActionResult> AttachProgram(Guid programId, [FromBody] AttachRelationshipMediaRequest request, CancellationToken cancellationToken) => HandleAttachProgram(programId, request, cancellationToken);
    [HttpDelete("loyalty-program/{programId:guid}/{mediaId:guid}")]
    public Task<IActionResult> DetachProgram(Guid programId, Guid mediaId, CancellationToken cancellationToken) => Detach("loyalty-program", programId, mediaId, cancellationToken);
    [HttpPut("review/{reviewId:guid}")]
    public Task<IActionResult> AttachReview(Guid reviewId, [FromBody] AttachRelationshipMediaRequest request, CancellationToken cancellationToken) => HandleAttachReview(reviewId, request, cancellationToken);
    [HttpDelete("review/{reviewId:guid}/{mediaId:guid}")]
    public Task<IActionResult> DetachReview(Guid reviewId, Guid mediaId, CancellationToken cancellationToken) => Detach("review", reviewId, mediaId, cancellationToken);

    private async Task<IActionResult> AssignBusiness(string field, AssignMediaRequest request, CancellationToken ct)
    { var id = UserId(); return id == null ? Unauthorized() : Map(await _media.AssignBusinessFieldAsync(id.Value, field, request.MediaId, ct)); }
    private async Task<IActionResult> ClearBusiness(string field, CancellationToken ct)
    { var id = UserId(); return id == null ? Unauthorized() : Map(await _media.AssignBusinessFieldAsync(id.Value, field, Guid.Empty, ct)); }
    private async Task<IActionResult> AssignAvatarCore(AssignMediaRequest request, CancellationToken ct)
    { var id = UserId(); return id == null ? Unauthorized() : Map(await _media.AssignUserAvatarAsync(id.Value, request.MediaId, ct)); }
    private async Task<IActionResult> HandleClearAvatar(CancellationToken ct)
    { var id = UserId(); return id == null ? Unauthorized() : Map(await _media.AssignUserAvatarAsync(id.Value, Guid.Empty, ct)); }
    private async Task<IActionResult> HandleAttachGallery(AttachGalleryMediaRequest request, CancellationToken ct)
    { var id = UserId(); return id == null ? Unauthorized() : Map(await _media.AttachGalleryAsync(id.Value, request.MediaId, request.SortOrder, request.Featured, ct)); }
    private async Task<IActionResult> HandleReorderGallery(ReorderGalleryMediaRequest request, CancellationToken ct)
    { var id = UserId(); return id == null ? Unauthorized() : Map(await _media.ReorderGalleryAsync(id.Value, request.MediaIds, ct)); }
    private async Task<IActionResult> HandleAttachService(Guid serviceId, AttachRelationshipMediaRequest request, CancellationToken ct)
    { var id = UserId(); return id == null ? Unauthorized() : Map(await _media.AttachServiceAsync(id.Value, serviceId, request.MediaId, request.SortOrder, ct)); }
    private async Task<IActionResult> HandleAttachProgram(Guid programId, AttachRelationshipMediaRequest request, CancellationToken ct)
    { var id = UserId(); return id == null ? Unauthorized() : Map(await _media.AttachProgramAsync(id.Value, programId, request.MediaId, request.SortOrder, ct)); }
    private async Task<IActionResult> HandleAttachReview(Guid reviewId, AttachRelationshipMediaRequest request, CancellationToken ct)
    { var id = UserId(); return id == null ? Unauthorized() : Map(await _media.AttachReviewAsync(id.Value, reviewId, request.MediaId, request.SortOrder, ct)); }
    private async Task<IActionResult> Detach(string relationship, Guid targetId, Guid mediaId, CancellationToken ct)
    { var id = UserId(); return id == null ? Unauthorized() : Map(await _media.DetachRelationshipAsync(id.Value, relationship, targetId, mediaId, ct)); }

    private IActionResult Map<T>(ApiResponse<T> result, bool created = false)
    {
        if (result.Success) return created ? StatusCode(StatusCodes.Status201Created, result) : Ok(result);
        return result.Error?.Code switch
        {
            "FORBIDDEN" => StatusCode(StatusCodes.Status403Forbidden, result),
            "MEDIA_NOT_FOUND" or "TARGET_NOT_FOUND" => NotFound(result),
            "IDEMPOTENCY_CONFLICT" or "MEDIA_STATE_CONFLICT" => Conflict(result),
            "UNSUPPORTED_MEDIA_TYPE" => StatusCode(StatusCodes.Status415UnsupportedMediaType, result),
            "MEDIA_TOO_LARGE" or "MEDIA_SIZE_MISMATCH" => StatusCode(StatusCodes.Status413PayloadTooLarge, result),
            _ => BadRequest(result)
        };
    }

    private Guid? UserId() => Guid.TryParse(User.FindFirst("userId")?.Value, out var id) ? id : null;
}
