using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

/// <summary>
/// The window a staff report covers, plus the immediately preceding window of the
/// same length. Both are stated in UTC and both are half-open
/// (<c>[From, To + 1 day)</c>), so two adjacent windows can never double-count the
/// boundary.
/// </summary>
public class StaffReportPeriodResponse
{
    /// <summary>Inclusive start date in the business's timezone (midnight, local).</summary>
    [JsonPropertyName("from")] public DateTime From { get; set; }

    /// <summary>Inclusive end date in the business's timezone.</summary>
    [JsonPropertyName("to")] public DateTime To { get; set; }

    /// <summary>Inclusive UTC instant the query used (local start converted to UTC).</summary>
    [JsonPropertyName("fromUtc")] public DateTime FromUtc { get; set; }

    /// <summary>Exclusive UTC instant the query used (midnight after the local end).</summary>
    [JsonPropertyName("toUtcExclusive")] public DateTime ToUtcExclusive { get; set; }

    /// <summary>The IANA timezone the boundaries were computed in.</summary>
    [JsonPropertyName("timeZoneId")] public string TimeZoneId { get; set; } = "UTC";

    /// <summary>Human label the client renders verbatim ("Last 30 days", "This month", …).</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;

    /// <summary>The canonically parsed preset: today | 7d | 30d | this_month | last_month | custom.</summary>
    [JsonPropertyName("range")] public string Range { get; set; } = string.Empty;

    /// <summary>Start of the comparison window that precedes <see cref="From"/> (local).</summary>
    [JsonPropertyName("previousFrom")] public DateTime PreviousFrom { get; set; }

    /// <summary>End of the comparison window, the day before <see cref="From"/> (local).</summary>
    [JsonPropertyName("previousTo")] public DateTime PreviousTo { get; set; }
}

/// <summary>
/// One staff member's financial contribution for the selected period.
///
/// <para><b>Definitions (all derived from persisted rows, never from current
/// catalogue prices):</b></para>
/// <list type="bullet">
/// <item><c>serviceValue</c> — the sum of
/// <see cref="PunchedApi.Domain.Entities.AppointmentResource.Price"/> over the
/// service lines of the member's <b>completed</b> appointments. This is the price
/// snapshotted when the appointment was booked, so a later catalogue edit cannot
/// rewrite history.</item>
/// <item><c>amountCollected</c> — the sum of payments in <c>Success</c> or
/// <c>Verified</c> state whose appointment is attributed to the member and falls
/// in the period. This is money actually received, not money billed.</item>
/// <item><c>outstandingAmount</c> — max(0, serviceValue − collected on completed
/// appointments). Cancelled and no-show appointments are never billed.</item>
/// <item><c>averageServiceValue</c> — serviceValue ÷ completed appointments.</item>
/// <item><c>averageRateCard</c> — serviceValue ÷ completed service lines.</item>
/// </list>
/// </summary>
public class StaffFinancialRowResponse
{
    [JsonPropertyName("staffUserId")] public Guid StaffUserId { get; set; }
    [JsonPropertyName("fullName")] public string FullName { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("avatarUrl")] public string? AvatarUrl { get; set; }
    [JsonPropertyName("positionName")] public string? PositionName { get; set; }

    /// <summary><c>Active</c> | <c>Suspended</c> (a member who left is not on the roster).</summary>
    [JsonPropertyName("membershipStatus")] public string MembershipStatus { get; set; } = "Active";
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }

    [JsonPropertyName("completedAppointments")] public int CompletedAppointments { get; set; }
    [JsonPropertyName("cancelledAppointments")] public int CancelledAppointments { get; set; }
    [JsonPropertyName("noShowAppointments")] public int NoShowAppointments { get; set; }

    /// <summary>Completed service lines — the denominator of <see cref="AverageRateCard"/>.</summary>
    [JsonPropertyName("completedServiceLines")] public int CompletedServiceLines { get; set; }

    [JsonPropertyName("serviceValue")] public decimal ServiceValue { get; set; }

    /// <summary>Revenue attributed to this member — equal to <see cref="ServiceValue"/> for completed work.</summary>
    [JsonPropertyName("revenueAttributed")] public decimal RevenueAttributed { get; set; }

    [JsonPropertyName("amountCollected")] public decimal AmountCollected { get; set; }
    [JsonPropertyName("outstandingAmount")] public decimal OutstandingAmount { get; set; }
    [JsonPropertyName("refundedAmount")] public decimal RefundedAmount { get; set; }

    [JsonPropertyName("averageServiceValue")] public decimal AverageServiceValue { get; set; }
    [JsonPropertyName("averageRateCard")] public decimal AverageRateCard { get; set; }

    /// <summary>Collected ÷ serviceValue for completed work, expressed as a percentage (0–100).</summary>
    [JsonPropertyName("collectionRate")] public decimal CollectionRate { get; set; }

    /// <summary>This member's share of the business's staff-attributed revenue (0–100).</summary>
    [JsonPropertyName("revenueSharePercent")] public decimal RevenueSharePercent { get; set; }

    /// <summary>Manual M-PESA claims on this member's appointments still awaiting a decision.</summary>
    [JsonPropertyName("pendingVerificationCount")] public int PendingVerificationCount { get; set; }
}


