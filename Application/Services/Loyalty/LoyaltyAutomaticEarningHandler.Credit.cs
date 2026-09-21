using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Loyalty;

/// <summary>
/// Rule resolution and stamp crediting for the automatic earning handler.
/// </summary>
public sealed partial class LoyaltyAutomaticEarningHandler
{
    /// <summary>
    /// Finds every Active program with an Active + Automatic rule for
    /// <paramref name="source"/> that was already live when the event occurred,
    /// then credits the earning customer's card on that program.
    /// </summary>
    /// <returns>True when at least one rule awarded stamps.</returns>
    private async Task<bool> CreditForSourceAsync(
        Guid businessId,
        EarningSource source,
        DateTime occurredAt,
        Guid earningCustomerId,
        Guid sourceRecordId,
        IReadOnlyList<Guid>? qualifyingServiceIds,
        string reason,
        CancellationToken cancellationToken)
    {
        // No retroactive stamping: the rule must have been activated at or before
        // the event. A rule activated later never back-fills this event.
        var rules = await _context.LoyaltyEarningRules
            .AsNoTracking()
            .Where(r => r.BusinessId == businessId
                && r.Source == source
                && r.Status == EarningRuleStatus.Active
                && r.StampingMode == StampingMode.Automatic
                && r.ActivatedAt != null
                && r.ActivatedAt <= occurredAt
                && r.Program.Status == ProgramStatus.Active)
            .ToListAsync(cancellationToken);

        if (rules.Count == 0) return false;

        var credited = false;

        foreach (var rule in rules)
        {
            // Service rules may be narrowed to one qualifying service.
            if (source == EarningSource.Service
                && rule.QualifyingServiceId.HasValue
                && (qualifyingServiceIds == null
                    || !qualifyingServiceIds.Contains(rule.QualifyingServiceId.Value)))
            {
                continue;
            }

            var card = await _context.LoyaltyCards
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    c => c.BusinessId == businessId
                        && c.CustomerId == earningCustomerId
                        && c.ProgramId == rule.ProgramId,
                    cancellationToken);

            if (card == null)
            {
                // Never auto-enrol: no card means no stamp for this program.
                _logger.LogDebug(
                    "No loyalty card for customer {CustomerId} on program {ProgramId}; automatic stamp skipped.",
                    earningCustomerId, rule.ProgramId);
                continue;
            }

            var result = await _stampingService.CreditFromEventAsync(
                new AutomaticStampCredit(
                    BusinessId: businessId,
                    ProgramId: rule.ProgramId,
                    EarningRuleId: rule.Id,
                    CardId: card.Id,
                    CustomerId: earningCustomerId,
                    Amount: rule.StampAmount,
                    Source: StampTransactions.FromEarningSource(source),
                    SourceId: sourceRecordId,
                    Reason: rule.Description ?? reason),
                cancellationToken);

            if (result.Success)
            {
                credited = true;
            }
            else if (result.Error?.Code is "ALREADY_APPLIED" or "DUPLICATE")
            {
                // Retried event: the first delivery already stamped. Not a failure.
                _logger.LogDebug(
                    "Automatic stamp for {Source}:{SourceId} on program {ProgramId} was already applied.",
                    source, sourceRecordId, rule.ProgramId);
            }
            else
            {
                _logger.LogWarning(
                    "Automatic stamp failed for {Source}:{SourceId} on program {ProgramId}: {Code} {Message}",
                    source, sourceRecordId, rule.ProgramId, result.Error?.Code, result.Error?.Message);
            }
        }

        return credited;
    }
}