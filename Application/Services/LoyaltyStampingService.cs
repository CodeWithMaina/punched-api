using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Loyalty;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Owns every mutation of a customer's loyalty stamp balance.
///
/// Invariants enforced here (and asserted by tests):
/// <list type="bullet">
/// <item>The mutable <see cref="LoyaltyCard.TotalStamps"/> always equals the sum
/// of that card's <see cref="StampTransaction"/> signed amounts.</item>
/// <item>Stamps are only awarded while the program is <see cref="ProgramStatus.Active"/>.</item>
/// <item>An automatic award is idempotent per (program, rule, source, sourceId).</item>
/// <item>A reward entitlement is created at most once per (card, reward, cycle).</item>
/// </list>
/// </summary>
public partial class LoyaltyStampingService : ILoyaltyStampingService
{
    private const string LoyaltyModuleKey = "loyalty";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ApplicationDbContext _context;
    private readonly ILoyaltyScopeResolver _scopeResolver;
    private readonly IModuleEntitlementService _entitlementService;
    private readonly ILogger<LoyaltyStampingService> _logger;

    public LoyaltyStampingService(
        IUnitOfWork unitOfWork,
        ApplicationDbContext context,
        ILoyaltyScopeResolver scopeResolver,
        IModuleEntitlementService entitlementService,
        ILogger<LoyaltyStampingService> logger)
    {
        _unitOfWork = unitOfWork;
        _context = context;
        _scopeResolver = scopeResolver;
        _entitlementService = entitlementService;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════
    //  MANUAL STAMPING
    // ═══════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<ApiResponse<ManualStampResponse>> AwardManualStampsAsync(
        Guid actorUserId, ManualStampRequest request)
    {
        if (request.Amount == 0)
            return ApiResponse<ManualStampResponse>.Fail(
                "INVALID_AMOUNT", "Amount must be non-zero.");

        if (Math.Abs(request.Amount) > 100)
            return ApiResponse<ManualStampResponse>.Fail(
                "INVALID_AMOUNT", "Amount must be between -100 and 100.");

        if (string.IsNullOrWhiteSpace(request.Reason))
            return ApiResponse<ManualStampResponse>.Fail(
                "REASON_REQUIRED", "A reason is required for every manual stamp action.");

        var scope = await _scopeResolver.ResolveAsync(actorUserId, "loyalty.stamp");
        if (!scope.Success)
            return ApiResponse<ManualStampResponse>.Fail(scope.ErrorCode!, scope.ErrorMessage!);

        var actor = scope.Actor!;

        // Defence in depth: the [RequireModule] filter already blocks this, but
        // the service layer must not be bypassable by a direct in-process call.
        if (!await _entitlementService.IsModuleEnabledAsync(actor.BusinessId, LoyaltyModuleKey))
            return ApiResponse<ManualStampResponse>.Fail(
                "MODULE_DISABLED", "The 'loyalty' module is not enabled for this business.");

        // Tenant scope: the card must belong to the caller's business.
        var card = await _context.LoyaltyCards
            .Include(c => c.Program)
            .Include(c => c.Customer)
            .FirstOrDefaultAsync(c => c.Id == request.CardId && c.BusinessId == actor.BusinessId);

        if (card == null)
            return ApiResponse<ManualStampResponse>.Fail(
                "NOT_FOUND", "Loyalty card not found for this business.");

        var lifecycleError = ValidateProgramCanEarn(card.Program);
        if (lifecycleError != null)
            return ApiResponse<ManualStampResponse>.Fail(lifecycleError.Value.Code, lifecycleError.Value.Message);

        var isCredit = request.Amount > 0;

        return await ApplyBalanceChangeAsync(
            card,
            amount: Math.Abs(request.Amount),
            direction: isCredit ? StampTransactionDirection.Credit : StampTransactionDirection.Debit,
            source: isCredit ? StampTransactions.Manual : StampTransactions.Adjustment,
            sourceId: null,
            earningRuleId: null,
            isAutomatic: false,
            reason: request.Reason.Trim(),
            actorUserId: actor.UserId,
            actorRole: actor.Role,
            idempotencyKey: null,
            metadataJson: null);
    }
}
