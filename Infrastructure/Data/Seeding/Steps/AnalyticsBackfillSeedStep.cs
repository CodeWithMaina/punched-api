using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Infrastructure.Data.Seeding.Steps;

public sealed class AnalyticsBackfillSeedStep : ISeedStep
{
    public string Name => "AnalyticsBackfill";

    public async Task ExecuteAsync(SeedExecutionContext context, CancellationToken cancellationToken)
    {
        // Explicit opt-in only: DatabaseSeeder skips this step entirely unless
        // Seed:IncludeAnalyticsBackfill=true. The supported path for historical
        // analytics is the dedicated CLI (`db backfill --from --to`), which uses
        // the durable AnalyticsBackfillRunner (checkpoints + resume + idempotent
        // replaceable aggregates). This seed step only handles the small
        // seeded-data range so a synthetic seed stays self-consistent.
        if (context.Db.Stamps.Any())
        {
            var minStamp = await context.Db.Stamps.MinAsync(s => s.StampedAt, cancellationToken);
            var maxStamp = await context.Db.Stamps.MaxAsync(s => s.StampedAt, cancellationToken);
            var minDate = DateOnly.FromDateTime(minStamp);
            var maxDate = DateOnly.FromDateTime(maxStamp);

            var aggregator = context.ServiceProvider.GetService(typeof(IAnalyticsAggregationService)) as IAnalyticsAggregationService;
            if (aggregator != null)
            {
                await aggregator.BackfillAllBusinessesAsync(minDate, maxDate, cancellationToken);
            }
        }

        var segmentation = context.ServiceProvider.GetService(typeof(ISegmentationService)) as ISegmentationService;
        var loyalty = context.ServiceProvider.GetService(typeof(ILoyaltyService)) as ILoyaltyService;

        if (segmentation != null)
        {
            await segmentation.BackfillAllBusinessesAsync(cancellationToken);
        }

        if (loyalty != null)
        {
            await loyalty.BackfillProgramHistoryAsync(cancellationToken);
        }
    }
}