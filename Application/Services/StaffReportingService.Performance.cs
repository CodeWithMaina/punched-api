using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Services;

/// <summary>
/// The performance report: completion quality, repeat custom, ratings and the
/// change against the preceding window.
/// </summary>
public partial class StaffReportingService
{
    /// <inheritdoc />
    public async Task<ApiResponse<StaffPerformanceReportResponse>> GetPerformanceAsync(
        Guid callerUserId, StaffReportQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var businessId = await ResolveBusinessIdAsync(callerUserId, cancellationToken);
            if (businessId == null)
                return ApiResponse<StaffPerformanceReportResponse>.Fail(
                    "NOT_FOUND", "No business found for this account.");

            // Same business-local boundary rule as the financial report, so the two
            // tabs can never describe different periods.
            var timeZoneId = await _context.Businesses.AsNoTracking()
                .Where(b => b.Id == businessId.Value)
                .Select(b => b.TimeZoneId)
                .FirstOrDefaultAsync(cancellationToken);

            var timeZone = BookingTime.ResolveTimeZone(timeZoneId);
            var (window, errorCode, errorMessage) = ResolveWindow(query, timeZone);
            if (window == null)
                return ApiResponse<StaffPerformanceReportResponse>.Fail(errorCode!, errorMessage!);

            var roster = await LoadRosterAsync(businessId.Value, cancellationToken);
            if (roster.Count == 0)
            {
                var (emptyPage, emptyPageSize) = Paging(query);
                return ApiResponse<StaffPerformanceReportResponse>.Ok(new StaffPerformanceReportResponse
                {
                    Period = ToPeriodResponse(window),
                    Currency = Currency,
                    Page = emptyPage,
                    PageSize = emptyPageSize
                });
            }

            var current = await LoadWindowAsync(
                businessId.Value, window.FromUtc, window.EndExclusiveUtc, cancellationToken);

            // The comparison window is only needed when there is someone to compare.
            var previous = await LoadWindowAsync(
                businessId.Value, window.PreviousFromUtc, window.PreviousEndExclusiveUtc, cancellationToken);

            var allRows = roster.Select(m => BuildPerformanceRow(m, current, previous)).ToList();

            // Rank is business-wide and only among members with data, so a new starter
            // with nothing completed is never presented as the worst performer.
            var ranked = allRows
                .Where(r => r.HasSufficientData)
                .OrderByDescending(r => r.RevenueAttributed)
                .ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
                .Select((row, index) => (row.StaffUserId, Rank: index + 1))
                .ToDictionary(x => x.StaffUserId, x => x.Rank);

            foreach (var row in allRows)
                row.Rank = ranked.TryGetValue(row.StaffUserId, out var rank) ? rank : null;

            IEnumerable<StaffPerformanceRowResponse> filtered = allRows;

            if (query.StaffId is Guid staffId)
            {
                var match = allRows.FirstOrDefault(r => r.StaffUserId == staffId);
                if (match == null)
                    return ApiResponse<StaffPerformanceReportResponse>.Fail(
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
                    || (r.PositionName?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            var rows = filtered.ToList();
            var totals = BuildPerformanceTotals(rows);

            var ordered = SortPerformance(rows, query.SortBy, query.SortDirection);
            var (page, pageSize) = Paging(query);
            var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            return ApiResponse<StaffPerformanceReportResponse>.Ok(new StaffPerformanceReportResponse
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
            _logger.LogError(ex, "Failed to build the staff performance report for {CallerUserId}.", callerUserId);
            return ApiResponse<StaffPerformanceReportResponse>.Fail(
                "REPORT_FAILED", "The performance report could not be built. Please try again.");
        }
    }

    private static StaffPerformanceRowResponse BuildPerformanceRow(
        RosterMember member, WindowFacts current, WindowFacts previous)
    {
        var status = current.Appointments.GetValueOrDefault(member.UserId, EmptyStatus);
        var service = current.Services.GetValueOrDefault(member.UserId) ?? new ServiceFacts(0, 0, 0);
        var prior = previous.Appointments.GetValueOrDefault(member.UserId, EmptyStatus);
        var priorService = previous.Services.GetValueOrDefault(member.UserId) ?? new ServiceFacts(0, 0, 0);
        var rating = current.Ratings.GetValueOrDefault(member.UserId);

        // Rates describe decided appointments only. An appointment that has not
        // happened yet is not a success or a failure, so it is reported separately.
        var decided = status.Completed + status.Cancelled + status.NoShow;
        var revenue = service.Value;
        var previousRevenue = priorService.Value;

        var revenueChange = ChangePercent(revenue, previousRevenue);
        var completedChange = ChangePercent(status.Completed, prior.Completed);

        return new StaffPerformanceRowResponse
        {
            StaffUserId = member.UserId,
            FullName = member.FullName,
            AvatarUrl = member.AvatarUrl,
            PositionName = member.PositionName,
            MembershipStatus = member.MembershipStatus,
            IsActive = member.IsActive,
            CompletedAppointments = status.Completed,
            CancelledAppointments = status.Cancelled,
            NoShowAppointments = status.NoShow,
            UpcomingAppointments = status.Upcoming,
            CompletionRate = Percent(status.Completed, decided),
            CancellationRate = Percent(status.Cancelled, decided),
            NoShowRate = Percent(status.NoShow, decided),
            RevenueAttributed = Money(revenue),
            AverageServiceValue = status.Completed > 0 ? Money(service.Value / status.Completed) : 0m,
            AverageAppointmentMinutes = status.Completed > 0
                ? Math.Round(service.Minutes / (decimal)status.Completed, 0)
                : 0m,
            RepeatCustomerRate = Percent(
                current.RepeatAppointments.GetValueOrDefault(member.UserId), status.Completed),
            Rating = rating is { Count: > 0 } ? Math.Round(rating.Sum / rating.Count, 1) : null,
            ReviewCount = rating?.Count ?? 0,
            PreviousRevenue = Money(previousRevenue),
            PreviousCompletedAppointments = prior.Completed,
            RevenueChangePercent = revenueChange,
            CompletedChangePercent = completedChange,
            Trend = TrendOf(revenueChange),
            HasSufficientData = status.Completed > 0
        };
    }

    private static StaffPerformanceTotalsResponse BuildPerformanceTotals(List<StaffPerformanceRowResponse> rows)
    {
        var completed = rows.Sum(r => r.CompletedAppointments);
        var cancelled = rows.Sum(r => r.CancelledAppointments);
        var noShow = rows.Sum(r => r.NoShowAppointments);
        var decided = completed + cancelled + noShow;
        var revenue = rows.Sum(r => r.RevenueAttributed);
        var previousRevenue = rows.Sum(r => r.PreviousRevenue);
        var reviewCount = rows.Sum(r => r.ReviewCount);

        // Weighted by review count, so one five-star review cannot outrank a member
        // with forty reviews — and a member with no reviews is simply absent.
        var ratedRows = rows.Where(r => r.Rating.HasValue && r.ReviewCount > 0).ToList();
        decimal? averageRating = reviewCount > 0
            ? Math.Round(ratedRows.Sum(r => r.Rating!.Value * r.ReviewCount) / reviewCount, 1)
            : null;

        return new StaffPerformanceTotalsResponse
        {
            StaffCount = rows.Count,
            ActiveStaffCount = rows.Count(r => r.IsActive),
            CompletedAppointments = completed,
            CancelledAppointments = cancelled,
            NoShowAppointments = noShow,
            CompletionRate = Percent(completed, decided),
            CancellationRate = Percent(cancelled, decided),
            NoShowRate = Percent(noShow, decided),
            RevenueAttributed = Money(revenue),
            AverageServiceValue = completed > 0 ? Money(revenue / completed) : 0m,
            AverageRating = averageRating,
            ReviewCount = reviewCount,
            PreviousRevenue = Money(previousRevenue),
            RevenueChangePercent = ChangePercent(revenue, previousRevenue)
        };
    }

    /// <summary>
    /// Deterministic ordering. Members without data sort last for every
    /// performance-oriented option, because "no completed appointments" is not a
    /// performance of zero — it is the absence of evidence.
    /// </summary>
    private static List<StaffPerformanceRowResponse> SortPerformance(
        List<StaffPerformanceRowResponse> rows, string? sortBy, string? sortDirection)
    {
        var descending = !string.Equals(sortDirection?.Trim(), "asc", StringComparison.OrdinalIgnoreCase);

        IOrderedEnumerable<StaffPerformanceRowResponse> ordered = (sortBy?.Trim().ToLowerInvariant()) switch
        {
            "name" => descending
                ? rows.OrderByDescending(r => r.FullName, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase),
            "completed" => descending
                ? rows.OrderByDescending(r => r.HasSufficientData).ThenByDescending(r => r.CompletedAppointments)
                : rows.OrderByDescending(r => r.HasSufficientData).ThenBy(r => r.CompletedAppointments),
            "completion" => descending
                ? rows.OrderByDescending(r => r.HasSufficientData).ThenByDescending(r => r.CompletionRate)
                : rows.OrderByDescending(r => r.HasSufficientData).ThenBy(r => r.CompletionRate),
            "rating" => descending
                ? rows.OrderByDescending(r => r.Rating.HasValue).ThenByDescending(r => r.Rating ?? 0m)
                : rows.OrderByDescending(r => r.Rating.HasValue).ThenBy(r => r.Rating ?? 0m),
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
