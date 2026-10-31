using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Corrections to the commission ledger: manual adjustments, single-entry
/// reversals and disputes.
///
/// <para>All three append to the ledger. An adjustment is a NEW signed entry
/// linked to the earning it corrects; a reversal is a NEW negative entry that
/// points at the earning it cancels; a dispute changes nothing about the amount -
/// it only holds the entry out of the payable balance until it is resolved. The
/// original entry and its calculation context are therefore always intact and a
/// commission figure can always be explained.</para>
/// </summary>
public partial class CommissionService
{
    /// <summary>Content-derived idempotency key for a manual adjustment.</summary>
    private static string AdjustmentDedupeKey(
        Guid businessId, Guid staffUserId, decimal amount, string reason,
        Guid? adjustmentOfEntryId, Guid? appointmentId, Guid? serviceId)
    {
        var payload = string.Join("|",
            businessId, staffUserId,
            amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            reason, adjustmentOfEntryId, appointmentId, serviceId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return $"commission:adjust:{Convert.ToHexString(hash)[..40]}";
    }

    /// <inheritdoc />
    public async Task<ApiResponse<CommissionEntryResponse>> AdjustAsync(
        Guid callerUserId, string role, CommissionAdjustmentRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<CommissionEntryResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "commissions.manage"))
            return ApiResponse<CommissionEntryResponse>.Fail("FORBIDDEN", "You are not allowed to adjust commissions.");

        if (request.Amount == 0m)
            return ApiResponse<CommissionEntryResponse>.Fail("VALIDATION_ERROR", "An adjustment amount must not be zero.");

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length < 3)
            return ApiResponse<CommissionEntryResponse>.Fail("VALIDATION_ERROR", "A reason of at least 3 characters is required for an adjustment.");

        var isMember = await _context.BusinessMemberships.AsNoTracking().AnyAsync(m =>
            m.BusinessId == businessId.Value &&
            m.UserId == request.StaffUserId &&
            m.Status == BusinessMembershipStatus.Active);
        if (!isMember)
            return ApiResponse<CommissionEntryResponse>.Fail("STAFF_NOT_FOUND", "Staff member not found in this business.");

        CommissionEntry? corrected = null;
        if (request.AdjustmentOfEntryId is Guid targetId)
        {
            corrected = await _context.CommissionEntries.AsNoTracking().FirstOrDefaultAsync(e =>
                e.Id == targetId && e.BusinessId == businessId.Value);
            if (corrected == null)
                return ApiResponse<CommissionEntryResponse>.Fail("NOT_FOUND", "The commission entry being corrected was not found.");

            // The correction must stay inside the same economic context.
            if (corrected.StaffUserId != request.StaffUserId)
                return ApiResponse<CommissionEntryResponse>.Fail(
                    "VALIDATION_ERROR", "The adjustment staff member does not match the entry being corrected.");
        }

        if (request.AppointmentId is Guid appointmentId &&
            !await _context.Appointments.AsNoTracking().AnyAsync(a =>
                a.Id == appointmentId && a.BusinessId == businessId.Value))
            return ApiResponse<CommissionEntryResponse>.Fail("NOT_FOUND", "Appointment not found in this business.");

        if (request.ServiceId is Guid serviceId &&
            !await _context.ServiceCatalogItems.AsNoTracking().AnyAsync(s =>
                s.Id == serviceId && s.BusinessId == businessId.Value))
            return ApiResponse<CommissionEntryResponse>.Fail("SERVICE_NOT_FOUND", "Service not found in this business.");

        // Identical retries of the same adjustment are idempotent: the key is
        // derived from the request content, not from the clock.
        var dedupeKey = AdjustmentDedupeKey(
            businessId.Value, request.StaffUserId, request.Amount, reason,
            request.AdjustmentOfEntryId, request.AppointmentId, request.ServiceId);

        var existing = await _context.CommissionEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.DedupeKey == dedupeKey);
        if (existing != null)
            return ApiResponse<CommissionEntryResponse>.Ok((await MapEntriesAsync(new List<CommissionEntry> { existing }))[0]);

        var set = await _resolver.LoadAsync(businessId.Value);
        var currency = set.Config?.Currency ?? "KES";
        var now = DateTime.UtcNow;

        var entry = new CommissionEntry
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId.Value,
            StaffUserId = request.StaffUserId,
            CustomerId = corrected?.CustomerId,
            AppointmentId = request.AppointmentId ?? corrected?.AppointmentId,
            AppointmentResourceId = corrected?.AppointmentResourceId,
            ServiceCatalogItemId = request.ServiceId ?? corrected?.ServiceCatalogItemId,
            EntryType = CommissionEntryType.Adjustment,
            SourceType = CommissionSourceType.Manual,
            ConfigScope = corrected?.ConfigScope ?? CommissionScope.Business,
            ConfigId = corrected?.ConfigId,
            ConfigSnapshotJson = corrected?.ConfigSnapshotJson ?? "{}",
            CalculationType = CommissionCalculationType.Fixed,
            BaseAmount = 0m,
            Amount = CommissionCalculator.Round(request.Amount, 2),
            Currency = currency,
            Reason = Truncate(reason, 300),
            AdjustmentOfEntryId = request.AdjustmentOfEntryId,
            DedupeKey = dedupeKey,
            CreatedByUserId = callerUserId,
            CreatedAt = now,
            UpdatedAt = now
        };

        ApplyAdjustmentStatus(entry, request.ReleaseImmediately, set.Config?.RequireApproval == true, callerUserId, now);

        _context.CommissionEntries.Add(entry);
        await _context.SaveChangesAsync();

        await NotifyAdjustmentAsync(entry);

        _logger.LogWarning(
            "Commission adjustment {EntryId} created by {UserId} for staff {StaffUserId}: {Amount} {Currency} ({Reason}).",
            entry.Id, callerUserId, entry.StaffUserId, entry.Amount, entry.Currency, entry.Reason);

        return ApiResponse<CommissionEntryResponse>.Ok((await MapEntriesAsync(new List<CommissionEntry> { entry }))[0]);
    }
}

