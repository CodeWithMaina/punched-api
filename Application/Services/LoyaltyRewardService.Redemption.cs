using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Services;

/// <summary>
/// Reward redemption entry point: resolves who is redeeming and for which
/// business, then delegates to the transactional single-use redemption flow.
/// </summary>
public partial class LoyaltyRewardService
{
    /// <inheritdoc />
    public async Task<ApiResponse<RedeemRewardResponse>> RedeemAsync(
        Guid actorUserId, string role, RedeemRewardRequest request)
    {
        if (request.EntitlementId == Guid.Empty)
            return ApiResponse<RedeemRewardResponse>.Fail(
                "INVALID_REQUEST", "An entitlement id is required.");

        var isCustomer = string.Equals(role, "Customer", StringComparison.OrdinalIgnoreCase);

        Guid businessId;

        if (isCustomer)
        {
            // Customers redeem their own entitlements. The business is derived
            // from the entitlement itself, never from client input.
            var ownedBusinessId = await _context.RewardEntitlements
                .AsNoTracking()
                .Where(e => e.Id == request.EntitlementId && e.CustomerId == actorUserId)
                .Select(e => (Guid?)e.BusinessId)
                .FirstOrDefaultAsync();

            if (ownedBusinessId == null)
                return ApiResponse<RedeemRewardResponse>.Fail("NOT_FOUND", "Reward not found.");

            businessId = ownedBusinessId.Value;
        }
        else
        {
            var scope = await _scopeResolver.ResolveAsync(actorUserId, "loyalty.redeem");
            if (!scope.Success)
                return ApiResponse<RedeemRewardResponse>.Fail(scope.ErrorCode!, scope.ErrorMessage!);

            businessId = scope.Actor!.BusinessId;
        }

        // Tenant isolation: the entitlement must be held by this business.
        var inTenant = await _context.RewardEntitlements
            .AsNoTracking()
            .AnyAsync(e => e.Id == request.EntitlementId && e.BusinessId == businessId);

        if (!inTenant)
            return ApiResponse<RedeemRewardResponse>.Fail("NOT_FOUND", "Reward not found.");

        return await ExecuteRedemptionAsync(request.EntitlementId, businessId, actorUserId, role, isCustomer);
    }
}