/// <summary>Business-wide totals for the financial table, computed from the same rows.</summary>
public class StaffFinancialTotalsResponse
{
    [JsonPropertyName("staffCount")] public int StaffCount { get; set; }
    [JsonPropertyName("activeStaffCount")] public int ActiveStaffCount { get; set; }
    [JsonPropertyName("completedAppointments")] public int CompletedAppointments { get; set; }
    [JsonPropertyName("cancelledAppointments")] public int CancelledAppointments { get; set; }
    [JsonPropertyName("noShowAppointments")] public int NoShowAppointments { get; set; }
    [JsonPropertyName("completedServiceLines")] public int CompletedServiceLines { get; set; }
    [JsonPropertyName("totalServiceValue")] public decimal TotalServiceValue { get; set; }
    [JsonPropertyName("totalRevenue")] public decimal TotalRevenue { get; set; }
    [JsonPropertyName("totalCollected")] public decimal TotalCollected { get; set; }
    [JsonPropertyName("totalOutstanding")] public decimal TotalOutstanding { get; set; }
    [JsonPropertyName("totalRefunded")] public decimal TotalRefunded { get; set; }
    [JsonPropertyName("averageServiceValue")] public decimal AverageServiceValue { get; set; }
    [JsonPropertyName("averageRateCard")] public decimal AverageRateCard { get; set; }
    [JsonPropertyName("collectionRate")] public decimal CollectionRate { get; set; }
    [JsonPropertyName("pendingVerificationCount")] public int PendingVerificationCount { get; set; }
}

/// <summary>Financial report: one row per staff member plus the totals of those rows.</summary>
public class StaffFinancialReportResponse
{
    [JsonPropertyName("period")] public StaffReportPeriodResponse Period { get; set; } = new();

    /// <summary>Which rate card the rows use — always the appointment snapshot.</summary>
    [JsonPropertyName("rateCardSource")] public string RateCardSource { get; set; } = "appointment_snapshot";

    [JsonPropertyName("currency")] public string Currency { get; set; } = "KES";
    [JsonPropertyName("items")] public List<StaffFinancialRowResponse> Items { get; set; } = [];
    [JsonPropertyName("totals")] public StaffFinancialTotalsResponse Totals { get; set; } = new();
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("pageSize")] public int PageSize { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("totalPages")] public int TotalPages { get; set; }
}