/// <summary>Adjustment status, notification and single-entry reversal.</summary>
public partial class CommissionService
{
    private static void ApplyAdjustmentStatus(
        CommissionEntry entry, bool releaseImmediately, bool requireApproval, Guid callerUserId, DateTime now)
    {
        if (releaseImmediately)
        {
            entry.Status = CommissionEntryStatus.Payable;
            entry.ApprovedAt = now;
            entry.ApprovedByUserId = callerUserId;
            entry.PayableAt = now;
            return;
        }

        if (requireApproval)
        {
            entry.Status = CommissionEntryStatus.Approved;
            entry.ApprovedAt = now;
            entry.ApprovedByUserId = callerUserId;
            return;
        }

        entry.Status = CommissionEntryStatus.Pending;
    }

    private async Task NotifyAdjustmentAsync(CommissionEntry entry)
    {
        await QueueNotificationSafelyAsync(new NotificationRequest(
            "commission.adjusted",
            entry.StaffUserId,
            entry.BusinessId,
            new Dictionary<string, object?>
            {
                ["businessName"] = await GetBusinessNameAsync(entry.BusinessId),
                ["amount"] = entry.Amount,
                ["reason"] = entry.Reason
            },
            $"commission:adjustment:{entry.Id}"));

        await _context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<ApiResponse<CommissionEntryResponse>> ReverseAsync(
        Guid callerUserId, string role, Guid entryId, CommissionReversalRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<CommissionEntryResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "commissions.manage"))
            return ApiResponse<CommissionEntryResponse>.Fail("FORBIDDEN", "You are not allowed to reverse commissions.");

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length < 3)
            return ApiResponse<CommissionEntryResponse>.Fail("VALIDATION_ERROR", "A reason of at least 3 characters is required for a reversal.");

        var entry = await _context.CommissionEntries.FirstOrDefaultAsync(e =>
            e.Id == entryId && e.BusinessId == businessId.Value);
        if (entry == null)
            return ApiResponse<CommissionEntryResponse>.Fail("NOT_FOUND", "Commission entry not found.");

        if (entry.EntryType == CommissionEntryType.Reversal)
            return ApiResponse<CommissionEntryResponse>.Fail("VALIDATION_ERROR", "A reversal entry cannot itself be reversed.");

        if (entry.Status is CommissionEntryStatus.Reversed or CommissionEntryStatus.Void)
            return ApiResponse<CommissionEntryResponse>.Fail(
                "INVALID_STATUS_TRANSITION", $"Commission entry is already {Wire(entry.Status.ToString())}.");

        var now = DateTime.UtcNow;
        var created = await CreateReversalAsync(entry, null, Truncate(reason, 300), now, default);
        if (!created)
            return ApiResponse<CommissionEntryResponse>.Fail("INVALID_STATUS_TRANSITION", "This commission entry has already been reversed.");

        await _context.SaveChangesAsync();

        _logger.LogWarning(
            "Commission entry {EntryId} reversed by {UserId} for business {BusinessId}: {Reason}",
            entryId, callerUserId, businessId.Value, reason);

        var reversal = await _context.CommissionEntries.AsNoTracking()
            .FirstAsync(e => e.ReversesEntryId == entryId);

        return ApiResponse<CommissionEntryResponse>.Ok((await MapEntriesAsync(new List<CommissionEntry> { reversal }))[0]);
    }
}

