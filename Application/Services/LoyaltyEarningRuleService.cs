using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Loyalty;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Business-facing management of a program's earning rules.
///
/// Entitlement rules enforced here (server-side, not just in the UI):
/// <list type="bullet">
/// <item>Every operation requires the <c>loyalty</c> module.</item>
/// <item>A referral rule additionally requires the <c>referral</c> module —
/// without it, referral rules can neither be created nor activated.</item>
/// </list>
/// </summary>
public partial class LoyaltyEarningRuleService : ILoyaltyEarningRuleService
{
    internal const string LoyaltyModuleKey = "loyalty";
    internal const string ReferralModuleKey = "referral";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ApplicationDbContext _context;
    private readonly ILoyaltyScopeResolver _scopeResolver;
    private readonly IModuleEntitlementService _entitlementService;
    private readonly ILogger<LoyaltyEarningRuleService> _logger;

    public LoyaltyEarningRuleService(
        IUnitOfWork unitOfWork,
        ApplicationDbContext context,
        ILoyaltyScopeResolver scopeResolver,
        IModuleEntitlementService entitlementService,
        ILogger<LoyaltyEarningRuleService> logger)
    {
        _unitOfWork = unitOfWork;
        _context = context;
        _scopeResolver = scopeResolver;
        _entitlementService = entitlementService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ApiResponse<List<EarningRuleResponse>>> GetRulesAsync(Guid actorUserId, Guid programId)
    {
        var scope = await _scopeResolver.ResolveAsync(actorUserId, "loyalty.view");
        if (!scope.Success)
            return ApiResponse<List<EarningRuleResponse>>.Fail(scope.ErrorCode!, scope.ErrorMessage!);

        var businessId = scope.Actor!.BusinessId;
        if (!await _entitlementService.IsModuleEnabledAsync(businessId, LoyaltyModuleKey))
            return ApiResponse<List<EarningRuleResponse>>.Fail(
                "MODULE_DISABLED", "The 'loyalty' module is not enabled for this business.");

        if (await ResolveProgramAsync(businessId, programId) == null)
            return ApiResponse<List<EarningRuleResponse>>.Fail("NOT_FOUND", "Loyalty program not found.");

        var referralAvailable = await IsReferralAvailableAsync(businessId);
        var rules = await _context.LoyaltyEarningRules
            .AsNoTracking()
            .Where(r => r.ProgramId == programId && r.BusinessId == businessId)
            .OrderBy(r => r.Source)
            .ToListAsync();

        return ApiResponse<List<EarningRuleResponse>>.Ok(
            rules.Select(r => MapRule(r, referralAvailable)).ToList());
    }

    /// <inheritdoc />
    public async Task<ApiResponse<EarningRuleResponse>> UpsertRuleAsync(
        Guid actorUserId, Guid programId, UpsertEarningRuleRequest request)
    {
        var scope = await _scopeResolver.ResolveAsync(actorUserId, "loyalty.manage");
        if (!scope.Success)
            return ApiResponse<EarningRuleResponse>.Fail(scope.ErrorCode!, scope.ErrorMessage!);

        var businessId = scope.Actor!.BusinessId;
        if (!await _entitlementService.IsModuleEnabledAsync(businessId, LoyaltyModuleKey))
            return ApiResponse<EarningRuleResponse>.Fail(
                "MODULE_DISABLED", "The 'loyalty' module is not enabled for this business.");

        if (!TryParseSource(request.Source, out var source))
            return ApiResponse<EarningRuleResponse>.Fail(
                "INVALID_SOURCE", "Source must be one of: appointment, service, referral.");

        if (request.StampAmount < 1 || request.StampAmount > 100)
            return ApiResponse<EarningRuleResponse>.Fail(
                "INVALID_STAMP_AMOUNT", "Stamp amount must be between 1 and 100.");

        if (!TryParseStampingMode(request.StampingMode, out var stampingMode))
            return ApiResponse<EarningRuleResponse>.Fail(
                "INVALID_STAMPING_MODE", "Stamping mode must be 'manual' or 'automatic'.");

        if (!TryParseRuleStatus(request.Status, out var status))
            return ApiResponse<EarningRuleResponse>.Fail(
                "INVALID_STATUS", "Status must be one of: draft, active, inactive, archived.");

        // Cross-module gate: referral earning is only configurable while the
        // business holds the Referrals module.
        var availability = await CheckSourceAvailabilityAsync(businessId, source);
        if (!availability.Available)
            return ApiResponse<EarningRuleResponse>.Fail("MODULE_DISABLED", availability.Reason!);

        var program = await ResolveProgramAsync(businessId, programId);
        if (program == null)
            return ApiResponse<EarningRuleResponse>.Fail("NOT_FOUND", "Loyalty program not found.");

        if (program.Status == ProgramStatus.Archived)
            return ApiResponse<EarningRuleResponse>.Fail(
                "PROGRAM_ARCHIVED", "An archived program cannot configure earning rules.");

        // One rule per source per program: upsert replaces the configuration.
        var rule = await _context.LoyaltyEarningRules
            .FirstOrDefaultAsync(r => r.ProgramId == programId
                && r.BusinessId == businessId
                && r.Source == source);

        if (rule == null)
        {
            rule = new LoyaltyEarningRule
            {
                Id = Guid.NewGuid(),
                ProgramId = programId,
                BusinessId = businessId,
                Source = source,
                CreatedAt = DateTime.UtcNow
            };
            await _unitOfWork.LoyaltyEarningRules.AddAsync(rule);
        }

        rule.StampAmount = request.StampAmount;
        rule.StampingMode = stampingMode;
        rule.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        rule.QualifyingServiceId = source == EarningSource.Service ? request.QualifyingServiceId : null;

        rule.Status = status;

        // A rule upserted directly into Active (never activated through the
        // explicit activate endpoint) must still receive its no-retroactivity
        // instant — otherwise automatic earning would have no start boundary.
        if (status == EarningRuleStatus.Active && rule.ActivatedAt == null)
            rule.ActivatedAt = DateTime.UtcNow;
        else if (status != EarningRuleStatus.Active)
            rule.ActivatedAt = null;

        _unitOfWork.LoyaltyEarningRules.Update(rule);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation(
            "Earning rule upserted: program={ProgramId} source={Source} mode={Mode} status={Status}",
            programId, source, stampingMode, status);

        return ApiResponse<EarningRuleResponse>.Ok(
            MapRule(rule, await IsReferralAvailableAsync(businessId)));
    }
}
