using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

public sealed class AnalyticsAggregationService : IAnalyticsAggregationService
{
    /// <summary>Max days aggregated in one range query before chunking.</summary>
    internal const int MaxRangeDays = 62;

    private readonly ApplicationDbContext _context;
    private readonly ILogger<AnalyticsAggregationService> _logger;

    public AnalyticsAggregationService(ApplicationDbContext context, ILogger<AnalyticsAggregationService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public Task RecomputeTodayForBusinessAsync(Guid businessId, CancellationToken cancellationToken = default)
    {
        return RecomputeBusinessDayAsync(businessId, DateOnly.FromDateTime(DateTime.UtcNow), cancellationToken);
    }

    public async Task BackfillBusinessAsync(Guid businessId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        NormalizeRange(from, to);
        foreach (var (chunkFrom, chunkTo) in ChunkRange(from, to))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RecomputeBusinessRangeAsync(businessId, chunkFrom, chunkTo, cancellationToken);
            await RecomputeStaffRangeAsync(businessId, chunkFrom, chunkTo, cancellationToken);
        }
    }

    public async Task BackfillAllBusinessesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var businessIds = await _context.Businesses
            .AsNoTracking()
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);

        foreach (var businessId in businessIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await BackfillBusinessAsync(businessId, from, to, cancellationToken);
        }
    }

    public async Task RecomputeBusinessDayAsync(Guid businessId, DateOnly day, CancellationToken cancellationToken = default)
    {
        await RecomputeBusinessRangeAsync(businessId, day, day, cancellationToken);
    }

    public async Task RecomputeBusinessRangeAsync(Guid businessId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        NormalizeRange(from, to);
        var (rangeStartUtc, rangeEndUtc) = GetUtcRange(from, to);

        // Two grouped SQL round-trips for the whole range (instead of N queries
        // per day): stamp activity grouped by UTC day, and redemption activity
        // grouped by UTC day. Business-wide snapshot metrics (liability,
        // reward-ready) are computed once and stamped on every day row so a
        // rerun deterministically replaces the same values (idempotent).
        var stampStats = await _context.Stamps
            .AsNoTracking()
            .Where(s => s.Card.BusinessId == businessId && s.StampedAt >= rangeStartUtc && s.StampedAt < rangeEndUtc)
            .GroupBy(s => s.StampedAt.Date)
            .Select(g => new
            {
                Day = g.Key,
                Stamps = g.Count(),
                DistinctCustomers = g.Select(x => x.Card.CustomerId).Distinct().Count(),
            })
            .ToDictionaryAsync(x => DateOnly.FromDateTime(x.Day), cancellationToken);

        var enrollmentStats = await _context.LoyaltyCards
            .AsNoTracking()
            .Where(c => c.BusinessId == businessId && c.EnrolledAt >= rangeStartUtc && c.EnrolledAt < rangeEndUtc)
            .GroupBy(c => c.EnrolledAt.Date)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => DateOnly.FromDateTime(x.Day), x => x.Count, cancellationToken);

        var redemptionStats = await _context.Redemptions
            .AsNoTracking()
            .Where(r => r.BusinessId == businessId && r.RedeemedAt >= rangeStartUtc && r.RedeemedAt < rangeEndUtc)
            .GroupBy(r => r.RedeemedAt.Date)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => DateOnly.FromDateTime(x.Day), x => x.Count, cancellationToken);

        var payoutStats = await _context.Redemptions
            .AsNoTracking()
            .Where(r => r.BusinessId == businessId && r.PaidAt != null && r.PaidAt >= rangeStartUtc && r.PaidAt < rangeEndUtc)
            .GroupBy(r => r.PaidAt!.Value.Date)
            .Select(g => new { Day = g.Key, Total = g.Sum(x => (decimal?)x.RewardValue) ?? 0m })
            .ToDictionaryAsync(x => DateOnly.FromDateTime(x.Day), x => x.Total, cancellationToken);

        var accruedLiabilityKes = await _context.LoyaltyCards
            .AsNoTracking()
            .Where(c => c.BusinessId == businessId)
            .Select(c => c.Program.StampsRequired > 0
                ? (decimal?)c.TotalStamps * (c.Program.RewardValue / c.Program.StampsRequired)
                : 0m)
            .SumAsync(cancellationToken) ?? 0m;

        var rewardReadyCustomers = await _context.LoyaltyCards
            .AsNoTracking()
            .Where(c => c.BusinessId == businessId && c.TotalStamps >= c.Program.StampsRequired)
            .CountAsync(cancellationToken);

        var existing = await _context.BusinessDailyAnalytics
            .Where(x => x.BusinessId == businessId && x.Date >= from && x.Date <= to)
            .ToDictionaryAsync(x => x.Date, cancellationToken);

        var now = DateTime.UtcNow;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            stampStats.TryGetValue(day, out var stamps);
            var row = existing.TryGetValue(day, out var found) ? found : null;
            if (row == null)
            {
                row = new BusinessDailyAnalytics { BusinessId = businessId, Date = day };
                await _context.BusinessDailyAnalytics.AddAsync(row, cancellationToken);
            }

            // Replaceable aggregate: recompute from source rows and overwrite —
            // never increment. Reruns converge to the same values.
            row.Stamps = stamps?.Stamps ?? 0;
            row.DistinctCustomers = stamps?.DistinctCustomers ?? 0;
            row.NewEnrollments = enrollmentStats.GetValueOrDefault(day);
            row.Redemptions = redemptionStats.GetValueOrDefault(day);
            row.PayoutKes = payoutStats.GetValueOrDefault(day);
            row.AccruedLiabilityKes = accruedLiabilityKes;
            row.RewardReadyCustomers = rewardReadyCustomers;
            row.UpdatedAt = now;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task RecomputeStaffDayAsync(Guid businessId, DateOnly day, CancellationToken cancellationToken = default)
    {
        await RecomputeStaffRangeAsync(businessId, day, day, cancellationToken);
    }

    public async Task RecomputeStaffRangeAsync(Guid businessId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        NormalizeRange(from, to);
        await using var transaction = await BeginTransactionIfSupportedAsync(cancellationToken);
        try
        {
            await UpsertStaffRangeAsync(businessId, from, to, cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (transaction != null)
            {
                try { await transaction.RollbackAsync(cancellationToken); } catch { }
            }
            throw;
        }
    }

    private async Task UpsertStaffRangeAsync(Guid businessId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var (startUtc, endUtc) = GetUtcRange(from, to);

        // Range-batched aggregation: one grouped query for stamp volume per
        // (staff, day), one for first-seen customers, one for redemptions —
        // instead of ~4 queries per day × days. Scoped delete+insert stays
        // inside the caller's transaction so concurrent reruns converge via
        // the (StaffUserId, BusinessId, Date) primary key.
        var grouped = await _context.Stamps
            .AsNoTracking()
            .Where(s => s.Card.BusinessId == businessId && s.AwardedByUserId != null && s.StampedAt >= startUtc && s.StampedAt < endUtc)
            .GroupBy(s => new { StaffUserId = s.AwardedByUserId!.Value, Day = s.StampedAt.Date })
            .Select(g => new
            {
                g.Key.StaffUserId,
                Day = g.Key.Day,
                Stamps = g.Count(),
                DistinctCustomers = g.Select(x => x.Card.CustomerId).Distinct().Count()
            })
            .ToListAsync(cancellationToken);

        var newCustomersByStaff = await _context.Stamps
            .AsNoTracking()
            .Where(s => s.Card.BusinessId == businessId && s.AwardedByUserId != null)
            .GroupBy(s => new { StaffUserId = s.AwardedByUserId!.Value, s.Card.CustomerId })
            .Select(g => new
            {
                g.Key.StaffUserId,
                FirstStampedAt = g.Min(s => s.StampedAt)
            })
            .Where(x => x.FirstStampedAt >= startUtc && x.FirstStampedAt < endUtc)
            .GroupBy(x => new { x.StaffUserId, Day = x.FirstStampedAt.Date })
            .Select(g => new { g.Key.StaffUserId, Day = g.Key.Day, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var rewardReadyByStaff = await _context.Redemptions
            .AsNoTracking()
            .Where(r => r.BusinessId == businessId && r.PerformedByUserId != null &&
                        r.RedeemedAt >= startUtc && r.RedeemedAt < endUtc)
            .GroupBy(r => new { StaffUserId = r.PerformedByUserId!.Value, Day = r.RedeemedAt.Date })
            .Select(g => new { g.Key.StaffUserId, Day = g.Key.Day, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var newCustomersLookup = newCustomersByStaff
            .ToDictionary(x => (x.StaffUserId, DateOnly.FromDateTime(x.Day)), x => x.Count);
        var rewardLookup = rewardReadyByStaff
            .ToDictionary(x => (x.StaffUserId, DateOnly.FromDateTime(x.Day)), x => x.Count);

        // Restrict the scoped delete to exactly the range being recomputed.
        var existingRows = await _context.StaffDailyAnalytics
            .Where(x => x.BusinessId == businessId && x.Date >= from && x.Date <= to)
            .ToListAsync(cancellationToken);
        _context.StaffDailyAnalytics.RemoveRange(existingRows);

        var now = DateTime.UtcNow;
        foreach (var stat in grouped)
        {
            var day = DateOnly.FromDateTime(stat.Day);
            var key = (stat.StaffUserId, day);
            await _context.StaffDailyAnalytics.AddAsync(new StaffDailyAnalytics
            {
                StaffUserId = stat.StaffUserId,
                BusinessId = businessId,
                Date = day,
                Stamps = stat.Stamps,
                DistinctCustomers = stat.DistinctCustomers,
                NewCustomers = newCustomersLookup.GetValueOrDefault(key),
                RewardReadyCreated = rewardLookup.GetValueOrDefault(key),
                UpdatedAt = now
            }, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Recomputed staff_daily_analytics for business {BusinessId} range {From}..{To}. Rows={Rows}",
            businessId, from, to, grouped.Count);
    }

    private static void NormalizeRange(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            throw new ArgumentOutOfRangeException(nameof(to), "'to' must be greater than or equal to 'from'.");
        }
    }

    private static IEnumerable<(DateOnly From, DateOnly To)> ChunkRange(DateOnly from, DateOnly to)
    {
        for (var cursor = from; cursor <= to; cursor = cursor.AddDays(MaxRangeDays))
        {
            var chunkEnd = cursor.AddDays(MaxRangeDays - 1);
            yield return (cursor, chunkEnd > to ? to : chunkEnd);
        }
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionIfSupportedAsync(CancellationToken cancellationToken)
    {
        // Relational providers (PostgreSQL) support transactions; the InMemory
        // provider used by unit tests does not — skip there.
        if (_context.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true)
        {
            return null;
        }

        return await _context.Database.BeginTransactionAsync(cancellationToken);
    }

    private static (DateTime StartUtc, DateTime EndUtc) GetUtcRange(DateOnly day)
    {
        var startUtc = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return (startUtc, startUtc.AddDays(1));
    }

    private static (DateTime StartUtc, DateTime EndUtc) GetUtcRange(DateOnly from, DateOnly to)
    {
        var startUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return (startUtc, to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }
}
