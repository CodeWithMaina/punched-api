using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Settlement and reversal of accrued commission.
///
/// <para><b>Settlement.</b> An appointment's pending earnings are released when
/// the money they represent has actually arrived. The rule is configurable per
/// business: a business may release on any successful payment, or only once a
/// configurable fraction of the appointment's value has been received.</para>
///
/// <para><b>Reversal.</b> A refund or a payment reversal must not silently
/// delete history. Unpaid entries are marked <c>Reversed</c> and paired with a
/// negative reversal entry; entries that were already PAID keep their
/// <c>Paid</c> status and receive a negative <c>Payable</c> clawback entry that
/// nets against the staff member's next payout. Either way the ledger explains
/// exactly what happened and the balance always reconciles.</para>
/// </summary>
public partial class CommissionService
{
    /// <inheritdoc />
    public async Task<int> SettleForAppointmentAsync(
        Guid businessId, Guid appointmentId, Guid? paymentId, CancellationToken cancellationToken = default)
    {
        var set = await _resolver.LoadAsync(businessId, cancellationToken);
        if (!set.IsEnabled || set.Config == null) return 0;

        var config = set.Config;
        if (!config.RequireSettlement) return 0;

        // Amounts are aggregated in memory from a narrow projection: the SQLite
        // provider (used by the test suite) cannot translate SUM over decimal,
        // and an appointment has only a handful of resource lines and payments.
        var linePrices = await _context.AppointmentResources
            .AsNoTracking()
            .Where(r => r.AppointmentId == appointmentId)
            .Select(r => r.Price)
            .ToListAsync(cancellationToken);
        var totalValue = linePrices.Sum();
        if (totalValue <= 0m) return 0;

        var paidAmounts = await _context.Payments
            .AsNoTracking()
            .Where(p => p.BusinessId == businessId &&
                        p.AppointmentId == appointmentId &&
                        p.Status == PaymentStatus.Success)
            .Select(p => p.Amount)
            .ToListAsync(cancellationToken);
        var settled = paidAmounts.Sum();

        // Nothing received yet: the earning stays pending.
        if (settled <= 0m) return 0;

        var ratio = settled / totalValue;
        if (!config.AllowPartialSettlement && ratio < 1m) return 0;
        if (config.MinimumSettlementRatio > 0m && ratio < config.MinimumSettlementRatio) return 0;

        var pending = await _context.CommissionEntries
            .Where(e => e.BusinessId == businessId &&
                        e.AppointmentId == appointmentId &&
                        e.EntryType == CommissionEntryType.Earning &&
                        e.Status == CommissionEntryStatus.Pending)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var entry in pending)
        {
            entry.Status = config.RequireApproval
                ? CommissionEntryStatus.Approved
                : CommissionEntryStatus.Payable;
            entry.ApprovedAt = now;
            if (entry.Status == CommissionEntryStatus.Payable) entry.PayableAt = now;
            if (paymentId.HasValue) entry.PaymentId = paymentId;
            entry.Version++;
            entry.UpdatedAt = now;
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Released {Count} commission entr(ies) for appointment {AppointmentId} (settled {Settled} of {Total}).",
            pending.Count, appointmentId, settled, totalValue);

        return pending.Count;
    }
}


/// <summary>Reversal and voiding of accrued commission.</summary>
public partial class CommissionService
{
    /// <inheritdoc />
    public async Task<int> ReverseForPaymentAsync(
        Guid businessId, Guid appointmentId, Guid? paymentId, string reason,
        CancellationToken cancellationToken = default)
    {
        var entries = await _context.CommissionEntries
            .Where(e => e.BusinessId == businessId &&
                        e.AppointmentId == appointmentId &&
                        e.EntryType == CommissionEntryType.Earning &&
                        e.Status != CommissionEntryStatus.Reversed &&
                        e.Status != CommissionEntryStatus.Void)
            .ToListAsync(cancellationToken);
        if (entries.Count == 0) return 0;

        var now = DateTime.UtcNow;
        var reversalReason = string.IsNullOrWhiteSpace(reason)
            ? "Payment reversed or refunded"
            : Truncate(reason.Trim(), 300);

        var reversed = 0;
        foreach (var entry in entries)
        {
            if (await CreateReversalAsync(entry, paymentId, reversalReason, now, cancellationToken))
                reversed++;
        }

        if (reversed > 0) await _context.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "Reversed {Count} commission entr(ies) for appointment {AppointmentId}: {Reason}",
            reversed, appointmentId, reversalReason);

