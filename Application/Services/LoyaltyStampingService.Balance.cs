using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// The single balance-mutation routine shared by manual and automatic stamping.
/// </summary>
public partial class LoyaltyStampingService
{
    /// <summary>
    /// Applies a ledger entry, keeps the card counters consistent, and unlocks any
    /// rewards the new balance reaches. Runs inside a row-locked transaction on
    /// relational providers so concurrent qualifying events cannot interleave.
    /// </summary>
    private async Task<ApiResponse<ManualStampResponse>> ApplyBalanceChangeAsync(
        LoyaltyCard card, int amount, StampTransactionDirection direction,
        string source, Guid? sourceId, Guid? earningRuleId, bool isAutomatic,
        string? reason, Guid? actorUserId, string? actorRole,
        string? idempotencyKey, string? metadataJson)
    {
        try
        {
            var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync()
                : null;

            try
            {
                await LockCardForUpdateAsync(card.Id);

                // Re-read under the lock: a concurrent award may already have
                // changed the balance since the card was loaded.
                await _context.Entry(card).ReloadAsync();

                var signed = direction == StampTransactionDirection.Credit ? amount : -amount;

                // A debit may never drive the balance negative; clamp the applied
                // amount so the ledger stays trustworthy.
                if (direction == StampTransactionDirection.Debit && card.TotalStamps + signed < 0)
                    signed = -card.TotalStamps;

                if (signed == 0)
                {
                    if (transaction != null) await transaction.RollbackAsync();
                    return ApiResponse<ManualStampResponse>.Fail(
                        "INVALID_AMOUNT", "The card has no stamps to remove.");
                }

                var now = DateTime.UtcNow;
                var entry = new StampTransaction
                {
                    Id = Guid.NewGuid(),
                    BusinessId = card.BusinessId,
                    ProgramId = card.ProgramId,
                    CardId = card.Id,
                    CustomerId = card.CustomerId,
                    Amount = Math.Abs(signed),
                    Direction = direction,
                    Source = source,
                    SourceId = sourceId,
                    EarningRuleId = earningRuleId,
                    IsAutomatic = isAutomatic,
                    Reason = reason,
                    CreatedByUserId = actorUserId,
                    CreatedByRole = actorRole,
                    IdempotencyKey = idempotencyKey,
                    MetadataJson = metadataJson,
                    CreatedAt = now
                };

                await _unitOfWork.StampTransactions.AddAsync(entry);

                card.TotalStamps += signed;
                if (direction == StampTransactionDirection.Credit) card.LifetimeStamps += amount;
                card.LastStampAt = now;
                _unitOfWork.LoyaltyCards.Update(card);

                var unlocked = await UnlockReachedRewardsAsync(card, card.Program, now);
                await _unitOfWork.SaveChangesAsync();
                if (transaction != null) await transaction.CommitAsync();

                _logger.LogInformation(
                    "Stamp txn {TxnId}: card={CardId} {Direction} {Amount} source={Source} auto={Auto} balance={Balance}",
                    entry.Id, card.Id, direction, entry.Amount, source, isAutomatic, card.TotalStamps);

                return ApiResponse<ManualStampResponse>.Ok(new ManualStampResponse
                {
                    CardId = card.Id,
                    CustomerId = card.CustomerId,
                    Amount = signed,
                    TotalStamps = card.TotalStamps,
                    StampsRequired = card.Program.StampsRequired,
                    Transaction = MapTransaction(entry, card.Program, card.Customer?.FullName, actorRole),
                    UnlockedReward = unlocked.FirstOrDefault()
                });
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
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Unique violation: the idempotency key raced (harmless — the first
            // writer won) or an entitlement unlock raced.
            if (idempotencyKey != null)
            {
                _logger.LogInformation(
                    "Concurrent automatic credit for {IdempotencyKey}; first writer won.", idempotencyKey);
                return ApiResponse<ManualStampResponse>.Fail(
                    "ALREADY_APPLIED", "This stamp award has already been applied.");
            }

            _logger.LogWarning(ex, "Duplicate loyalty write for card {CardId}.", card.Id);
            return ApiResponse<ManualStampResponse>.Fail(
                "DUPLICATE", "This stamp change has already been recorded.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply stamp balance change for card {CardId}.", card.Id);
            return ApiResponse<ManualStampResponse>.Fail("STAMP_FAILED", "Failed to record the stamp change.");
        }
    }
}