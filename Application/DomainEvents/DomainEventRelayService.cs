using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.DomainEvents;

/// <inheritdoc />
public sealed class DomainEventRelayService : IDomainEventPublisher
{
    private readonly ApplicationDbContext _context;

    public DomainEventRelayService(ApplicationDbContext context) => _context = context;

    /// <summary>
    /// Appends the event row to the caller's unit of work and returns.
    /// <para/>
    /// The row is durable (writes atomically with the state change on
    /// <see cref="ApplicationDbContext.SaveChangesAsync"/>) but relay happens
    /// asynchronously in <see cref="DomainEventRelayWorker"/> so handler latency
    /// never blocks the caller or its transaction.
    /// </summary>
    public async Task PublishAsync(DomainEventRecord record, CancellationToken ct = default)
    {
        var row = new DomainEvent
        {
            EventType = record.EventType,
            AggregateType = record.AggregateType,
            AggregateId = record.AggregateId,
            BusinessId = record.BusinessId,
            PayloadJson = record.PayloadJson,
            OccurredAtUtc = record.OccurredAtUtc,
            Status = "pending",
            Attempts = 0,
            HandledByJson = "[]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _context.DomainEvents.AddAsync(row, ct);
    }
}