/// <summary>Commission disputes.</summary>
public partial class CommissionService
{
    /// <inheritdoc />
    public async Task<ApiResponse<CommissionEntryResponse>> DisputeAsync(
        Guid callerUserId, string role, Guid entryId, CommissionDisputeRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<CommissionEntryResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "commissions.manage"))
            return ApiResponse<CommissionEntryResponse>.Fail("FORBIDDEN", "You are not allowed to raise a commission dispute.");

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length < 3)
            return ApiResponse<CommissionEntryResponse>.Fail("VALIDATION_ERROR", "A reason of at least 3 characters is required to dispute a commission.");

        var entry = await _context.CommissionEntries.FirstOrDefaultAsync(e =>
            e.Id == entryId && e.BusinessId == businessId.Value);
        if (entry == null)
            return ApiResponse<CommissionEntryResponse>.Fail("NOT_FOUND", "Commission entry not found.");

        if (entry.Status is not (CommissionEntryStatus.Pending
            or CommissionEntryStatus.Approved
            or CommissionEntryStatus.Payable))
            return ApiResponse<CommissionEntryResponse>.Fail(
                "INVALID_STATUS_TRANSITION",
                $"A {Wire(entry.Status.ToString())} commission cannot be disputed.");

        var now = DateTime.UtcNow;
        entry.Status = CommissionEntryStatus.Disputed;
        entry.DisputedAt = now;
        entry.DisputedByUserId = callerUserId;
        entry.DisputeReason = Truncate(reason, 300);
        entry.Version++;
        entry.UpdatedAt = now;

        await _context.SaveChangesAsync();

        _logger.LogWarning(
            "Commission entry {EntryId} disputed by {UserId}: {Reason}", entryId, callerUserId, reason);

        return ApiResponse<CommissionEntryResponse>.Ok((await MapEntriesAsync(new List<CommissionEntry> { entry }))[0]);
    }

    /// <inheritdoc />
    public async Task<ApiResponse<CommissionEntryResponse>> ResolveDisputeAsync(
        Guid callerUserId, string role, Guid entryId, ResolveCommissionDisputeRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<CommissionEntryResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "commissions.approve"))
            return ApiResponse<CommissionEntryResponse>.Fail("FORBIDDEN", "You are not allowed to resolve commission disputes.");

        var entry = await _context.CommissionEntries.FirstOrDefaultAsync(e =>
            e.Id == entryId && e.BusinessId == businessId.Value);
        if (entry == null)
            return ApiResponse<CommissionEntryResponse>.Fail("NOT_FOUND", "Commission entry not found.");

        if (entry.Status != CommissionEntryStatus.Disputed)
            return ApiResponse<CommissionEntryResponse>.Fail(
                "INVALID_STATUS_TRANSITION", "Only a disputed commission can be resolved.");

        var now = DateTime.UtcNow;
        if (request.Approve)
        {
            entry.Status = CommissionEntryStatus.Payable;
            entry.PayableAt = now;
        }
        else
        {
            entry.Status = CommissionEntryStatus.Void;
            entry.VoidedAt = now;
            entry.VoidReason = Truncate(
                string.IsNullOrWhiteSpace(request.Note)
                    ? "Dispute resolved against the commission."
                    : request.Note.Trim(),
                300);
        }

        entry.Version++;
        entry.UpdatedAt = now;
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Commission dispute on entry {EntryId} resolved by {UserId}: {Outcome}.",
            entryId, callerUserId, request.Approve ? "released" : "voided");

        return ApiResponse<CommissionEntryResponse>.Ok((await MapEntriesAsync(new List<CommissionEntry> { entry }))[0]);
    }
}
