using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// The workforce summary behind the Staff overview.
///
/// <para><b>Why this lives here and not in a second service.</b> Every number the
/// overview needs — revenue, collection, completion, cancellation, no-shows,
/// ratings — is already defined by the Financials and Performance reports. A
/// separate implementation would be a second definition of the same money, and
/// the two would drift. This partial loads the identical window with the identical
/// queries and reuses <c>BuildFinancialRow</c> / <c>BuildPerformanceRow</c> and
/// their totals, so "overview revenue" and "Financials revenue" are the same
/// number by construction.</para>
///
/// <para>Cost is one roster load plus seven GROUP BY queries for the current
/// window and the same for the preceding window (needed only for the revenue
/// trend). Nothing is enumerated per staff member.</para>
/// </summary>
public partial class StaffReportingService
{
    /// <summary>The overview's headline figures for one window.</summary>
    public async Task<ApiResponse<StaffOverviewSummaryResponse>> GetOverviewSummaryAsync(
        Guid callerUserId, StaffReportQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var businessId = await ResolveBusinessIdAsync(callerUserId, cancellationToken);
            if (businessId == null)
                return ApiResponse<StaffOverviewSummaryResponse>.Fail(
                    "NOT_FOUND", "No business found for this account.");

            // Same business-local boundary as the two tabs, so the overview cannot
            // describe a different "today" from the report it links to.
            var timeZoneId = await _context.Businesses.AsNoTracking()
                .Where(b => b.Id == businessId.Value)
                .Select(b => b.TimeZoneId)
                .FirstOrDefaultAsync(cancellationToken);

            var timeZone = BookingTime.ResolveTimeZone(timeZoneId);
            var (window, errorCode, errorMessage) = ResolveWindow(query, timeZone);
            if (window == null)
                return ApiResponse<StaffOverviewSummaryResponse>.Fail(errorCode!, errorMessage!);

            var roster = await LoadRosterAsync(businessId.Value, cancellationToken);

            // Invitations are a workforce fact, not an activity one: an invitation is
            // counted while it is still open, whatever window is selected. An expired
            // pending row is not a pending invitation.
            var now = DateTime.UtcNow;
            var pendingInvitations = await _context.StaffInvitations.AsNoTracking()
                .CountAsync(i => i.BusinessId == businessId.Value
                    && i.Status == InvitationStatus.Pending
                    && i.ExpiresAt > now, cancellationToken);

            if (roster.Count == 0)
            {
                return ApiResponse<StaffOverviewSummaryResponse>.Ok(new StaffOverviewSummaryResponse
                {
                    Period = ToPeriodResponse(window),
                    Currency = Currency,
                    PendingInvitations = pendingInvitations
                });
            }

            var current = await LoadWindowAsync(
                businessId.Value, window.FromUtc, window.EndExclusiveUtc, cancellationToken);

            var previous = await LoadWindowAsync(
                businessId.Value, window.PreviousFromUtc, window.PreviousEndExclusiveUtc, cancellationToken);

            var financialTotals = BuildFinancialTotals(
                roster.Select(m => BuildFinancialRow(m, current)).ToList());

            var performanceTotals = BuildPerformanceTotals(
                roster.Select(m => BuildPerformanceRow(m, current, previous)).ToList());

            // Upcoming work is summed over the ROSTER, not over every fact row: a member
            // who left the business is not part of today's workforce, and counting their
            // bookings here would disagree with every other number on the card.
            var upcoming = roster.Sum(m =>
                current.Appointments.GetValueOrDefault(m.UserId, EmptyStatus).Upcoming);
            var other = roster.Sum(m =>
                current.Appointments.GetValueOrDefault(m.UserId, EmptyStatus).Other);

            var decided = financialTotals.CompletedAppointments
                + financialTotals.CancelledAppointments
                + financialTotals.NoShowAppointments;

            return ApiResponse<StaffOverviewSummaryResponse>.Ok(new StaffOverviewSummaryResponse
            {
                Period = ToPeriodResponse(window),
                Currency = Currency,

                TotalStaff = roster.Count,
                ActiveStaff = roster.Count(m => m.IsActive),
                InactiveStaff = roster.Count(m => !m.IsActive),
                PendingInvitations = pendingInvitations,

                // "Handled" is every appointment attributed to staff in the window,
                // including the ones still ahead. Completed/cancelled/no-show are the
                // states themselves, so nothing is counted under two labels by accident.
                AppointmentsHandled = decided + upcoming + other,
                BookedAppointments = upcoming,
                CompletedAppointments = financialTotals.CompletedAppointments,
                CancelledAppointments = financialTotals.CancelledAppointments,
                NoShowAppointments = financialTotals.NoShowAppointments,

                RevenueAttributed = financialTotals.TotalRevenue,
                AmountCollected = financialTotals.TotalCollected,
                OutstandingAmount = financialTotals.TotalOutstanding,
                RefundedAmount = financialTotals.TotalRefunded,
                AverageServiceValue = financialTotals.AverageServiceValue,
                AverageRateCard = financialTotals.AverageRateCard,
                CollectionRate = financialTotals.CollectionRate,
                PendingVerificationCount = financialTotals.PendingVerificationCount,

                CompletionRate = performanceTotals.CompletionRate,
                CancellationRate = performanceTotals.CancellationRate,
                NoShowRate = performanceTotals.NoShowRate,
                AverageRating = performanceTotals.AverageRating,
                ReviewCount = performanceTotals.ReviewCount,
                RevenueChangePercent = performanceTotals.RevenueChangePercent,

                HasFinancialData = financialTotals.CompletedAppointments > 0
                    || financialTotals.TotalCollected > 0,
                HasPerformanceData = decided > 0
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build the staff overview summary for {CallerUserId}.", callerUserId);
            return ApiResponse<StaffOverviewSummaryResponse>.Fail(
                "REPORT_FAILED", "The staff summary could not be built. Please try again.");
        }
    }
}
