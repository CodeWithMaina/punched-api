using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

/// <summary>The financial report: per-staff revenue, collection and outstanding balances.</summary>
public partial class StaffReportingService
{
    /// <inheritdoc />
    public async Task<ApiResponse<StaffFinancialReportResponse>> GetFinancialsAsync(
        Guid callerUserId, StaffReportQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var businessId = await ResolveBusinessIdAsync(callerUserId, cancellationToken);
            if (businessId == null)
                return ApiResponse<StaffFinancialReportResponse>.Fail(
                    "NOT_FOUND", "No business found for this account.");

            // Boundaries are computed in the business's own timezone, then converted to
            // the UTC instants stored in PostgreSQL. A report for "Today" means the
            // shop's today, not UTC's.
            var timeZoneId = await _context.Businesses.AsNoTracking()
                .Where(b => b.Id == businessId.Value)
                .Select(b => b.TimeZoneId)
                .FirstOrDefaultAsync(cancellationToken);

            var timeZone = BookingTime.ResolveTimeZone(timeZoneId);
            var (window, errorCode, errorMessage) = ResolveWindow(query, timeZone);
            if (window == null)
                return ApiResponse<StaffFinancialReportResponse>.Fail(errorCode!, errorMessage!);

            var roster = await LoadRosterAsync(businessId.Value, cancellationToken);
            var facts = roster.Count == 0
                ? EmptyWindowFacts()
                : await LoadWindowAsync(businessId.Value, window.FromUtc, window.EndExclusiveUtc, cancellationToken);

            // Every roster member is a row, even with no activity: "zero completed" is
            // information the owner needs, not a reason to hide the person.
            var allRows = roster.Select(m => BuildFinancialRow(m, facts)).ToList();

            // Share is a business-wide denominator, so it stays honest when the table is
            // filtered down to one member.
            var periodRevenue = allRows.Sum(r => r.RevenueAttributed);
            foreach (var row in allRows)
                row.RevenueSharePercent = Percent(row.RevenueAttributed, periodRevenue);

            IEnumerable<StaffFinancialRowResponse> filtered = allRows;

            if (query.StaffId is Guid staffId)
            {
                var match = allRows.FirstOrDefault(r => r.StaffUserId == staffId);
                if (match == null)
                    return ApiResponse<StaffFinancialReportResponse>.Fail(
                        "NOT_FOUND", "Staff member not found in your business.");
                filtered = [match];
            }

            var status = NormalizeStatus(query.Status);
            filtered = filtered.Where(r => status switch
            {
                "active" => r.IsActive,
                "inactive" => !r.IsActive,
                _ => true
            });

            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var needle = query.Search.Trim();
                filtered = filtered.Where(r =>
                    r.FullName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || r.Email.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || (r.PositionName?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            var rows = filtered.ToList();
            var totals = BuildFinancialTotals(rows);

            var ordered = SortFinancials(rows, query.SortBy, query.SortDirection);
            var (page, pageSize) = Paging(query);
            var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            return ApiResponse<StaffFinancialReportResponse>.Ok(new StaffFinancialReportResponse
            {
                Period = ToPeriodResponse(window),
                Currency = Currency,
                Items = items,
                Totals = totals,
                Page = page,
                PageSize = pageSize,
                Total = rows.Count,
                TotalPages = (int)Math.Ceiling(rows.Count / (double)pageSize)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build the staff financial report for {CallerUserId}.", callerUserId);
            return ApiResponse<StaffFinancialReportResponse>.Fail(
                "REPORT_FAILED", "The financial report could not be built. Please try again.");
        }
    }

    private static WindowFacts EmptyWindowFacts() => new(
        new Dictionary<Guid, StatusFacts>(),
        new Dictionary<Guid, ServiceFacts>(),
        new Dictionary<Guid, decimal>(),
        new Dictionary<Guid, decimal>(),
        new Dictionary<Guid, int>(),
        new Dictionary<Guid, RatingFacts>(),
        new Dictionary<Guid, int>());

    private static StaffFinancialRowResponse BuildFinancialRow(RosterMember member, WindowFacts facts)
    {
        var status = facts.Appointments.GetValueOrDefault(member.UserId, EmptyStatus);
        var service = facts.Services.GetValueOrDefault(member.UserId) ?? new ServiceFacts(0, 0, 0);
        var collected = facts.Collected.GetValueOrDefault(member.UserId);
        var refunded = facts.Refunded.GetValueOrDefault(member.UserId);

        // Only completed work is billable; a cancelled or no-show visit never creates debt.
        var outstanding = Math.Max(0m, service.Value - collected);

        return new StaffFinancialRowResponse
        {
            StaffUserId = member.UserId,
            FullName = member.FullName,
            Email = member.Email,
            AvatarUrl = member.AvatarUrl,
            PositionName = member.PositionName,
            MembershipStatus = member.MembershipStatus,
            IsActive = member.IsActive,
            CompletedAppointments = status.Completed,
            CancelledAppointments = status.Cancelled,
            NoShowAppointments = status.NoShow,
            CompletedServiceLines = service.Lines,
            ServiceValue = Money(service.Value),
            RevenueAttributed = Money(service.Value),
            AmountCollected = Money(collected),
            OutstandingAmount = Money(outstanding),
            RefundedAmount = Money(refunded),
            AverageServiceValue = status.Completed > 0 ? Money(service.Value / status.Completed) : 0m,
            AverageRateCard = service.Lines > 0 ? Money(service.Value / service.Lines) : 0m,
            CollectionRate = Percent(collected, service.Value),
            PendingVerificationCount = facts.PendingVerification.GetValueOrDefault(member.UserId)
        };
    }

    private static StaffFinancialTotalsResponse BuildFinancialTotals(List<StaffFinancialRowResponse> rows)
    {
        var completed = rows.Sum(r => r.CompletedAppointments);
        var lines = rows.Sum(r => r.CompletedServiceLines);
        var serviceValue = rows.Sum(r => r.ServiceValue);
        var collected = rows.Sum(r => r.AmountCollected);

        return new StaffFinancialTotalsResponse
        {
            StaffCount = rows.Count,
            ActiveStaffCount = rows.Count(r => r.IsActive),
            CompletedAppointments = completed,
            CancelledAppointments = rows.Sum(r => r.CancelledAppointments),
            NoShowAppointments = rows.Sum(r => r.NoShowAppointments),
            CompletedServiceLines = lines,
            TotalServiceValue = Money(serviceValue),
            TotalRevenue = Money(serviceValue),
            TotalCollected = Money(collected),
            TotalOutstanding = Money(rows.Sum(r => r.OutstandingAmount)),
            TotalRefunded = Money(rows.Sum(r => r.RefundedAmount)),
            AverageServiceValue = completed > 0 ? Money(serviceValue / completed) : 0m,
            AverageRateCard = lines > 0 ? Money(serviceValue / lines) : 0m,
            CollectionRate = Percent(collected, serviceValue),
            PendingVerificationCount = rows.Sum(r => r.PendingVerificationCount)
        };
    }

    /// <summary>
    /// Deterministic ordering: every option ends with the name and then the id, so a
    /// page boundary can never repeat one member and skip another.
    /// </summary>
    private static List<StaffFinancialRowResponse> SortFinancials(
        List<StaffFinancialRowResponse> rows, string? sortBy, string? sortDirection)
    {
        var descending = !string.Equals(sortDirection?.Trim(), "asc", StringComparison.OrdinalIgnoreCase);

        IOrderedEnumerable<StaffFinancialRowResponse> ordered = (sortBy?.Trim().ToLowerInvariant()) switch
        {
            "name" => descending
                ? rows.OrderByDescending(r => r.FullName, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase),
            "revenue_asc" => rows.OrderBy(r => r.RevenueAttributed),
            "average" => descending
                ? rows.OrderByDescending(r => r.AverageServiceValue)
                : rows.OrderBy(r => r.AverageServiceValue),
            "appointments" => descending
                ? rows.OrderByDescending(r => r.CompletedAppointments)
                : rows.OrderBy(r => r.CompletedAppointments),
            "collection" => descending
                ? rows.OrderByDescending(r => r.CollectionRate)
                : rows.OrderBy(r => r.CollectionRate),
            "outstanding" => descending
                ? rows.OrderByDescending(r => r.OutstandingAmount)
                : rows.OrderBy(r => r.OutstandingAmount),
            _ => descending
                ? rows.OrderByDescending(r => r.RevenueAttributed)
                : rows.OrderBy(r => r.RevenueAttributed)
        };

        return ordered
            .ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.StaffUserId)
            .ToList();
    }
}
