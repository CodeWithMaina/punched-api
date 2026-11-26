using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Owner-facing staff reporting: the financial contribution and the operational
/// performance of each member of the team, over a stated period.
///
/// <para>Both reports are read-only projections over persisted appointments,
/// appointment service lines (the booked price snapshot), payments and reviews.
/// They never read the current catalogue price, so a price change cannot restate
/// yesterday's revenue.</para>
///
/// <para>The business is resolved from the caller's own membership — the client
/// never names it — so a report can only ever describe the caller's tenant.</para>
/// </summary>
public interface IStaffReportingService
{
    /// <summary>
    /// Per-staff revenue, collection and outstanding balances for a period.
    /// </summary>
    Task<ApiResponse<StaffFinancialReportResponse>> GetFinancialsAsync(
        Guid callerUserId, StaffReportQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Per-staff completion, cancellation, no-show, repeat-customer and rating
    /// metrics for a period, with a comparison against the preceding window.
    /// </summary>
    Task<ApiResponse<StaffPerformanceReportResponse>> GetPerformanceAsync(
        Guid callerUserId, StaffReportQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The workforce summary the Staff overview renders: head count, appointments
    /// handled, revenue attributed and collected, outstanding balance, undecided
    /// payment claims and the quality rates — all from the same definitions the
    /// Financials and Performance reports use.
    /// </summary>
    Task<ApiResponse<StaffOverviewSummaryResponse>> GetOverviewSummaryAsync(
        Guid callerUserId, StaffReportQuery query, CancellationToken cancellationToken = default);
}
