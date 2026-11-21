using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.BackgroundProcessing;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Recomputes today's analytics aggregates for every business, with the daily
/// backfill, segmentation, insights and win-back cron folded in.
///
/// <para>The near-realtime cadence is still five minutes, but it now runs through
/// <see cref="PeriodicWorkerLoop"/>: jittered, and in scale-to-zero-friendly mode
/// folded onto the shared tick so a quiet deployment is not woken every five minutes
/// by an analytics rebuild.</para>
/// </summary>
public sealed class AnalyticsWorker : BackgroundService
{
    private static readonly TimeSpan NearRealtimeInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DailyInterval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AnalyticsWorker> _logger;
    private readonly Microsoft.Extensions.Options.IOptions<PunchedApi.Application.Settings.StampingSettings> _stampingSettings;
    private readonly PollingProfile? _profile;
    private readonly BackgroundProcessingMetrics _metrics;

    public AnalyticsWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<AnalyticsWorker> logger,
        Microsoft.Extensions.Options.IOptions<PunchedApi.Application.Settings.StampingSettings> stampingSettings,
        BackgroundProcessingOptions? backgroundProcessing = null,
        BackgroundProcessingMetrics? metrics = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _stampingSettings = stampingSettings;
        _profile = backgroundProcessing?.For(BackgroundProcessingOptions.Names.Analytics);
        _metrics = metrics ?? BackgroundProcessingMetrics.Shared;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastDailyRun = DateTime.MinValue;
        var lastWinBackRun = DateTime.MinValue;

        return PeriodicWorkerLoop.RunAsync(
            BackgroundProcessingOptions.Names.Analytics,
            _profile ?? new PollingProfile(),
            NearRealtimeInterval,
            _logger,
            _metrics,
            async ct =>
            {
                var startedAt = DateTime.UtcNow;

                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var aggregator = scope.ServiceProvider.GetRequiredService<IAnalyticsAggregationService>();
                var segmentation = scope.ServiceProvider.GetRequiredService<ISegmentationService>();
                var insightService = scope.ServiceProvider.GetRequiredService<IInsightService>();
                var maintenance = scope.ServiceProvider.GetRequiredService<IStampingMaintenanceService>();

                // Only the ids: the aggregate rebuilds read their own rows, so
                // materialising whole Business entities here would fetch columns nobody
                // uses on every cycle.
                var businessIds = await db.Businesses.AsNoTracking()
                    .Select(b => b.Id)
                    .ToListAsync(ct);

                // Win-back nudge cron (Phase 4). Runs once per WinBackCronHours
                // (default 24h). Dedupes via NotificationLog inside
                // StampingMaintenanceService, so re-runs never notify twice.
                var winBackCronHours = Math.Max(1, _stampingSettings.Value.WinBackCronHours);
                if ((DateTime.UtcNow - lastWinBackRun) >= TimeSpan.FromHours(winBackCronHours))
                {
                    var winBackDays = Math.Max(1, _stampingSettings.Value.WinBackDays);
                    var nudges = await maintenance.SendWinBackNotificationsAsync(winBackDays, ct);
                    lastWinBackRun = DateTime.UtcNow;
                    _logger.LogInformation(
                        "Win-back cron complete. NudgesSent={Nudges}, WinBackDays={Days}, NextRunHours={Hours}",
                        nudges, winBackDays, winBackCronHours);
                }

                foreach (var businessId in businessIds)
                {
                    await aggregator.RecomputeTodayForBusinessAsync(businessId, ct);
                    await aggregator.RecomputeStaffDayAsync(businessId, DateOnly.FromDateTime(DateTime.UtcNow), ct);
                }

                if ((DateTime.UtcNow - lastDailyRun) >= DailyInterval)
                {
                    var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
                    foreach (var businessId in businessIds)
                    {
                        await aggregator.RecomputeBusinessDayAsync(businessId, yesterday, ct);
                        await aggregator.RecomputeStaffDayAsync(businessId, yesterday, ct);
                    }

                    await segmentation.RecomputeAllBusinessesAsync(ct);
                    await insightService.GenerateAllBusinessInsightsAsync(ct);
                    await insightService.GenerateAdminInsightsAsync(ct);

                    lastDailyRun = DateTime.UtcNow;
                }

                _logger.LogInformation(
                    "AnalyticsWorker cycle complete. Businesses={Businesses}, DurationMs={DurationMs}",
                    businessIds.Count,
                    (int)(DateTime.UtcNow - startedAt).TotalMilliseconds);

                // Business count is the unit of work this sweep reports; it is not a
                // row count, so the periodic loop treats it as "work found" regardless.
                return businessIds.Count;
            },
            stoppingToken);
    }
}