        return reversed;
    }

    /// <summary>
    /// Voids everything accrued for an appointment that does not qualify for
    /// commission, leaving paid entries untouched. Idempotent: a second call
    /// finds nothing left to void.
    /// </summary>
    private async Task<int> VoidUnreleasedAsync(
        Appointment appointment, string reason, CancellationToken cancellationToken)
    {
        var candidates = await _context.CommissionEntries
            .Where(e => e.BusinessId == appointment.BusinessId &&
                        e.AppointmentId == appointment.Id &&
                        e.EntryType == CommissionEntryType.Earning &&
                        (e.Status == CommissionEntryStatus.Pending ||
                         e.Status == CommissionEntryStatus.Approved ||
                         e.Status == CommissionEntryStatus.Payable ||
                         e.Status == CommissionEntryStatus.Disputed))
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var entry in candidates)
        {
            entry.Status = CommissionEntryStatus.Void;
            entry.VoidedAt = now;
            entry.VoidReason = Truncate(reason, 300);
            entry.Version++;
            entry.UpdatedAt = now;
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Voided {Count} unreleased commission entr(ies) for appointment {AppointmentId}: {Reason}",
            candidates.Count, appointment.Id, reason);

        return candidates.Count;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>Reversal entry creation and the shared save guard.</summary>
public partial class CommissionService
{
    /// <summary>
    /// Creates the reversal of one earning. Returns false when a reversal
    /// already exists (idempotency) or when the entry cannot be reversed.
    /// </summary>
    private async Task<bool> CreateReversalAsync(
        CommissionEntry entry, Guid? paymentId, string reason, DateTime now, CancellationToken cancellationToken)
    {
        if (entry.ReversedByEntryId.HasValue) return false;
        if (await _context.CommissionEntries.AnyAsync(
                e => e.DedupeKey == ReversalDedupeKey(entry.Id), cancellationToken))
            return false;

        // A paid entry is never rewritten: the money moved, so the correction is
        // a negative clawback that nets against the next payout.
        var wasPaid = entry.Status == CommissionEntryStatus.Paid;

        var reversal = new CommissionEntry
        {
            Id = Guid.NewGuid(),
            BusinessId = entry.BusinessId,
            StaffUserId = entry.StaffUserId,
            CustomerId = entry.CustomerId,
            AppointmentId = entry.AppointmentId,
            AppointmentResourceId = entry.AppointmentResourceId,
            ServiceCatalogItemId = entry.ServiceCatalogItemId,
            PaymentId = paymentId ?? entry.PaymentId,
            EntryType = CommissionEntryType.Reversal,
            SourceType = entry.SourceType,
            ConfigScope = entry.ConfigScope,
            ConfigId = entry.ConfigId,
            ConfigSnapshotJson = entry.ConfigSnapshotJson,
            CalculationType = entry.CalculationType,
            RatePercent = entry.RatePercent,
            FixedAmount = entry.FixedAmount,
            BaseAmount = entry.BaseAmount,
            Amount = -entry.Amount,
            Currency = entry.Currency,
            Reason = reason,
            ReversesEntryId = entry.Id,
            DedupeKey = ReversalDedupeKey(entry.Id),
            CreatedByUserId = entry.CreatedByUserId,
            CreatedAt = now,
            UpdatedAt = now
        };

        if (wasPaid)
        {
            // Recoverable from future earnings: the clawback is payable.
            reversal.Status = CommissionEntryStatus.Payable;
            reversal.ApprovedAt = now;
            reversal.PayableAt = now;
        }
        else
        {
            reversal.Status = CommissionEntryStatus.Reversed;
            reversal.ReversedAt = now;
        }

        entry.ReversedByEntryId = reversal.Id;
        entry.Version++;
        entry.UpdatedAt = now;
        if (!wasPaid)
        {
            entry.Status = CommissionEntryStatus.Reversed;
            entry.ReversedAt = now;
        }

        _context.CommissionEntries.Add(reversal);
        return true;
    }

    /// <summary>
    /// Saves, returning false when the write lost a unique-key race and
    /// detaching the failed inserts so the caller can continue.
    /// </summary>
    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            foreach (var entry in _context.ChangeTracker.Entries()
                         .Where(e => e.State == EntityState.Added)
                         .ToList())
            {
                entry.State = EntityState.Detached;
            }

            return false;
        }
    }
}

