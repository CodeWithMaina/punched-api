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

    private async Task<List<Guid>> ClaimPostgresIdsAsync(int take, CancellationToken ct)
    {
        var connection = _context.Database.GetDbConnection();
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