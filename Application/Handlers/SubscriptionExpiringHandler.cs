using PunchedApi.Application.DomainEvents;

namespace PunchedApi.Application.Handlers;

/// <summary>
/// Checkpoints <c>subscription.expiring</c>. Producers re-evaluate business
/// health at send time, so this handler is deliberately thin.
/// </summary>
public sealed class SubscriptionExpiringHandler : IDomainEventHandler
{
    private readonly ILogger<SubscriptionExpiringHandler> _logger;

    public SubscriptionExpiringHandler(ILogger<SubscriptionExpiringHandler> logger) => _logger = logger;

    public bool CanHandle(string eventType) => eventType == "subscription.expiring";

    public Task HandleAsync(DomainEventRecord record, CancellationToken ct = default)
    {
        _logger.LogInformation("Subscription expiring for {BusinessId} ({OccurredAt}).", record.BusinessId, record.OccurredAtUtc);
        return Task.CompletedTask;
    }
}