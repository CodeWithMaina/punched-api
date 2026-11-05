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

    public StorefrontBuilderController(IStorefrontBuilderService builder)
    {
        _builder = builder;
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
