using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Earning-rule activation. Re-checks the source's subscription entitlement, so
/// a downgrade cannot leave referral earning live.
/// </summary>
public partial class LoyaltyEarningRuleService
{
    /// <inheritdoc />
    public async Task<ApiResponse<EarningRuleActivationResponse>> ActivateRuleAsync(
        Guid actorUserId, Guid programId, Guid ruleId)
    {
        var (rule, error) = await LoadForMutationAsync(actorUserId, programId, ruleId);
        if (error != null)
            return ApiResponse<EarningRuleActivationResponse>.Fail(error.Value.Code, error.Value.Message);

        var scope = await _scopeResolver.ResolveAsync(actorUserId, "loyalty.manage");
        var businessId = scope.Actor!.BusinessId;

        var availability = await CheckSourceAvailabilityAsync(businessId, rule!.Source);
        if (!availability.Available)
            return ApiResponse<EarningRuleActivationResponse>.Fail("MODULE_DISABLED", availability.Reason!);

        var program = await ResolveProgramAsync(businessId, programId);
        if (program == null)
            return ApiResponse<EarningRuleActivationResponse>.Fail("NOT_FOUND", "Loyalty program not found.");

        if (program.Status == ProgramStatus.Archived)
            return ApiResponse<EarningRuleActivationResponse>.Fail(
                "PROGRAM_ARCHIVED", "An archived program cannot activate earning rules.");

        rule.Status = EarningRuleStatus.Active;

        // Stamp the activation instant. Automatic earning only considers events
        // at/after this point: that is the mechanism guaranteeing no retroactive
        // stamping — nothing ever scans historical activity.
        rule.ActivatedAt = DateTime.UtcNow;

        _unitOfWork.LoyaltyEarningRules.Update(rule);
        await _unitOfWork.SaveChangesAsync();

        var automatic = rule.StampingMode == StampingMode.Automatic;

        _logger.LogInformation(
            "Earning rule activated: program={ProgramId} source={Source} mode={Mode}",
            programId, rule.Source, rule.StampingMode);

        return ApiResponse<EarningRuleActivationResponse>.Ok(new EarningRuleActivationResponse
        {
            Rule = MapRule(rule, await IsReferralAvailableAsync(businessId)),
            RetroactiveStamping = false,
            Notice = automatic
                ? "Automatic stamping is now on. Only qualifying activity from this point forward will be stamped — existing appointments, services and referrals will not be stamped retroactively."
                : "This rule is now active. Stamps are awarded manually by your team when the qualifying action happens."
        });
    }
}