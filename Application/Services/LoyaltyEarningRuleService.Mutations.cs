using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Non-activating earning-rule mutations and the shared mutation guard.
/// </summary>
public partial class LoyaltyEarningRuleService
{
    /// <inheritdoc />
    public async Task<ApiResponse<EarningRuleResponse>> DeactivateRuleAsync(
        Guid actorUserId, Guid programId, Guid ruleId)
    {
        var (rule, error) = await LoadForMutationAsync(actorUserId, programId, ruleId);
        if (error != null) return ApiResponse<EarningRuleResponse>.Fail(error.Value.Code, error.Value.Message);

        rule!.Status = EarningRuleStatus.Inactive;
        rule.ActivatedAt = null;

        _unitOfWork.LoyaltyEarningRules.Update(rule);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Earning rule deactivated: program={ProgramId} rule={RuleId}", programId, ruleId);

        var scope = await _scopeResolver.ResolveAsync(actorUserId);
        return ApiResponse<EarningRuleResponse>.Ok(
            MapRule(rule, await IsReferralAvailableAsync(scope.Actor!.BusinessId)));
    }

    /// <inheritdoc />
    public async Task<ApiResponse<EarningRuleResponse>> ArchiveRuleAsync(
        Guid actorUserId, Guid programId, Guid ruleId)
    {
        var (rule, error) = await LoadForMutationAsync(actorUserId, programId, ruleId);
        if (error != null) return ApiResponse<EarningRuleResponse>.Fail(error.Value.Code, error.Value.Message);

        rule!.Status = EarningRuleStatus.Archived;
        rule.ActivatedAt = null;

        _unitOfWork.LoyaltyEarningRules.Update(rule);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Earning rule archived: program={ProgramId} rule={RuleId}", programId, ruleId);

        var scope = await _scopeResolver.ResolveAsync(actorUserId);
        return ApiResponse<EarningRuleResponse>.Ok(
            MapRule(rule, await IsReferralAvailableAsync(scope.Actor!.BusinessId)));
    }

    /// <inheritdoc />
    public async Task<ApiResponse<bool>> DeleteRuleAsync(Guid actorUserId, Guid programId, Guid ruleId)
    {
        var (rule, error) = await LoadForMutationAsync(actorUserId, programId, ruleId);
        if (error != null) return ApiResponse<bool>.Fail(error.Value.Code, error.Value.Message);

        // A rule that already produced transactions is part of the audit trail
        // and must never be deleted — archive it instead.
        var hasHistory = await _context.StampTransactions
            .AsNoTracking()
            .AnyAsync(t => t.EarningRuleId == ruleId);

        if (hasHistory)
            return ApiResponse<bool>.Fail(
                "RULE_IN_USE",
                "This rule has already awarded stamps, so it cannot be deleted. Archive it instead to keep the audit trail intact.");

        _unitOfWork.LoyaltyEarningRules.Delete(rule!);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Earning rule deleted: program={ProgramId} rule={RuleId}", programId, ruleId);
        return ApiResponse<bool>.Ok(true);
    }

    /// <summary>
    /// Resolves actor scope, the loyalty entitlement, and a tenant-scoped rule.
    /// Returns a failure tuple when any check fails.
    /// </summary>
    private async Task<(LoyaltyEarningRule? Rule, (string Code, string Message)? Error)> LoadForMutationAsync(
        Guid actorUserId, Guid programId, Guid ruleId)
    {
        var scope = await _scopeResolver.ResolveAsync(actorUserId, "loyalty.manage");
        if (!scope.Success)
            return (null, (scope.ErrorCode!, scope.ErrorMessage!));

        var businessId = scope.Actor!.BusinessId;

        if (!await _entitlementService.IsModuleEnabledAsync(businessId, LoyaltyModuleKey))
            return (null, ("MODULE_DISABLED", "The 'loyalty' module is not enabled for this business."));

        var rule = await _context.LoyaltyEarningRules
            .FirstOrDefaultAsync(r => r.Id == ruleId
                && r.ProgramId == programId
                && r.BusinessId == businessId);

        return rule == null
            ? (null, ("NOT_FOUND", "Earning rule not found."))
            : (rule, null);
    }
}