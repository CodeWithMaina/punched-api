namespace PunchedApi.Application.Analytics;

public sealed record AnalyticsBackfillOptions
{
    public Guid? BusinessId { get; init; }
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public Guid RunGroupId { get; init; } = Guid.NewGuid();
    public bool Resume { get; init; } = true;
    public int ProgressReportEveryDays { get; init; } = 14;
}

public sealed record AnalyticsBackfillResult
{
    public Guid RunGroupId { get; init; }
    public int BusinessesProcessed { get; init; }
    public int DaysProcessed { get; init; }
    public int DaysSkipped { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}
