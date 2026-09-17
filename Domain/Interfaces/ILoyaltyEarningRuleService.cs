using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Business-facing management of a program's earning rules.
///
/// Entitlement rules enforced here (server-side, not just in the UI):
/// <list type="bullet">
/// <item>Every operation requires the <c>loyalty</c> module.</item>
/// <item>A <see cref="Domain.Entities.EarningSource.Referral"/> rule additionally
/// requires the <c>referral</c> module. Without it, referral rules can neither be
/// created nor activated, and are reported as unavailable.</item>
/// </list>
/// </summary>
public interface ILoyaltyEarningRuleService
{
    /// <summary>Lists a program's earning rules, annotating entitlement availability.</summary>
    Task<ApiResponse<List<EarningRuleResponse>>> GetRulesAsync(Guid actorUserId, Guid programId);

    /// <summary>Creates or replaces the rule for the request's source.</summary>
    Task<ApiResponse<EarningRuleResponse>> UpsertRuleAsync(
        Guid actorUserId, Guid programId, UpsertEarningRuleRequest request);

    /// <summary>
    /// Activates a rule. For Automatic rules the response states explicitly that
    /// existing historical activity is not stamped retroactively.
    /// </summary>
    Task<ApiResponse<EarningRuleActivationResponse>> ActivateRuleAsync(
        Guid actorUserId, Guid programId, Guid ruleId);

    /// <summary>Switches a rule off without deleting it. Historical awards remain.</summary>
    Task<ApiResponse<EarningRuleResponse>> DeactivateRuleAsync(
        Guid actorUserId, Guid programId, Guid ruleId);

    /// <summary>Terminally archives a rule. Historical awards remain.</summary>
    Task<ApiResponse<EarningRuleResponse>> ArchiveRuleAsync(
        Guid actorUserId, Guid programId, Guid ruleId);

    /// <summary>Deletes a rule that has never produced a transaction.</summary>
    Task<ApiResponse<bool>> DeleteRuleAsync(Guid actorUserId, Guid programId, Guid ruleId);
}