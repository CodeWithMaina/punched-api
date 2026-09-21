namespace PunchedApi.Application.Loyalty;

/// <inheritdoc />
public sealed class LoyaltyEventBus : ILoyaltyEventBus
{
    private readonly IEnumerable<ILoyaltyEventHandler> _handlers;
    private readonly ILogger<LoyaltyEventBus> _logger;

    public LoyaltyEventBus(
        IEnumerable<ILoyaltyEventHandler> handlers,
        ILogger<LoyaltyEventBus> logger)
    {
        _handlers = handlers;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PublishAsync(
        ILoyaltyDomainEvent domainEvent,
        CancellationToken cancellationToken = default)
    {
        // Isolate every handler: Loyalty is a downstream consumer, so it must
        // never be able to fail the module that produced the event.
        foreach (var handler in _handlers)
        {
            if (!handler.CanHandle(domainEvent)) continue;

            try
            {
                await handler.HandleAsync(domainEvent, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Loyalty handler {Handler} failed for {EventType} on business {BusinessId}.",
                    handler.GetType().Name,
                    domainEvent.GetType().Name,
                    domainEvent.BusinessId);
            }
        }
    }
}