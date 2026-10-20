using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PunchedApi.Application.Analytics;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

public interface IAnalyticsBackfillRunner
{
    Task<AnalyticsBackfillResult> RunAsync(AnalyticsBackfillOptions options, CancellationToken cancellationToken = default);
}

public sealed class AnalyticsBackfillRunner : IAnalyticsBackfillRunner
{
    private const int MaxAttempts = 4;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AnalyticsBackfillRunner> _logger;

    public AnalyticsBackfillRunner(IServiceScopeFactory scopeFactory, ILogger<AnalyticsBackfillRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<AnalyticsBackfillResult> RunAsync(AnalyticsBackfillOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.To < options.From)
        {
            throw new ArgumentOutOfRangeException(nameof(options.To), "'to' must be >= 'from'.");
        }

        var started = DateTime.UtcNow;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var businessIds = options.BusinessId.HasValue
            ? new List<Guid> { options.BusinessId.Value }
            : await db.Businesses.AsNoTracking().Select(b => b.Id).ToListAsync(cancellationToken);

        if (options.BusinessId.HasValue)
        {
            var exists = await db.Businesses.AsNoTracking().AnyAsync(b => b.Id == options.BusinessId.Value, cancellationToken);
            if (!exists)
            {
                throw new InvalidOperationException($"Business {options.BusinessId.Value} not found.");
            }
        }
        var totalDays = options.To.DayNumber - options.From.DayNumber + 1;
        var processedDays = 0;
        var skippedDays = 0;
        var businessesProcessed = 0;

        _logger.LogInformation("Starting analytics backfill. Businesses={N} Days={D} Resume={R}",
            businessIds.Count, totalDays, options.Resume);

        foreach (var businessId in businessIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await RunBusinessAsync(db, options, businessId, totalDays, cancellationToken);
            processedDays += outcome.Processed;
            skippedDays += outcome.Skipped;
            businessesProcessed++;
        }

        var elapsed = DateTime.UtcNow - started;
        _logger.LogInformation("Analytics backfill complete. Processed={P} Skipped={S} ElapsedMs={M}",
            processedDays, skippedDays, (long)elapsed.TotalMilliseconds);

        return new AnalyticsBackfillResult
        {
            RunGroupId = options.RunGroupId,
            BusinessesProcessed = businessesProcessed,
            DaysProcessed = processedDays,
            DaysSkipped = skippedDays,
            Elapsed = elapsed,
            Success = true,
        };
    }
    private async Task<(int Processed, int Skipped)> RunBusinessAsync(
        ApplicationDbContext db,
        AnalyticsBackfillOptions options,
        Guid businessId,
        int totalDays,
        CancellationToken cancellationToken)
    {
        var run = await db.AnalyticsBackfillRuns
            .FirstOrDefaultAsync(r => r.RunGroupId == options.RunGroupId && r.BusinessId == businessId, cancellationToken);

        if (run == null)
        {
            AnalyticsBackfillRun? prior = null;
            if (options.Resume)
            {
                prior = await db.AnalyticsBackfillRuns
                    .Where(r => r.BusinessId == businessId && r.FromDate == options.From && r.ToDate == options.To && r.Status == "Completed")
                    .OrderByDescending(r => r.CompletedAt)
                    .FirstOrDefaultAsync(cancellationToken);
            }
            run = new AnalyticsBackfillRun
            {
                Id = Guid.NewGuid(),
                RunGroupId = options.RunGroupId,
                BusinessId = businessId,
                FromDate = options.From,
                ToDate = options.To,
                Status = prior != null ? "Completed" : "Pending",
                LastCompletedDate = prior?.LastCompletedDate,
                TotalDays = totalDays,
                CompletedDays = prior?.CompletedDays ?? 0,
                CreatedAt = DateTime.UtcNow,
            };
            db.AnalyticsBackfillRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);
            if (prior != null)
            {
                _logger.LogInformation("Checkpoint reused for {B}: {C}/{T} days.", businessId, run.CompletedDays, totalDays);
                return (0, run.CompletedDays);
            }
        }
        if (run.Status == "Completed" && run.LastCompletedDate >= options.To)
        {
            return (0, totalDays);
        }
        run.Status = "Running";
        run.StartedAt ??= DateTime.UtcNow;
        run.Error = null;
        await db.SaveChangesAsync(cancellationToken);
        var startDay = options.From;
        if (options.Resume && run.LastCompletedDate.HasValue)
        {
            var resumeFrom = run.LastCompletedDate.Value.AddDays(1);
            if (resumeFrom > startDay) startDay = resumeFrom;
        }
        var processed = 0;
        var skipped = startDay.DayNumber - options.From.DayNumber;
        try
        {
            for (var day = startDay; day <= options.To; day = day.AddDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteWithRetryAsync(() => RecomputeDayAsync(businessId, day, cancellationToken), businessId, day, cancellationToken);
                run.LastCompletedDate = day;
                run.CompletedDays++;
                run.LastProgressAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                processed++;
                if (processed % Math.Max(1, options.ProgressReportEveryDays) == 0 || day == options.To)
                {
                    _logger.LogInformation("Backfill progress {B} {D} {C}/{T}", businessId, day.ToString("yyyy-MM-dd"), run.CompletedDays, totalDays);
                }
            }
            run.Status = "Completed";
            run.CompletedAt = DateTime.UtcNow;
            run.LastProgressAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return (processed, skipped);
        }
        catch (OperationCanceledException)
        {
            run.Status = "Cancelled";
            run.Error = "Cancelled by operator.";
            run.LastProgressAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            run.Status = "Failed";
            run.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            run.LastProgressAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task RecomputeDayAsync(Guid businessId, DateOnly day, CancellationToken cancellationToken)
    {
        await using var dayScope = _scopeFactory.CreateAsyncScope();
        var aggregator = dayScope.ServiceProvider.GetRequiredService<IAnalyticsAggregationService>();
        await aggregator.RecomputeBusinessDayAsync(businessId, day, cancellationToken);
        await aggregator.RecomputeStaffDayAsync(businessId, day, cancellationToken);
    }

    private async Task ExecuteWithRetryAsync(Func<Task> action, Guid businessId, DateOnly day, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await action();
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxAttempts)
            {
                _logger.LogWarning(ex, "Transient failure attempt {A}/{M} business {B} day {D}.",
                    attempt, MaxAttempts, businessId, day.ToString("yyyy-MM-dd"));
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
            }
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is DbUpdateException
            || ex is Npgsql.PostgresException { IsTransient: true }
            || ex is TimeoutException
            || (ex is InvalidOperationException && ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase));
}
