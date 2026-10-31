using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Staff commission balances and business-level commission reporting.
///
/// <para>A balance is the sum of entry amounts grouped by lifecycle status. A
/// reversal entry carries a NEGATIVE amount, so a clawback against an already
/// paid earning shows up in payableAmount and nets out of the next payout
/// without any special-case arithmetic.</para>
/// </summary>
public partial class CommissionService
{
    private static readonly CommissionEntryStatus[] EarnedStatuses =
    {
        CommissionEntryStatus.Approved,
        CommissionEntryStatus.Payable,
        CommissionEntryStatus.Paid
    };

    /// <inheritdoc />
    public async Task<ApiResponse<CommissionStaffBalanceResponse>> GetStaffBalanceAsync(
        Guid callerUserId, string role, Guid staffUserId)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<CommissionStaffBalanceResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "commissions.view"))
            return ApiResponse<CommissionStaffBalanceResponse>.Fail("FORBIDDEN", "You are not allowed to view commissions.");

        // A staff member may only read their own balance.
        if (IsRole(role, "Staff") && staffUserId != callerUserId)
            return ApiResponse<CommissionStaffBalanceResponse>.Fail("FORBIDDEN", "You can only view your own commission balance.");

        var isMember = await _context.BusinessMemberships.AsNoTracking().AnyAsync(m =>
            m.BusinessId == businessId.Value &&
            m.UserId == staffUserId &&
            m.Status == BusinessMembershipStatus.Active);
        if (!isMember)
            return ApiResponse<CommissionStaffBalanceResponse>.Fail("STAFF_NOT_FOUND", "Staff member not found in this business.");

        var balances = await BuildBalancesAsync(businessId.Value, new List<Guid> { staffUserId });
        return ApiResponse<CommissionStaffBalanceResponse>.Ok(balances[0]);
    }

    /// <inheritdoc />
    public async Task<ApiResponse<List<CommissionStaffBalanceResponse>>> GetBalancesAsync(
        Guid callerUserId, string role)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<List<CommissionStaffBalanceResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "commissions.view"))
            return ApiResponse<List<CommissionStaffBalanceResponse>>.Fail("FORBIDDEN", "You are not allowed to view commissions.");

        // Staff see exactly one row: their own.
        if (IsRole(role, "Staff"))
        {
            var own = await BuildBalancesAsync(businessId.Value, new List<Guid> { callerUserId });
            return ApiResponse<List<CommissionStaffBalanceResponse>>.Ok(own);
        }

        var staffIds = await _context.BusinessMemberships.AsNoTracking()
            .Where(m => m.BusinessId == businessId.Value &&
                        m.Status == BusinessMembershipStatus.Active &&
                        (m.Role == BusinessMembershipRole.Staff || m.Role == BusinessMembershipRole.Owner))
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync();

        var withEntries = await _context.CommissionEntries.AsNoTracking()
            .Where(e => e.BusinessId == businessId.Value)
            .Select(e => e.StaffUserId)
            .Distinct()
            .ToListAsync();

        var all = staffIds.Union(withEntries).Distinct().ToList();
        var balances = await BuildBalancesAsync(businessId.Value, all);

        return ApiResponse<List<CommissionStaffBalanceResponse>>.Ok(
            balances.Where(b => b.PendingAmount != 0m || b.ApprovedAmount != 0m || b.PayableAmount != 0m ||
                                b.PaidAmount != 0m || b.DisputedAmount != 0m || b.ReversedAmount != 0m)
                    .OrderBy(b => b.StaffName)
                    .ToList());
    }
}

