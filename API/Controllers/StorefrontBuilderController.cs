using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Filters;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// The Storefront Builder.
///
/// <para><b>Owner surface</b> (<c>v1/storefront/builder/*</c>) — gated by the
/// <c>storefront</c> module and the Owner policy. The business is derived from the
/// caller's membership server-side; a client never supplies a business id.</para>
///
/// <para><b>Public surface</b> (<c>v1/storefront/public/{businessId}</c>) —
/// anonymous, read-only, tenant-narrowed and capability-filtered. It returns only
/// the published configuration, never a draft.</para>
/// </summary>
[ApiController]
[Route("v1/storefront")]
[Produces("application/json")]
[EnableRateLimiting("general")]
[RequireModule("storefront")]
public class StorefrontBuilderController : ControllerBase
{
    private readonly IStorefrontBuilderService _builder;
    private readonly IStorefrontRevisionService _revisions;

    public StorefrontBuilderController(IStorefrontBuilderService builder, IStorefrontRevisionService revisions)
    {
        _builder = builder;
        _revisions = revisions;
    }

    [HttpGet("builder")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<StorefrontBuilderStateResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBuilder()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _builder.GetForOwnerAsync(userId.Value);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    [HttpPut("builder/draft")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<StorefrontBuilderStateResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SaveDraft([FromBody] SaveStorefrontDraftRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _builder.SaveDraftAsync(userId.Value, request);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    [HttpPost("builder/publish")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<StorefrontBuilderStateResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Publish([FromBody] PublishStorefrontRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _builder.PublishAsync(userId.Value, request ?? new PublishStorefrontRequest());
        return result.Success ? Ok(result) : MapFailure(result);
    }

    [HttpPost("builder/unpublish")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<StorefrontBuilderStateResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Unpublish()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _builder.UnpublishAsync(userId.Value);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    [HttpPost("builder/reset")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<StorefrontBuilderStateResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResetDraft()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _builder.ResetDraftAsync(userId.Value);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    [HttpPost("builder/template/{templateId}")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<StorefrontBuilderStateResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ApplyTemplate(string templateId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _builder.ApplyTemplateAsync(userId.Value, templateId);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// The business's storefront revision history, newest first.
    ///
    /// <para>Metadata only — a page of history never carries a configuration
    /// payload, so the feed stays cheap. Filtering, paging and the free-text
    /// search all run in SQL.</para>
    /// </summary>
    [HttpGet("builder/revisions")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<StorefrontRevisionListResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListRevisions(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? type = null,
        [FromQuery] bool publishedOnly = false,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? q = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _revisions.ListAsync(
            userId.Value,
            new StorefrontRevisionQuery
            {
                Page = page,
                PageSize = pageSize,
                Type = type,
                PublishedOnly = publishedOnly,
                From = from,
                To = to,
                Search = q,
            },
            cancellationToken);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// One revision with its complete configuration snapshot. A revision belonging
    /// to another business is reported as not found.
    /// </summary>
    [HttpGet("builder/revisions/{revisionId:guid}")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<StorefrontRevisionDetailResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRevision(Guid revisionId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _revisions.GetAsync(userId.Value, revisionId, cancellationToken);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Structured, human-readable differences between two revisions.</summary>
    [HttpGet("builder/revisions/compare")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<StorefrontRevisionCompareResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CompareRevisions(
        [FromQuery] Guid from, [FromQuery] Guid to, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        var result = await _revisions.CompareAsync(userId.Value, from, to, cancellationToken);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Restores a previous revision into the working draft and records the rollback
    /// as a new revision.
    ///
    /// <para>The published storefront is not changed — publishing remains an
    /// explicit, separate action. <c>expectedVersion</c> guards against replacing
    /// newer work with a stale rollback, and <c>confirm</c> must be true so a
    /// destructive restore is never accidental.</para>
    /// </summary>
    [HttpPost("builder/revisions/{revisionId:guid}/restore")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [ProducesResponseType(typeof(ApiResponse<RestoreStorefrontRevisionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RestoreRevision(
        Guid revisionId,
        [FromBody] RestoreStorefrontRevisionRequest? request,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var restored = await _revisions.RestoreAsync(
            userId.Value, revisionId, request ?? new RestoreStorefrontRevisionRequest(), cancellationToken);
        if (!restored.Success) return MapFailure(restored);

        // The editor needs the resulting draft immediately, so the rollback's own
        // metadata and the refreshed builder state travel together.
        var state = await _builder.GetForOwnerAsync(userId.Value);
        if (!state.Success) return MapFailure(state);

        return Ok(ApiResponse<RestoreStorefrontRevisionResponse>.Ok(new RestoreStorefrontRevisionResponse
        {
            Revision = restored.Data!,
            State = state.Data!,
        }));
    }

    /// <summary>The published storefront for a business. Anonymous + tenant-narrowed.</summary>
    [HttpGet("public/{businessId:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<PublicStorefrontDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPublic(Guid businessId, CancellationToken cancellationToken)
    {
        var result = await _builder.GetPublicAsync(businessId, cancellationToken);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    private IActionResult MapFailure<T>(ApiResponse<T> result)
        => result.Error?.Code switch
        {
            "NOT_FOUND" => NotFound(result),
            "STALE_VERSION" => Conflict(result),
            "MEDIA_NOT_FOUND" => NotFound(result),
            "VALIDATION_ERROR" => BadRequest(result),
            _ => BadRequest(result),
        };

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}
