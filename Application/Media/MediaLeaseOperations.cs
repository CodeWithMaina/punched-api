using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Media;

/// <summary>
/// The lease state machine behind media processing, extracted verbatim from
/// <see cref="MediaProcessingWorker"/> and <see cref="MediaCleanupWorker"/> so it can be
/// regression-tested directly instead of only through long-running BackgroundService loops.
/// Every method executes its predicate inside a single SQL statement (ExecuteUpdateAsync),
/// so two callers racing for the same row can never both win — the database decides which
/// UPDATE matched.
///
/// Semantics (unchanged from the workers that call these):
///  - CLAIM:   eligible when status is Uploaded/Processing, attempts &lt; MaxAttempts, and
///             the previous lease is absent or already expired. Sets a fresh token, extends
///             the lease, increments attempts, and moves the row to Processing.
///  - RENEW:   succeeds only while the row is still Processing AND still carries this
///             caller's token — a worker that lost its lease gets 0 rows back (i.e. false)
///             and its heartbeat loop then cancels the in-flight processing.
///  - RECLAIM: the cleanup worker fails Processing rows whose lease expired at or before
///             the supplied clock AND whose attempt budget is exhausted. Rows with attempts
///             remaining are deliberately left alone: the processing worker reclaims those
///             itself, and failing them here would burn an attempt the worker should own.
///
/// All comparisons use the caller-supplied <c>now</c> (workers pass DateTime.UtcNow) so
/// tests can pin the clock and every comparison happens server-side in SQL.
/// </summary>
public static class MediaLeaseOperations
{
    /// <summary>Atomically claims one media row for processing. Returns rows affected (0 or 1).</summary>
    public static Task<int> ClaimAsync(
        ApplicationDbContext db,
        Guid mediaId,
        DateTime now,
        string leaseToken,
        MediaStorageOptions options,
        CancellationToken cancellationToken)
        => db.Media
            .Where(x => x.Id == mediaId &&
                        (x.Status == MediaStatus.Uploaded || x.Status == MediaStatus.Processing) &&
                        x.ProcessingAttempts < options.Processing.MaxAttempts &&
                        (x.ProcessingLeaseUntil == null || x.ProcessingLeaseUntil <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, MediaStatus.Processing)
                .SetProperty(x => x.ProcessingLeaseToken, leaseToken)
                .SetProperty(x => x.ProcessingLeaseUntil, now.AddMinutes(options.Processing.LeaseMinutes))
                .SetProperty(x => x.ProcessingAttempts, x => x.ProcessingAttempts + 1)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);

    /// <summary>
    /// Extends a live lease. Returns rows affected: 1 = still ours, 0 = lease lost (wrong
    /// token, row no longer Processing, or row gone).
    /// </summary>
    public static Task<int> RenewAsync(
        ApplicationDbContext db,
        Guid mediaId,
        string leaseToken,
        DateTime now,
        MediaStorageOptions options,
        CancellationToken cancellationToken)
        => db.Media
            .Where(x => x.Id == mediaId && x.Status == MediaStatus.Processing && x.ProcessingLeaseToken == leaseToken)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ProcessingLeaseUntil, now.AddMinutes(options.Processing.LeaseMinutes))
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);

    /// <summary>
    /// Fails Processing rows whose lease expired at or before <paramref name="now"/> and
    /// whose attempt budget is exhausted; records stale-lease telemetry for each reclaimed
    /// row. Idempotent: a second run finds nothing to reclaim.
    /// </summary>
    public static async Task ReclaimExpiredAsync(
        ApplicationDbContext db,
        MediaStorageOptions options,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var expiredLeases = await db.Media
            .Where(x => x.Status == MediaStatus.Processing && x.ProcessingLeaseUntil != null && x.ProcessingLeaseUntil <= now)
            .Take(options.CleanupBatchSize)
            .ToListAsync(cancellationToken);
        foreach (var stale in expiredLeases)
        {
            if (stale.ProcessingAttempts >= options.Processing.MaxAttempts)
            {
                if (stale.ProcessingLeaseUntil is DateTime leaseUntil)
                    MediaMetrics.RecordStaleLeaseAge(now - leaseUntil, stale.Purpose);
                MediaMetrics.RecordStaleLeasesReclaimed(stale.Purpose, 1);
            }
        }

        await db.Media
            .Where(x => x.Status == MediaStatus.Processing &&
                        x.ProcessingAttempts >= options.Processing.MaxAttempts &&
                        x.ProcessingLeaseUntil != null && x.ProcessingLeaseUntil <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, MediaStatus.Failed)
                .SetProperty(x => x.LastErrorCode, "PROCESSING_LEASE_EXPIRED")
                .SetProperty(x => x.ProcessingLeaseToken, (string?)null)
                .SetProperty(x => x.ProcessingLeaseUntil, (DateTime?)null)
                .SetProperty(x => x.NextAttemptAt, (DateTime?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
    }
}
