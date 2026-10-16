using PunchedApi.Application.DomainEvents;

namespace PunchedApi.Application.Handlers;

/// <summary>
/// Checkpoints <c>appointment.cancelled</c> and triggers automation rule evaluation.
/// </summary>
public sealed class AppointmentCancelledHandler : IDomainEventHandler
{
    private readonly ILogger<AppointmentCancelledHandler> _logger;
    private readonly AutomationRuleEvaluator _evaluator;

    public AppointmentCancelledHandler(
        ILogger<AppointmentCancelledHandler> logger,
        AutomationRuleEvaluator evaluator)
    {
        _logger = logger;
        _evaluator = evaluator;
    }

    public bool CanHandle(string eventType) => eventType == "appointment.cancelled";

    public async Task HandleAsync(DomainEventRecord record, CancellationToken ct = default)
    {
        _logger.LogInformation("Appointment cancelled {AggregateId} ({OccurredAt}).", record.AggregateId, record.OccurredAtUtc);
        if (record.BusinessId.HasValue)
        {
            await _evaluator.EvaluateAsync(
                record.BusinessId.Value, record.EventType, record.AggregateId,
                record.PayloadJson, record.OccurredAtUtc, ct);
        }
    }
}