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
}
