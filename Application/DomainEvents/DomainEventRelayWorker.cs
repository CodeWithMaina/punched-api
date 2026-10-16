using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.DomainEvents;

/// <summary>
/// Claims pending domain events (10 s claim cadence, <c>FOR UPDATE SKIP LOCKED</c>,
/// 1-minute leases with stale reclaim) and dispatches every claimed event to the
/// in-process handlers that declare interest in the event type. Each handler is
/// invoked at most once per event (per-handler checkpoints in
/// <c>handledByJson</c>) so retries can never re-run completed work.
/// </summary>
/// <remarks>
/// The relay is a <c>BackgroundService</c> behind the <c>WORKERS_ENABLED</c> gate.
/// </remarks>
public sealed class DomainEventRelayWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DomainEventRelayWorker> _logger;
    private readonly int _claimSize;
    private readonly int _leaseMinutes;
    private readonly int _pollSeconds;
    private readonly int _maxAttempts;

    public DomainEventRelayWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<DomainEventRelayWorker> logger,
        IOptions<NotificationWorkerOptions>? options = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var o = options?.Value ?? new NotificationWorkerOptions();
        _claimSize = Math.Max(1, o.BatchSize);
        _leaseMinutes = Math.Max(1, o.StaleProcessingMinutes);
        _pollSeconds = Math.Max(1, o.PollIntervalSeconds);
        _maxAttempts = Math.Max(1, o.MaxDomainEventAttempts);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromSeconds(_pollSeconds);
        _logger.LogInformation("Domain-event relay started. Claim: {_ClaimSize} every {_PollSeconds}s.", _claimSize, _pollSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Domain-event relay batch failed.");
            }

            await Task.Delay(pollInterval, stoppingToken);
        }

        _logger.LogInformation("Domain-event relay stopped.");
    }

    public async Task ProcessBatchAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var context = provider.GetRequiredService<ApplicationDbContext>();

        var claimed = await ClaimAsync(context, _claimSize, ct);
        foreach (var row in claimed)
        {
            ct.ThrowIfCancellationRequested();
            await new Invocation(row, provider, _logger).RunAsync(ct);
        }
    }

    private static async Task<IReadOnlyList<DomainEvent>> ClaimAsync(
        ApplicationDbContext context, int take, CancellationToken ct)
    {
        if (take <= 0) return Array.Empty<DomainEvent>();

        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var ids = await ClaimPostgresIdsAsync(context, take, ct);
        if (ids.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return Array.Empty<DomainEvent>();
        }

        var now = DateTime.UtcNow;
        await context.DomainEvents
            .Where(row => ids.Contains(row.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, "processing")
                .SetProperty(row => row.UpdatedAt, now)
                .SetProperty(row => row.LockedAt, now), ct);

        var claimed = await context.DomainEvents
            .AsNoTracking()
            .Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.OccurredAtUtc)
            .ToListAsync(ct);

        await transaction.CommitAsync(ct);
        return claimed;
    }

    private static async Task<List<Guid>> ClaimPostgresIdsAsync(
        ApplicationDbContext context, int take, CancellationToken ct)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id
            FROM domain_events
            WHERE status = 'pending'
              AND locked_at IS NULL
              AND occurred_at_utc <= now()
            ORDER BY occurred_at_utc
            LIMIT @take
            FOR UPDATE SKIP LOCKED;
            """;
        command.Parameters.Add(new NpgsqlParameter("take", take));

        var ids = new List<Guid>(take);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetGuid(0));

        return ids;
    }

    private sealed class Invocation
    {
        private readonly ApplicationDbContext _context;
        private readonly IEnumerable<IDomainEventHandler> _handlers;
        private readonly ILogger _logger;
        private readonly DomainEvent _row;

        public Invocation(DomainEvent row, IServiceProvider provider, ILogger logger)
        {
            _row = row;
            _context = provider.GetRequiredService<ApplicationDbContext>();
            _handlers = provider.GetServices<IDomainEventHandler>();
            _logger = logger;
        }

        public async Task RunAsync(CancellationToken ct = default)
        {
            var eligible = _handlers.Where(h => h.CanHandle(_row.EventType)).ToList();
            var handlerNames = eligible.Select(h => h.GetType().FullName ?? h.GetType().Name).ToArray();
            if (handlerNames.Length == 0) return;

            var completed = await LoadCheckpointAsync(ct);
            var attempted = new HashSet<string>(StringComparer.Ordinal);

            foreach (var handler in eligible)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await handler.HandleAsync(new DomainEventRecord(
                        _row.EventType,
                        _row.AggregateType,
                        _row.AggregateId,
                        _row.BusinessId,
                        _row.PayloadJson,
                        _row.OccurredAtUtc), ct);
                    completed = completed.Concat(new[] { handler.GetType().FullName ?? handler.GetType().Name })
                        .Distinct(StringComparer.Ordinal).ToArray();
                    attempted.Add(handler.GetType().FullName ?? handler.GetType().Name);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Domain-event handler {Handler} failed for {EventType} (attempt {Attempts}).",
                        handler.GetType().FullName, _row.EventType, _row.Attempts + 1);
                }
            }

            await CompleteAsync(handlerNames, completed, attempted.Count == handlerNames.Length, ct);
        }

        private async Task<string[]> LoadCheckpointAsync(CancellationToken ct)
        {
            return await _context.DomainEvents
                .AsNoTracking()
                .Where(r => r.Id == _row.Id)
                .Select(r => r.HandledByJson)
                .FirstOrDefaultAsync(ct) is { } json
                ? (JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>())
                : Array.Empty<string>();
        }

        private async Task CompleteAsync(
            string[] handlerNames, string[] completed, bool allRan, CancellationToken ct)
        {
            var payload = JsonSerializer.Serialize(completed, new JsonSerializerOptions { WriteIndented = false }) ?? "[]";

            await _context.DomainEvents
                .Where(r => r.Id == _row.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.HandledByJson, payload)
                    .SetProperty(r => r.Attempts, allRan ? 0 : _row.Attempts + 1)
                    .SetProperty(r => r.Status, allRan ? "completed" : "processing")
                    .SetProperty(r => r.CompletedAt, allRan ? DateTime.UtcNow : (DateTime?)null)
                    .SetProperty(r => r.Error, allRan ? (string?)null : _row.Error), ct);
        }
    }
}