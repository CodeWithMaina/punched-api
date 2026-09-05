using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PunchedApi.API.Filters;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;
using Microsoft.AspNetCore.RateLimiting;

namespace PunchedApi.API.Controllers;

[ApiController]
[Route("v1/referrals")]
[Authorize]
[RequireModule("referral")]
[EnableRateLimiting("general")]
public class ReferralController : ControllerBase
{
    private readonly IReferralService _referralService;

    public ReferralController(IReferralService referralService)
    {
        _referralService = referralService;
    }

    // ── Referral Program (Business) ─────────────────────────

    /// <summary>Create or update the referral program for the current business.</summary>
    [HttpPut("programs/me")]
    [Authorize(Roles = "Business")]
    public async Task<IActionResult> UpsertProgram([FromBody] UpsertReferralProgramRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _referralService.UpsertProgramAsync(userId.Value, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Get a business's referral program (public).</summary>
    [HttpGet("programs/{businessId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetProgram(Guid businessId)
    {
        var result = await _referralService.GetProgramAsync(businessId);
        return result.Success ? Ok(result) : NotFound(result);
    }

    // ── Referral Links (Customer) ───────────────────────────

    /// <summary>Generate a referral link for a specific business.</summary>
    [HttpPost("links")]
    [Authorize(Roles = "Customer,Business")]
    public async Task<IActionResult> GenerateLink([FromBody] GenerateReferralLinkRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _referralService.GenerateLinkAsync(userId.Value, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Get all my referral links across businesses.</summary>
    [HttpGet("links")]
    [Authorize(Roles = "Customer,Business")]
    public async Task<IActionResult> GetMyLinks()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _referralService.GetMyLinksAsync(userId.Value);
        return Ok(result);
    }

    /// <summary>Get my referral link for a specific business.</summary>
    [HttpGet("links/{businessId:guid}")]
    [Authorize(Roles = "Customer,Business")]
    public async Task<IActionResult> GetLinkForBusiness(Guid businessId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _referralService.GetLinkForBusinessAsync(userId.Value, businessId);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>Records an anonymous referral-link visit before sign-in.</summary>
    [HttpPost("links/{code}/open")]
    [AllowAnonymous]
    public async Task<IActionResult> TrackLinkOpen(string code)
    {
        var result = await _referralService.TrackLinkOpenAsync(code);
        return result.Success ? Ok(result) : NotFound(result);
    }

    // ── Referral Resolution ─────────────────────────────────

    /// <summary>Resolve a referral code — creates referral record and auto-enrolls referee.</summary>
    [HttpPost("resolve")]
    [Authorize(Roles = "Customer,Business")]
    public async Task<IActionResult> ResolveCode([FromBody] ResolveReferralRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _referralService.ResolveCodeAsync(userId.Value, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    // ── Referral Tracking ───────────────────────────────────

    /// <summary>Get all referrals I've made (as referrer).</summary>
    [HttpGet]
    [Authorize(Roles = "Customer,Business")]
    public async Task<IActionResult> GetMyReferrals()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _referralService.GetMyReferralsAsync(userId.Value);
        return Ok(result);
    }

    /// <summary>Get referrals where I'm the referee.</summary>
    [HttpGet("incoming")]
    [Authorize(Roles = "Customer,Business")]
    public async Task<IActionResult> GetIncomingReferrals()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _referralService.GetIncomingReferralsAsync(userId.Value);
        return Ok(result);
    }

    /// <summary>Get my referral statistics.</summary>
    [HttpGet("stats")]
    [Authorize(Roles = "Customer,Business")]
    public async Task<IActionResult> GetMyStats()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _referralService.GetMyStatsAsync(userId.Value);
        return Ok(result);
    }

    /// <summary>
    /// Referral activity overview for the authenticated business owner:
    /// referral counts by lifecycle stage, conversion rate and recent activity.
    /// </summary>
    [HttpGet("business")]
    [Authorize(Roles = "Business")]
    public async Task<IActionResult> GetBusinessReferrals()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _referralService.GetBusinessReferralsAsync(userId.Value);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}