/// <summary>
/// One staff member's operational performance for the selected period.
///
/// <para>Rates are shares of <b>decided</b> appointments (completed + cancelled +
/// no-show). Upcoming appointments are reported separately and never dragged into
/// a rate — an appointment that has not happened yet is not a failure.</para>
///
/// <para><see cref="HasSufficientData"/> is false when nothing completed in the
/// period: the client shows "not enough data yet" instead of ranking that member
/// as a poor performer.</para>
/// </summary>
public class StaffPerformanceRowResponse
{
    [JsonPropertyName("staffUserId")] public Guid StaffUserId { get; set; }
    [JsonPropertyName("fullName")] public string FullName { get; set; } = string.Empty;
    [JsonPropertyName("avatarUrl")] public string? AvatarUrl { get; set; }
    [JsonPropertyName("positionName")] public string? PositionName { get; set; }
    [JsonPropertyName("membershipStatus")] public string MembershipStatus { get; set; } = "Active";
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }

    [JsonPropertyName("completedAppointments")] public int CompletedAppointments { get; set; }
    [JsonPropertyName("cancelledAppointments")] public int CancelledAppointments { get; set; }
    [JsonPropertyName("noShowAppointments")] public int NoShowAppointments { get; set; }

    /// <summary>Booked/confirmed appointments still ahead of the member in this period.</summary>
    [JsonPropertyName("upcomingAppointments")] public int UpcomingAppointments { get; set; }

    [JsonPropertyName("completionRate")] public decimal CompletionRate { get; set; }
    [JsonPropertyName("cancellationRate")] public decimal CancellationRate { get; set; }
    [JsonPropertyName("noShowRate")] public decimal NoShowRate { get; set; }

    [JsonPropertyName("revenueAttributed")] public decimal RevenueAttributed { get; set; }
    [JsonPropertyName("averageServiceValue")] public decimal AverageServiceValue { get; set; }
    [JsonPropertyName("averageAppointmentMinutes")] public decimal AverageAppointmentMinutes { get; set; }

    /// <summary>Share of completed appointments whose customer completed more than one with this member in the period.</summary>
    [JsonPropertyName("repeatCustomerRate")] public decimal RepeatCustomerRate { get; set; }

    /// <summary>Mean published rating for the member in the period (null = no reviews).</summary>
    [JsonPropertyName("rating")] public decimal? Rating { get; set; }
    [JsonPropertyName("reviewCount")] public int ReviewCount { get; set; }

    [JsonPropertyName("previousRevenue")] public decimal PreviousRevenue { get; set; }
    [JsonPropertyName("previousCompletedAppointments")] public int PreviousCompletedAppointments { get; set; }

    /// <summary>Percentage change against the preceding window (null when there was no baseline).</summary>
    [JsonPropertyName("revenueChangePercent")] public decimal? RevenueChangePercent { get; set; }
    [JsonPropertyName("completedChangePercent")] public decimal? CompletedChangePercent { get; set; }

    /// <summary><c>up</c> | <c>down</c> | <c>flat</c> | <c>none</c> (no baseline).</summary>
    [JsonPropertyName("trend")] public string Trend { get; set; } = "none";

    /// <summary>False when the member completed nothing in the period.</summary>
    [JsonPropertyName("hasSufficientData")] public bool HasSufficientData { get; set; }

    /// <summary>1-based position by attributed revenue among members with data (null when no data).</summary>
    [JsonPropertyName("rank")] public int? Rank { get; set; }
}


/// <summary>Business-wide totals for the performance table.</summary>
public class StaffPerformanceTotalsResponse
{
    [JsonPropertyName("staffCount")] public int StaffCount { get; set; }
    [JsonPropertyName("activeStaffCount")] public int ActiveStaffCount { get; set; }
    [JsonPropertyName("completedAppointments")] public int CompletedAppointments { get; set; }
    [JsonPropertyName("cancelledAppointments")] public int CancelledAppointments { get; set; }
    [JsonPropertyName("noShowAppointments")] public int NoShowAppointments { get; set; }
    [JsonPropertyName("completionRate")] public decimal CompletionRate { get; set; }
    [JsonPropertyName("cancellationRate")] public decimal CancellationRate { get; set; }
    [JsonPropertyName("noShowRate")] public decimal NoShowRate { get; set; }
    [JsonPropertyName("revenueAttributed")] public decimal RevenueAttributed { get; set; }
    [JsonPropertyName("averageServiceValue")] public decimal AverageServiceValue { get; set; }
    [JsonPropertyName("averageRating")] public decimal? AverageRating { get; set; }
    [JsonPropertyName("reviewCount")] public int ReviewCount { get; set; }
    [JsonPropertyName("previousRevenue")] public decimal PreviousRevenue { get; set; }
    [JsonPropertyName("revenueChangePercent")] public decimal? RevenueChangePercent { get; set; }
}

/// <summary>Performance report: one row per staff member plus the totals of those rows.</summary>
public class StaffPerformanceReportResponse
{
    [JsonPropertyName("period")] public StaffReportPeriodResponse Period { get; set; } = new();
    [JsonPropertyName("currency")] public string Currency { get; set; } = "KES";
    [JsonPropertyName("items")] public List<StaffPerformanceRowResponse> Items { get; set; } = [];
    [JsonPropertyName("totals")] public StaffPerformanceTotalsResponse Totals { get; set; } = new();
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("pageSize")] public int PageSize { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("totalPages")] public int TotalPages { get; set; }
}

