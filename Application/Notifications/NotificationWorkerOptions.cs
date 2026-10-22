namespace PunchedApi.Application.Notifications;

public sealed class NotificationWorkerOptions
{
    public const string SectionName = "NotificationWorker";

    public int BatchSize { get; set; } = 50;
    public int PollIntervalSeconds { get; set; } = 20;
    public int StaleProcessingMinutes { get; set; } = 5;
    public int InboxRetentionDays { get; set; } = 90;
    public int LedgerRetentionDays { get; set; } = 90;
    public int FailedLedgerRetentionDays { get; set; } = 30;
    public int MaxDomainEventAttempts { get; set; } = 3;
    public int MaxJobAttempts { get; set; } = 3;

    // ── Phase 1.6 dispatch guards ─────────────────────────────
    // Rolling window used by the per-(business, user, channel) frequency cap.
    public int FrequencyWindowMinutes { get; set; } = 60;

    /// <summary>Max non-SECURITY admissions per (business, user, channel) window.</summary>
    public int FrequencyCapPerUserChannel { get; set; } = 10;

    // Quiet hours in the BUSINESS timezone (local hours; start == end disables).
    public int QuietHoursStartHour { get; set; } = 21;
    public int QuietHoursEndHour { get; set; } = 7;

    // Per-tenant daily admission budgets (UTC day, all instances, SECURITY included).
    public int SmsBudgetPerBusinessPerDay { get; set; } = 1000;
    public int PushBudgetPerBusinessPerDay { get; set; } = 5000;

    // ── Phase 3 customer.lapsed ─────────────────────────────────
    // Days of inactivity before a customer is considered lapsed.
    public int LapseInactivityDays { get; set; } = 30;

    /// <summary>
    /// Minimum days between two win-back nudges for the same
    /// (customer, business) pair. Also the width of one win-back cycle, so it
    /// must be ≥ 1. A deferred job is rescheduled to
    /// <c>lastSuccessfulSend + WinBackCooldownDays</c>.
    /// </summary>
    public int WinBackCooldownDays { get; set; } = 30;

    /// <summary>
    /// When no <c>customer.lapsed</c> automation rule fired, queue the
    /// registered default <c>customer.lapsed</c> notification directly. Set to
    /// false for tenants that want rule-only win-backs.
    /// </summary>
    public bool WinBackFallbackNudgeEnabled { get; set; } = true;
}
