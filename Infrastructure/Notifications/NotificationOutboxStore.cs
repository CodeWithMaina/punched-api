using Microsoft.EntityFrameworkCore;
using Npgsql;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Infrastructure.Notifications;

/// <summary>Claims pending notification ledger rows for one delivery batch.</summary>
public sealed class NotificationOutboxStore
{
    private readonly ApplicationDbContext _context;

    public NotificationOutboxStore(ApplicationDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// PostgreSQL uses the required row-lock SQL in one transaction. EF InMemory is
    /// the test-only fallback because it has no transactions or SQL engine; its
    /// in-process claim still changes status before returning, preserving worker state
    /// transitions without replacing the production SQL path.
    /// </summary>
    public async Task<IReadOnlyList<NotificationLog>> ClaimAsync(
        int take,
        CancellationToken ct = default)
    {
        if (take <= 0) return Array.Empty<NotificationLog>();

        if (_context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
            return await ClaimInMemoryAsync(take, ct);

        await using var transaction = await _context.Database.BeginTransactionAsync(ct);
        var ids = await ClaimPostgresIdsAsync(take, ct);
        if (ids.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return Array.Empty<NotificationLog>();
        }

        var now = DateTime.UtcNow;
        await _context.NotificationLogs
            .Where(row => ids.Contains(row.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, "processing")
                .SetProperty(row => row.UpdatedAt, now), ct);

        var claimed = await _context.NotificationLogs
            .AsNoTracking()
            .Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(ct);
        await transaction.CommitAsync(ct);
        return claimed;
    }

    /// <summary>
    /// Reclaims rows left in a stale processing state so a crashed worker does not strand work forever.
    /// </summary>
    public async Task<int> ReclaimStaleProcessingAsync(
        TimeSpan? age = null,
        CancellationToken ct = default)
    {
        var threshold = DateTime.UtcNow.Subtract(age ?? TimeSpan.FromMinutes(5));

        if (_context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            var stale = await _context.NotificationLogs
                .Where(row => row.Status == "processing" && row.UpdatedAt <= threshold)
                .ToListAsync(ct);

            foreach (var row in stale)
            {
                row.Status = "pending";
                row.UpdatedAt = DateTime.UtcNow;
                row.NextAttemptAt = row.NextAttemptAt <= DateTime.UtcNow ? row.NextAttemptAt : DateTime.UtcNow;
            }

            if (stale.Count > 0) await _context.SaveChangesAsync(ct);
            return stale.Count;
        }

        return await _context.NotificationLogs
            .Where(row => row.Status == "processing" && row.UpdatedAt <= threshold)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, "pending")
                .SetProperty(row => row.UpdatedAt, DateTime.UtcNow)
                .SetProperty(row => row.NextAttemptAt, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Safely requeues reviewed transient failures after the operator confirms the provider outcome.
    /// </summary>
    public async Task<int> RequeueTransientFailureAsync(
        Guid notificationId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        if (_context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            var row = await _context.NotificationLogs
                .SingleOrDefaultAsync(item => item.Id == notificationId && item.Status == "failed", ct);
            if (row is null || string.IsNullOrWhiteSpace(row.Error) || !row.Error.StartsWith("retry_exhausted:"))
                return 0;

            row.Status = "pending";
            row.Error = null;
            row.Attempts = 0;
            row.NextAttemptAt = now;
            row.UpdatedAt = now;
            await _context.SaveChangesAsync(ct);
            return 1;
        }

        return await _context.NotificationLogs
            .Where(row => row.Id == notificationId
                && row.Status == "failed"
                && row.Error != null
                && row.Error.StartsWith("retry_exhausted:"))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, "pending")
                .SetProperty(row => row.Attempts, 0)
                .SetProperty(row => row.Error, (string?)null)
                .SetProperty(row => row.NextAttemptAt, now)
                .SetProperty(row => row.UpdatedAt, now), ct);
    }

    private async Task<List<Guid>> ClaimPostgresIdsAsync(int take, CancellationToken ct)
    {
        var connection = _context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id
            FROM notifications
            WHERE status = 'pending'
              AND next_attempt_at <= now()
            ORDER BY created_at
            LIMIT @take
            FOR UPDATE SKIP LOCKED;
            """;
        var parameter = new NpgsqlParameter("take", take);
        command.Parameters.Add(parameter);

        var ids = new List<Guid>(take);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        return ids;
    }

    private async Task<IReadOnlyList<NotificationLog>> ClaimInMemoryAsync(int take, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var claimed = await _context.NotificationLogs
            .Where(row => row.Status == "pending" && row.NextAttemptAt <= now)
            .OrderBy(row => row.CreatedAt)
            .Take(take)
            .ToListAsync(ct);

        foreach (var row in claimed)
        {
            row.Status = "processing";
            row.UpdatedAt = now;
        }

        if (claimed.Count > 0) await _context.SaveChangesAsync(ct);
        return claimed;
    }
}