/// <summary>
/// Query for both staff reports. <c>from</c>/<c>to</c> win over <c>range</c>; the
/// report then calls the window "custom" so the client can show it honestly.
/// </summary>
public class StaffReportQuery
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string? Range { get; set; }
    public Guid? StaffId { get; set; }
    public string? Status { get; set; }
    public string? Search { get; set; }
    public string? SortBy { get; set; }
    public string? SortDirection { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

/// <summary>
/// The workforce summary the Staff overview needs on first paint: head count,
/// what the team did in the window, what it earned and how well it finished.
///
/// <para><b>It is derived, not recalculated.</b> The same roster load, the same
/// seven aggregate queries and the same row builders that produce the Financials
/// and Performance tabs produce these totals, so the overview can never disagree
/// with the tab it summarises.</para>
///
/// <para><see cref="HasFinancialData"/> and <see cref="HasPerformanceData"/> let
/// the console say "statistics appear after the first completed appointment"
/// instead of showing a wall of zeroes that reads as failure.</para>
/// </summary>
public class StaffOverviewSummaryResponse
{
    [JsonPropertyName("period")] public StaffReportPeriodResponse Period { get; set; } = new();
    [JsonPropertyName("currency")] public string Currency { get; set; } = "KES";

    // ── Workforce ────────────────────────────────────────────
    [JsonPropertyName("totalStaff")] public int TotalStaff { get; set; }
    [JsonPropertyName("activeStaff")] public int ActiveStaff { get; set; }
    [JsonPropertyName("inactiveStaff")] public int InactiveStaff { get; set; }
    [JsonPropertyName("pendingInvitations")] public int PendingInvitations { get; set; }

    // ── Work in the window ───────────────────────────────────
    /// <summary>Every appointment attributed to staff in the window, whatever its state.</summary>
    [JsonPropertyName("appointmentsHandled")] public int AppointmentsHandled { get; set; }

    /// <summary>Booked, confirmed or pending-payment — work that has not been decided yet.</summary>
    [JsonPropertyName("bookedAppointments")] public int BookedAppointments { get; set; }

    [JsonPropertyName("completedAppointments")] public int CompletedAppointments { get; set; }
    [JsonPropertyName("cancelledAppointments")] public int CancelledAppointments { get; set; }
    [JsonPropertyName("noShowAppointments")] public int NoShowAppointments { get; set; }

    // ── Money ────────────────────────────────────────────────
    [JsonPropertyName("revenueAttributed")] public decimal RevenueAttributed { get; set; }
    [JsonPropertyName("amountCollected")] public decimal AmountCollected { get; set; }
    [JsonPropertyName("outstandingAmount")] public decimal OutstandingAmount { get; set; }
    [JsonPropertyName("refundedAmount")] public decimal RefundedAmount { get; set; }
    [JsonPropertyName("averageServiceValue")] public decimal AverageServiceValue { get; set; }
    [JsonPropertyName("averageRateCard")] public decimal AverageRateCard { get; set; }
    [JsonPropertyName("collectionRate")] public decimal CollectionRate { get; set; }

    /// <summary>Manual M-PESA claims on staff appointments still awaiting a decision.</summary>
    [JsonPropertyName("pendingVerificationCount")] public int PendingVerificationCount { get; set; }

    // ── Quality ──────────────────────────────────────────────
    [JsonPropertyName("completionRate")] public decimal CompletionRate { get; set; }
    [JsonPropertyName("cancellationRate")] public decimal CancellationRate { get; set; }
    [JsonPropertyName("noShowRate")] public decimal NoShowRate { get; set; }
    [JsonPropertyName("averageRating")] public decimal? AverageRating { get; set; }
    [JsonPropertyName("reviewCount")] public int ReviewCount { get; set; }

    /// <summary>Change in attributed revenue against the preceding equal window (null = no baseline).</summary>
    [JsonPropertyName("revenueChangePercent")] public decimal? RevenueChangePercent { get; set; }

    /// <summary>True once the team has completed (or been paid for) something in the window.</summary>
    [JsonPropertyName("hasFinancialData")] public bool HasFinancialData { get; set; }

    /// <summary>True once any appointment reached a decided state in the window.</summary>
    [JsonPropertyName("hasPerformanceData")] public bool HasPerformanceData { get; set; }
}

