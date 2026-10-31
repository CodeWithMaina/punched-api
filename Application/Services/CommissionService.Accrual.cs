using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Accrual - the automatic half of the commission lifecycle, driven by the
/// appointment reaching a terminal status.
///
/// <para>Every method here is <b>idempotent by construction</b>: entries carry a
/// deterministic dedupe key with a unique index, so re-running any of them (a
/// retried request, a replayed callback, a repeated background sweep) adds
/// nothing. Status transitions only ever move forward, so a re-run cannot
/// re-release or re-reverse an entry.</para>
/// </summary>
public partial class CommissionService
{
    /// <summary>Deterministic key for an appointment line earning entry.</summary>
    private static string EarningDedupeKey(Guid appointmentId, Guid appointmentResourceId) =>
        $"commission:appt:{appointmentId}:res:{appointmentResourceId}:earning";

    /// <summary>Deterministic key for the reversal of a given earning entry.</summary>
    private static string ReversalDedupeKey(Guid entryId) => $"commission:reversal:{entryId}";

    /// <summary>
    /// The starting state of a fresh earning:
    /// <list type="bullet">
    /// <item>RequireSettlement to Pending (released when the money arrives).</item>
    /// <item>otherwise, RequireApproval to Approved (awaiting a human release).</item>
    /// <item>otherwise to Payable (ready for payout immediately).</item>
    /// </list>
    /// </summary>
    private static void ApplyInitialStatus(
        CommissionEntry entry, ResolvedCommissionArrangement arrangement, DateTime now)
    {
        if (arrangement.RequireSettlement)
        {
            entry.Status = CommissionEntryStatus.Pending;
            return;
        }

        if (arrangement.RequireApproval)
        {
            entry.Status = CommissionEntryStatus.Approved;
            entry.ApprovedAt = now;
            return;
        }

        entry.Status = CommissionEntryStatus.Payable;
        entry.ApprovedAt = now;
        entry.PayableAt = now;
    }
}

/// <summary>Commission accrual for a terminal appointment.</summary>
public partial class CommissionService
{
    /// <inheritdoc />
    public async Task<int> AccrueForAppointmentAsync(
        Guid appointmentId, CancellationToken cancellationToken = default)
    {
        var appointment = await _context.Appointments
            .Include(a => a.Resources)
            .FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);
        if (appointment == null) return 0;

        var set = await _resolver.LoadAsync(appointment.BusinessId, cancellationToken);
        if (!set.IsEnabled || set.Config == null) return 0;

        var config = set.Config;
        var qualifies =
            appointment.Status == AppointmentStatuses.Completed ||
            (appointment.Status == AppointmentStatuses.NoShow && config.EarnOnNoShow) ||
            (appointment.Status == AppointmentStatuses.Cancelled && config.EarnOnCancelled);

        if (!qualifies)
        {
            // The appointment does not satisfy the business rules for earning
            // commission. Anything already accrued but not yet paid is voided so
            // no unearned money can ever be released. Paid entries are left
            // alone: money that already moved is corrected by an explicit,
            // audited adjustment rather than by rewriting the ledger.
            return await VoidUnreleasedAsync(
                appointment,
                $"Appointment is {appointment.Status}, which does not qualify for commission under this business configuration.",
                cancellationToken);
        }

        // Commission needs somebody to pay. An unassigned appointment accrues nothing.
        if (appointment.StaffUserId is not Guid staffUserId) return 0;

        HashSet<Guid>? eligibleServiceIds = null;
        if (config.RequireStaffServiceEligibility)
        {
            var ids = await _context.StaffServiceAssignments
                .AsNoTracking()
                .Where(s => s.BusinessId == appointment.BusinessId && s.StaffUserId == staffUserId)
                .Select(s => s.ServiceCatalogItemId)
                .ToListAsync(cancellationToken);
            eligibleServiceIds = ids.ToHashSet();
        }

        var now = DateTime.UtcNow;
        var created = 0;

        foreach (var line in appointment.Resources.OrderBy(r => r.SortOrder))
        {
            if (eligibleServiceIds != null && !eligibleServiceIds.Contains(line.ServiceCatalogItemId))
            {
                _logger.LogInformation(
                    "Commission skipped for appointment {AppointmentId} line {LineId}: staff {StaffUserId} is not eligible for service {ServiceId}.",
                    appointment.Id, line.Id, staffUserId, line.ServiceCatalogItemId);
                continue;
            }

            var arrangement = set.Resolve(staffUserId, line.ServiceCatalogItemId);
            if (arrangement == null) continue;

            var amount = CommissionCalculator.Calculate(
                arrangement.CalculationType, arrangement.Percentage, arrangement.FixedAmount,
                line.Price, arrangement.RoundingDecimals);
            if (amount <= 0m) continue;

            var dedupeKey = EarningDedupeKey(appointment.Id, line.Id);
            if (await _context.CommissionEntries.AnyAsync(e => e.DedupeKey == dedupeKey, cancellationToken))
                continue;

            var entry = new CommissionEntry
            {
                Id = Guid.NewGuid(),
                BusinessId = appointment.BusinessId,
                StaffUserId = staffUserId,
                CustomerId = appointment.CustomerId,
                AppointmentId = appointment.Id,
                AppointmentResourceId = line.Id,
                ServiceCatalogItemId = line.ServiceCatalogItemId,
                EntryType = CommissionEntryType.Earning,
                SourceType = CommissionSourceType.Appointment,
                ConfigScope = arrangement.Scope,
                ConfigId = arrangement.ConfigId,
                ConfigSnapshotJson = arrangement.SnapshotJson,
                CalculationType = arrangement.CalculationType,
                RatePercent = arrangement.CalculationType == CommissionCalculationType.Percentage ? arrangement.Percentage : null,
                FixedAmount = arrangement.CalculationType == CommissionCalculationType.Fixed ? arrangement.FixedAmount : null,
                BaseAmount = line.Price,
                Amount = amount,
                Currency = arrangement.Currency,
                DedupeKey = dedupeKey,
                CreatedAt = now,
                UpdatedAt = now
            };

            ApplyInitialStatus(entry, arrangement, now);

            _context.CommissionEntries.Add(entry);
            created++;
        }

        if (created > 0 && !await TrySaveAsync(cancellationToken))
        {
            // A concurrent accrual won the unique key race for one or more
            // lines; the ledger already has them, so there is nothing to add.
            _logger.LogDebug(
                "Commission accrual for appointment {AppointmentId} lost a concurrency race; existing entries retained.",
                appointment.Id);
            return 0;
        }

        // An appointment can already be paid when it completes (a walk-in
        // settled at the counter), so release immediately when the money is there.
        if (created > 0 && config.RequireSettlement)
            await SettleForAppointmentAsync(appointment.BusinessId, appointment.Id, null, cancellationToken);

        return created;
    }
}
