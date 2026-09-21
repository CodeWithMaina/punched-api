using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Filters;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Loyalty earning-rule management for a program.
/// Base route: /v1/loyalty/programs/{programId}/earning-rules
///
/// Server-side entitlement enforcement: [RequireModule("loyalty")] blocks the
/// whole surface with 403 MODULE_DISABLED, and the service layer re-checks
/// entitlements so an in-process call cannot bypass the gate. Referral rules
/// additionally require the Referrals module, enforced in the service.
/// </summary>
[ApiController]
[Route("v1/loyalty/programs/{programId:guid}/earning-rules")]
[Produces("application/json")]
[Authorize(Roles = "Business")]
[RequireModule("loyalty")]
[EnableRateLimiting("general")]
public class LoyaltyEarningRuleController : ControllerBase
{
    private readonly ILoyaltyEarningRuleService _earningRuleService;

    public LoyaltyEarningRuleController(ILoyaltyEarningRuleService earningRuleService)
    {
        _earningRuleService = earningRuleService;
    }

    /// <summary>Lists a program's earning rules, annotated with entitlement availability.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<List<EarningRuleResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRules(Guid programId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _earningRuleService.GetRulesAsync(userId.Value, programId);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Creates or replaces the rule for the request's source.</summary>
    [HttpPut]
    [ProducesResponseType(typeof(ApiResponse<EarningRuleResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpsertRule(Guid programId, [FromBody] UpsertEarningRuleRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _earningRuleService.UpsertRuleAsync(userId.Value, programId, request);

        if (result.Success) return Ok(result);
        return result.Error?.Code == "MODULE_DISABLED"
            ? StatusCode(StatusCodes.Status403Forbidden, result)
            : BadRequest(result);
    }

    /// <summary>
    /// Activates a rule. Automatic rules return an explicit
    /// <c>retroactiveStamping: false</c> notice — existing activity is never
    /// stamped retroactively.
    /// </summary>
    [HttpPost("{ruleId:guid}/activate")]
    [ProducesResponseType(typeof(ApiResponse<EarningRuleActivationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ActivateRule(Guid programId, Guid ruleId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _earningRuleService.ActivateRuleAsync(userId.Value, programId, ruleId);

        if (result.Success) return Ok(result);
        return result.Error?.Code == "MODULE_DISABLED"
            ? StatusCode(StatusCodes.Status403Forbidden, result)
            : BadRequest(result);
    }

    /// <summary>Switches a rule off. It keeps its history and can be re-activated.</summary>
    [HttpPost("{ruleId:guid}/deactivate")]
    [ProducesResponseType(typeof(ApiResponse<EarningRuleResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateRule(Guid programId, Guid ruleId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _earningRuleService.DeactivateRuleAsync(userId.Value, programId, ruleId);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Terminally archives a rule. Historical awards are retained.</summary>
    [HttpPost("{ruleId:guid}/archive")]
    [ProducesResponseType(typeof(ApiResponse<EarningRuleResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ArchiveRule(Guid programId, Guid ruleId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _earningRuleService.ArchiveRuleAsync(userId.Value, programId, ruleId);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Deletes a rule that has never awarded stamps; otherwise archive it.</summary>
    [HttpDelete("{ruleId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteRule(Guid programId, Guid ruleId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _earningRuleService.DeleteRuleAsync(userId.Value, programId, ruleId);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}