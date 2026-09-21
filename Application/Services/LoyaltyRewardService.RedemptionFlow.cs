using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

/// <summary>
/// The transactional, single-use redemption flow and the tenant-scoped program
/// lookup shared by the reward queries.
/// </summary>
public partial class LoyaltyRewardService
{
    /// <summary>Tenant-scoped program lookup. Never trusts a client-supplied business id.</summary>
    private Task<LoyaltyProgram?> ResolveProgramAsync(Guid businessId, Guid programId) =>
        _context.LoyaltyPrograms
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == programId && p.BusinessId == businessId);

    /// <summary>
    /// Redeems an unlocked entitlement exactly once:
    /// <list type="bullet">
    /// <item>row-locks the entitlement, so two concurrent redemptions converge
    /// on one winner — the loser sees it already redeemed;</item>
    /// <item>creates the <see cref="Redemption"/> payout/audit record with a
    /// hashed fulfilment code (plaintext returned once);</item>
    /// <item>consumes the stamps through an immutable REDEMPTION debit on the
    /// ledger — the customer's history is never rewritten;</item>
    /// <item>increments the card's redemption cycle so the next earning cycle
    /// can unlock the reward again.</item>
    /// </list>
    /// </summary>
    private async Task<ApiResponse<RedeemRewardResponse>> ExecuteRedemptionAsync(
        Guid entitlementId, Guid businessId, Guid actorUserId, string role, bool isCustomer)
    {
        try
        {
            var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync()
                : null;

            try
            {
                return await ExecuteRedemptionCoreAsync(
                    transaction, entitlementId, businessId, actorUserId, role);
            }
            catch
            {
                if (transaction != null) await transaction.RollbackAsync();
                throw;
            }
            finally
            {
                if (transaction != null) await transaction.DisposeAsync();
            }
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(
                ex, "Concurrent redemption race for entitlement {EntitlementId}.", entitlementId);
            return ApiResponse<RedeemRewardResponse>.Fail(
                "ALREADY_REDEEMED", "This reward has already been redeemed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redemption failed for entitlement {EntitlementId}.", entitlementId);
            return ApiResponse<RedeemRewardResponse>.Fail(
                "REDEMPTION_FAILED", "Failed to redeem the reward. Please try again.");
        }
    }

    /// <summary>Locked, re-validated redemption body.</summary>
    private async Task<ApiResponse<RedeemRewardResponse>> ExecuteRedemptionCoreAsync(
        IDbContextTransaction? transaction, Guid entitlementId, Guid businessId,
        Guid actorUserId, string role)
    {
        // Serialise concurrent redemptions of the same entitlement.
        await LockEntitlementForUpdateAsync(entitlementId);

        var entitlement = await _context.RewardEntitlements
            .Include(e => e.Card)
            .ThenInclude(c => c.Program)
            .Include(e => e.Reward)
            .FirstOrDefaultAsync(e => e.Id == entitlementId && e.BusinessId == businessId);

        if (entitlement == null)
        {
            if (transaction != null) await transaction.RollbackAsync();
            return ApiResponse<RedeemRewardResponse>.Fail("NOT_FOUND", "Reward not found.");
        }

        if (entitlement.Status == RewardEntitlementStatus.Redeemed)
        {
            if (transaction != null) await transaction.RollbackAsync();
            return ApiResponse<RedeemRewardResponse>.Fail(
                "ALREADY_REDEEMED", "This reward has already been redeemed.");
        }

        if (entitlement.Status != RewardEntitlementStatus.Unlocked)
        {
            if (transaction != null) await transaction.RollbackAsync();
            return ApiResponse<RedeemRewardResponse>.Fail(
                "NOT_REDEEMABLE", "This reward is no longer available to redeem.");
        }

        if (entitlement.ExpiresAt.HasValue && entitlement.ExpiresAt <= DateTime.UtcNow)
        {
            // Expire lazily: the data stays for the audit trail.
            entitlement.Status = RewardEntitlementStatus.Expired;
            _unitOfWork.RewardEntitlements.Update(entitlement);
            await _unitOfWork.SaveChangesAsync();
            if (transaction != null) await transaction.CommitAsync();
            return ApiResponse<RedeemRewardResponse>.Fail(
                "EXPIRED", "This reward expired before it could be redeemed.");
        }

        var stampsToConsume = Math.Max(0, entitlement.StampsToConsume);

        if (entitlement.Card.TotalStamps < stampsToConsume)
        {
            if (transaction != null) await transaction.RollbackAsync();
            return ApiResponse<RedeemRewardResponse>.Fail(
                "INSUFFICIENT_STAMPS",
                $"This reward needs {stampsToConsume} stamps but the card only has {entitlement.Card.TotalStamps}.");
        }

        // 1) Consume stamps through the immutable ledger (auditable debit).
        if (stampsToConsume > 0)
        {
            var consumption = await _stampingService.ConsumeForRedemptionAsync(
                businessId,
                entitlement.CardId,
                stampsToConsume,
                entitlement.Id,
                $"Redeemed reward: {entitlement.RewardName}");

            if (!consumption.Success)
            {
                if (transaction != null) await transaction.RollbackAsync();
                return ApiResponse<RedeemRewardResponse>.Fail(
                    consumption.Error!.Code, consumption.Error!.Message);
            }
        }

        // 2) Payout/audit record + one-time fulfilment code.
        var fulfilmentCode = GenerateFulfilmentCode();
        var redemption = new Redemption
        {
            Id = Guid.NewGuid(),
            CardId = entitlement.CardId,
            BusinessId = businessId,
            PerformedByUserId = actorUserId,
            PerformedByRole = role,
            RewardValue = entitlement.RewardValue,
            Status = RedemptionStatus.Pending,
            StampsConsumed = stampsToConsume,
            FulfilmentCodeHash = HashToken(fulfilmentCode),
            RedeemedAt = DateTime.UtcNow
        };
        await _unitOfWork.Redemptions.AddAsync(redemption);

        // 3) Single-use entitlement: redeemed state is terminal and the row is
        //    retained for history — never deleted.
        entitlement.Status = RewardEntitlementStatus.Redeemed;
        entitlement.RedeemedAt = redemption.RedeemedAt;
        entitlement.RedemptionId = redemption.Id;
        _unitOfWork.RewardEntitlements.Update(entitlement);

        await _unitOfWork.SaveChangesAsync();
        if (transaction != null) await transaction.CommitAsync();

        _logger.LogInformation(
            "Reward entitlement {EntitlementId} redeemed on card {CardId} by {Role} {ActorId}; " +
            "{Stamps} stamps consumed, redemption {RedemptionId}.",
            entitlement.Id, entitlement.CardId, role, actorUserId, stampsToConsume, redemption.Id);

        return ApiResponse<RedeemRewardResponse>.Ok(new RedeemRewardResponse
        {
            Entitlement = MapEntitlement(
                entitlement, entitlement.Card.Program?.Name, entitlement.Card.Customer?.FullName),
            RedemptionId = redemption.Id,
            StampsConsumed = stampsToConsume,
            RemainingStamps = entitlement.Card.TotalStamps,
            FulfilmentCode = fulfilmentCode
        });
    }
}
