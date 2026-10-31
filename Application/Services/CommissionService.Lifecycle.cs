using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// The human half of the commission lifecycle: approval and payout recording.
///
/// <para><b>Authorization.</b> Every operation re-checks the caller business-scoped
/// permission inside the service - payouts and approvals can never be reached by
/// a caller who merely knows an id. Staff members hold commissions.view only, so
/// they can see their own earnings and nothing else.</para>
///
/// <para><b>Auditability.</b> Nothing here rewrites an amount. Approvals move a
/// status forward and payouts attribute already-earned money to a real payment
/// reference.</para>
/// </summary>
public partial class CommissionService
{
    /// <inheritdoc />
    public async Task<ApiResponse<List<CommissionEntryResponse>>> ApproveAsync(
        Guid callerUserId, string role, ApproveCommissionEntriesRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<List<CommissionEntryResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "commissions.approve"))
            return ApiResponse<List<CommissionEntryResponse>>.Fail("FORBIDDEN", "You are not allowed to approve commissions.");

        var query = _context.CommissionEntries
            .Where(e => e.BusinessId == businessId.Value &&
                        e.Status == CommissionEntryStatus.Approved);

        if (request.EntryIds is { Length: > 0 })
        {
            var ids = request.EntryIds.Distinct().ToArray();
            query = query.Where(e => ids.Contains(e.Id));
        }
        else
        {
            if (request.StaffUserId.HasValue) query = query.Where(e => e.StaffUserId == request.StaffUserId.Value);
            if (request.To.HasValue) query = query.Where(e => e.CreatedAt <= request.To.Value);
        }

        var entries = await query.ToListAsync();
        if (entries.Count == 0)
            return ApiResponse<List<CommissionEntryResponse>>.Fail(
                "NOT_FOUND", "No commissions are awaiting approval for the given selection.");

        var now = DateTime.UtcNow;
        foreach (var entry in entries)
        {
            entry.Status = CommissionEntryStatus.Payable;
            entry.PayableAt = now;
            if (entry.ApprovedAt == null)
            {
                entry.ApprovedAt = now;
                entry.ApprovedByUserId = callerUserId;
            }

            entry.Version++;
            entry.UpdatedAt = now;
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "User {UserId} released {Count} commission entr(ies) for business {BusinessId}.",
            callerUserId, entries.Count, businessId.Value);

        // Each affected professional is told their earning is now payable. The
        // queue call is per-recipient and idempotency-keyed on the entry, so a
        // retried approval cannot double-notify.
        var businessName = await GetBusinessNameAsync(businessId.Value);
        foreach (var entry in entries)
        {
            if (entry.StaffUserId == Guid.Empty) continue;

            await QueueNotificationSafelyAsync(new NotificationRequest(
                "commission.approved",
                entry.StaffUserId,
                entry.BusinessId,
                new Dictionary<string, object?>
                {
                    ["businessName"] = businessName,
                    ["amount"] = entry.Amount,
                    ["appointmentId"] = entry.AppointmentId
                },
                $"commission:approved:{entry.Id}"));
        }

        await _context.SaveChangesAsync();

        return ApiResponse<List<CommissionEntryResponse>>.Ok(await MapEntriesAsync(entries));
    }
}

