using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

[ApiController]
[Route("v1")]
[EnableRateLimiting("general")]
public sealed class ReviewController : ControllerBase
{
    private readonly IReviewService _reviews;
    private readonly IBusinessContext _businessContext;

    public ReviewController(IReviewService reviews, IBusinessContext businessContext)
    { _reviews = reviews; _businessContext = businessContext; }

    [HttpPost("reviews")]
    [Authorize(Roles = "Customer")]
    public async Task<IActionResult> Create([FromBody] CreateReviewRequest request)
    { var id = UserId(); return id == null ? Unauthorized() : Map(await _reviews.CreateAsync(id.Value, request)); }

    [HttpGet("appointments/{appointmentId:guid}/review")]
    [Authorize(Roles = "Customer")]
    public async Task<IActionResult> GetAppointmentState(Guid appointmentId)
    { var id = UserId(); return id == null ? Unauthorized() : Map(await _reviews.GetAppointmentStateAsync(id.Value, appointmentId)); }

    [HttpGet("customers/me/reviews")]
    [Authorize(Roles = "Customer")]
    public async Task<IActionResult> GetMine([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    { var id = UserId(); return id == null ? Unauthorized() : Map(await _reviews.GetCustomerReviewsAsync(id.Value, page, pageSize)); }

    [HttpPut("reviews/{reviewId:guid}")]
    [Authorize(Roles = "Customer")]
    public async Task<IActionResult> Update(Guid reviewId, [FromBody] UpdateReviewRequest request)
    { var id = UserId(); return id == null ? Unauthorized() : Map(await _reviews.UpdateAsync(id.Value, reviewId, request)); }

    [HttpGet("businesses/{businessId:guid}/reviews")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPublic(Guid businessId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    => Map(await _reviews.GetBusinessReviewsAsync(businessId, page, pageSize));

    [HttpGet("businesses/{businessId:guid}/reviews/summary")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSummary(Guid businessId) => Map(await _reviews.GetSummaryAsync(businessId));

    [HttpGet("businesses/me/reviews")]
    [Authorize(Roles = "Business")]
    public async Task<IActionResult> GetOwner([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var businessId = await _businessContext.GetBusinessIdAsync();
        return businessId == null ? Unauthorized() : Map(await _reviews.GetOwnerReviewsAsync(businessId.Value, page, pageSize));
    }

    private IActionResult Map<T>(ApiResponse<T> result) => result.Success ? Ok(result) : result.Error?.Code switch
    {
        "NOT_FOUND" or "REVIEW_NOT_ELIGIBLE" => NotFound(result),
        "REVIEW_ALREADY_EXISTS" or "REVIEW_EDIT_WINDOW_EXPIRED" => Conflict(result),
        _ => BadRequest(result)
    };

    private Guid? UserId() => Guid.TryParse(User.FindFirst("userId")?.Value, out var id) ? id : null;
}