/// <summary>Business-level commission reporting and balance aggregation.</summary>
public partial class CommissionService
{
    /// <inheritdoc />
    public async Task<ApiResponse<CommissionReportResponse>> GetReportAsync(
        Guid callerUserId, string role, DateTime? from, DateTime? to)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<CommissionReportResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "commissions.view"))
            return ApiResponse<CommissionReportResponse>.Fail("FORBIDDEN", "You are not allowed to view commission reports.");

        var query = _context.CommissionEntries.AsNoTracking()
            .Where(e => e.BusinessId == businessId.Value);
        if (from.HasValue) query = query.Where(e => e.CreatedAt >= from.Value);
        if (to.HasValue) query = query.Where(e => e.CreatedAt <= to.Value);

        var rows = await query
            .Select(e => new { e.StaffUserId, e.Status, e.Amount })
            .ToListAsync();

        var config = await _context.CommissionConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.BusinessId == businessId.Value);

        var nameMap = await StaffNamesAsync(rows.Select(r => r.StaffUserId).Distinct().ToList());

        var byStaff = rows.GroupBy(r => r.StaffUserId)
            .Select(g => BuildBalance(
                g.Key,
                g.Select(r => (r.Status, r.Amount)).ToList(),
                config?.Currency ?? "KES",
                nameMap.TryGetValue(g.Key, out var name) ? name : null))
            .OrderBy(b => b.StaffName)
            .ToList();

        return ApiResponse<CommissionReportResponse>.Ok(new CommissionReportResponse
        {
            BusinessId = businessId.Value,
            From = from,
            To = to,
            Currency = config?.Currency ?? "KES",
            TotalAccruedAmount = rows.Where(r => r.Status is CommissionEntryStatus.Pending
                                              or CommissionEntryStatus.Approved
                                              or CommissionEntryStatus.Payable
                                              or CommissionEntryStatus.Paid).Sum(r => r.Amount),
            TotalApprovedAmount = rows.Where(r => r.Status == CommissionEntryStatus.Approved).Sum(r => r.Amount),
            TotalPayableAmount = rows.Where(r => r.Status == CommissionEntryStatus.Payable).Sum(r => r.Amount),
            TotalPaidAmount = rows.Where(r => r.Status == CommissionEntryStatus.Paid).Sum(r => r.Amount),
            TotalReversedAmount = rows.Where(r => r.Status == CommissionEntryStatus.Reversed).Sum(r => r.Amount),
            TotalVoidedAmount = rows.Where(r => r.Status == CommissionEntryStatus.Void).Sum(r => r.Amount),
            DisputedAmount = rows.Where(r => r.Status == CommissionEntryStatus.Disputed).Sum(r => r.Amount),
            EntryCount = rows.Count,
            ByStaff = byStaff
        });
    }

    private async Task<List<CommissionStaffBalanceResponse>> BuildBalancesAsync(
        Guid businessId, List<Guid> staffIds)
    {
        if (staffIds.Count == 0) return new List<CommissionStaffBalanceResponse>();

        var rows = await _context.CommissionEntries.AsNoTracking()
            .Where(e => e.BusinessId == businessId && staffIds.Contains(e.StaffUserId))
            .Select(e => new { e.StaffUserId, e.Status, e.Amount })
            .ToListAsync();

        var config = await _context.CommissionConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.BusinessId == businessId);

        var nameMap = await StaffNamesAsync(staffIds);

        return staffIds
            .Select(id => BuildBalance(
                id,
                rows.Where(r => r.StaffUserId == id).Select(r => (r.Status, r.Amount)).ToList(),
                config?.Currency ?? "KES",
                nameMap.TryGetValue(id, out var name) ? name : null))
            .OrderBy(b => b.StaffName)
            .ToList();
    }

    private static CommissionStaffBalanceResponse BuildBalance(
        Guid staffUserId,
        List<(CommissionEntryStatus Status, decimal Amount)> rows,
        string currency = "KES",
        string? staffName = null)
    {
        decimal SumOf(params CommissionEntryStatus[] statuses) =>
            rows.Where(r => statuses.Contains(r.Status)).Sum(r => r.Amount);

        var payable = SumOf(CommissionEntryStatus.Payable);
        var approved = SumOf(CommissionEntryStatus.Approved);

        return new CommissionStaffBalanceResponse
        {
            StaffUserId = staffUserId,
            StaffName = staffName,
            Currency = currency,
            PendingAmount = SumOf(CommissionEntryStatus.Pending),
            ApprovedAmount = approved,
            PayableAmount = payable,
            PaidAmount = SumOf(CommissionEntryStatus.Paid),
            DisputedAmount = SumOf(CommissionEntryStatus.Disputed),
            ReversedAmount = SumOf(CommissionEntryStatus.Reversed),
            OutstandingAmount = payable + approved,
            LifetimeEarnedAmount = SumOf(EarnedStatuses)
        };
    }

    private async Task<Dictionary<Guid, string>> StaffNamesAsync(List<Guid> staffIds)
    {
        if (staffIds.Count == 0) return new Dictionary<Guid, string>();

        return await _context.Users.AsNoTracking()
            .Where(u => staffIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName })
            .ToDictionaryAsync(u => u.Id, u => u.FullName);
    }
}