/// <summary>Payout recording.</summary>
public partial class CommissionService
{
    /// <inheritdoc />
    public async Task<ApiResponse<CommissionPayoutResponse>> RecordPayoutAsync(
        Guid callerUserId, string role, RecordCommissionPayoutRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<CommissionPayoutResponse>.Fail("NOT_FOUND", "No business found for this account.");

        // Recording a payout is a critical money operation; it is gated on its
        // own permission so a manager cannot settle staff pay.
        if (!await HasPermissionAsync(callerUserId, businessId.Value, "commissions.payout"))
            return ApiResponse<CommissionPayoutResponse>.Fail("FORBIDDEN", "You are not allowed to record commission payouts.");

        var reference = request.Reference?.Trim() ?? string.Empty;
        if (reference.Length == 0)
            return ApiResponse<CommissionPayoutResponse>.Fail("VALIDATION_ERROR", "A payout reference is required.");
        if (reference.Length > 100)
            return ApiResponse<CommissionPayoutResponse>.Fail("VALIDATION_ERROR", "A payout reference may be at most 100 characters.");

        var isMember = await _context.BusinessMemberships.AsNoTracking().AnyAsync(m =>
            m.BusinessId == businessId.Value &&
            m.UserId == request.StaffUserId &&
            m.Status == BusinessMembershipStatus.Active);
        if (!isMember)
            return ApiResponse<CommissionPayoutResponse>.Fail("STAFF_NOT_FOUND", "Staff member not found in this business.");

        // A reference identifies a real payment, so it can only be used once.
        if (await _context.CommissionPayouts.AnyAsync(p =>
                p.BusinessId == businessId.Value && p.Reference == reference))
            return ApiResponse<CommissionPayoutResponse>.Fail(
                "PAYOUT_REFERENCE_EXISTS", "A payout with this reference has already been recorded.");

        await using var transaction = await _context.Database.BeginTransactionAsync();

        var query = _context.CommissionEntries
            .Where(e => e.BusinessId == businessId.Value &&
                        e.StaffUserId == request.StaffUserId &&
                        e.Status == CommissionEntryStatus.Payable);

        if (request.EntryIds is { Length: > 0 })
        {
            var ids = request.EntryIds.Distinct().ToArray();
            query = query.Where(e => ids.Contains(e.Id));
        }
        else
        {
            if (request.PeriodStart.HasValue) query = query.Where(e => e.CreatedAt >= request.PeriodStart.Value);
            if (request.PeriodEnd.HasValue) query = query.Where(e => e.CreatedAt <= request.PeriodEnd.Value);
        }

        var entries = await query.ToListAsync();
        if (entries.Count == 0)
        {
            await transaction.RollbackAsync();
            return ApiResponse<CommissionPayoutResponse>.Fail(
                "NOT_FOUND", "There is no payable commission for this staff member in the given selection.");
        }

        // A payout must never be negative: if clawbacks outweigh the earnings
        // being settled, the caller must settle a larger set.
        var total = entries.Sum(e => e.Amount);
        if (total <= 0m)
        {
            await transaction.RollbackAsync();
            return ApiResponse<CommissionPayoutResponse>.Fail(
                "VALIDATION_ERROR", "The selected entries net to zero or less; widen the selection before recording a payout.");
        }

        var now = DateTime.UtcNow;
        var payout = new CommissionPayout
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId.Value,
            StaffUserId = request.StaffUserId,
            Reference = reference,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : Truncate(request.Notes.Trim(), 500),
            TotalAmount = CommissionCalculator.Round(total, 2),
            Currency = entries[0].Currency,
            EntryCount = entries.Count,
            PeriodStartUtc = request.PeriodStart,
            PeriodEndUtc = request.PeriodEnd,
            PaidAtUtc = now,
            CreatedByUserId = callerUserId,
            CreatedAt = now
        };

        _context.CommissionPayouts.Add(payout);

        foreach (var entry in entries)
        {
            entry.Status = CommissionEntryStatus.Paid;
            entry.PaidAt = now;
            entry.PayoutId = payout.Id;
            entry.Version++;
            entry.UpdatedAt = now;
        }

        await _context.SaveChangesAsync();

        await QueueNotificationSafelyAsync(new NotificationRequest(
            "commission.payout_recorded",
            payout.StaffUserId,
            payout.BusinessId,
            new Dictionary<string, object?>
            {
                ["businessName"] = await GetBusinessNameAsync(payout.BusinessId),
                ["amount"] = payout.TotalAmount,
                ["reference"] = payout.Reference
            },
            $"commission:payout:{payout.Id}"));

        await _context.SaveChangesAsync();
        await transaction.CommitAsync();

        _logger.LogInformation(
            "Commission payout {PayoutId} recorded for staff {StaffUserId} by {UserId}: {Amount} {Currency} across {Count} entr(ies), reference {Reference}.",
            payout.Id, payout.StaffUserId, callerUserId, payout.TotalAmount, payout.Currency, payout.EntryCount, payout.Reference);

        return ApiResponse<CommissionPayoutResponse>.Ok(await MapPayoutAsync(payout, entries));
    }
}

/// <summary>Payout history, notification helper and payout mapping.</summary>
public partial class CommissionService
{
    /// <summary>
    /// Queues a notification inside the caller open unit of work. Notification
    /// problems are logged and never roll back an otherwise valid financial
    /// operation.
    /// </summary>
    private async Task QueueNotificationSafelyAsync(NotificationRequest request)
    {
        try
        {
            await _notifications.QueueAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Notification {NotificationType} could not be queued for recipient {RecipientUserId}.",
                request.Type, request.RecipientUserId);
        }
    }

    /// <inheritdoc />
    public async Task<ApiResponse<List<CommissionPayoutResponse>>> GetPayoutsAsync(
        Guid callerUserId, string role, Guid? staffUserId)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<List<CommissionPayoutResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "commissions.view"))
            return ApiResponse<List<CommissionPayoutResponse>>.Fail("FORBIDDEN", "You are not allowed to view commissions.");

        var query = _context.CommissionPayouts.AsNoTracking()
            .Where(p => p.BusinessId == businessId.Value);

        if (IsRole(role, "Staff"))
            query = query.Where(p => p.StaffUserId == callerUserId);
        else if (staffUserId.HasValue)
            query = query.Where(p => p.StaffUserId == staffUserId.Value);

        var payouts = await query.OrderByDescending(p => p.PaidAtUtc).Take(200).ToListAsync();
        if (payouts.Count == 0)
            return ApiResponse<List<CommissionPayoutResponse>>.Ok(new List<CommissionPayoutResponse>());

        var payoutIds = payouts.Select(p => p.Id).ToList();
        var lines = await _context.CommissionEntries.AsNoTracking()
            .Where(e => e.PayoutId.HasValue && payoutIds.Contains(e.PayoutId.Value))
            .Select(e => new { PayoutId = e.PayoutId!.Value, e.Id })
            .ToListAsync();

        var byPayout = lines.GroupBy(l => l.PayoutId).ToDictionary(g => g.Key, g => g.Select(l => l.Id).ToList());
        var nameMap = await StaffNamesAsync(payouts.Select(p => p.StaffUserId).Distinct().ToList());

        return ApiResponse<List<CommissionPayoutResponse>>.Ok(payouts.Select(p =>
        {
            var response = MapPayout(p);
            response.StaffName = nameMap.TryGetValue(p.StaffUserId, out var name) ? name : null;
            response.EntryIds = byPayout.TryGetValue(p.Id, out var ids) ? ids : new List<Guid>();
            return response;
        }).ToList());
    }

    private async Task<CommissionPayoutResponse> MapPayoutAsync(CommissionPayout payout, List<CommissionEntry> entries)
    {
        var response = MapPayout(payout);
        var nameMap = await StaffNamesAsync(new List<Guid> { payout.StaffUserId });
        response.StaffName = nameMap.TryGetValue(payout.StaffUserId, out var name) ? name : null;
        response.EntryIds = entries.Select(e => e.Id).ToList();
        return response;
    }

    private static CommissionPayoutResponse MapPayout(CommissionPayout payout) => new()
    {
        Id = payout.Id,
        BusinessId = payout.BusinessId,
        StaffUserId = payout.StaffUserId,
        Reference = payout.Reference,
        Notes = payout.Notes,
        TotalAmount = payout.TotalAmount,
        Currency = payout.Currency,
        EntryCount = payout.EntryCount,
        PeriodStart = payout.PeriodStartUtc,
        PeriodEnd = payout.PeriodEndUtc,
        PaidAt = payout.PaidAtUtc,
        CreatedAt = payout.CreatedAt
    };
}
