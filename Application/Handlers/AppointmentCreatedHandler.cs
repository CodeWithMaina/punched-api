using PunchedApi.Application.DomainEvents;

namespace PunchedApi.Application.Handlers;

/// <summary>
/// Checkpoints <c>appointment.created</c> and triggers automation rule evaluation.
/// </summary>
public sealed class AppointmentCreatedHandler : IDomainEventHandler
{
    private readonly ILogger<AppointmentCreatedHandler> _logger;
    private readonly AutomationRuleEvaluator _evaluator;

    public AppointmentCreatedHandler(
        ILogger<AppointmentCreatedHandler> logger,
        AutomationRuleEvaluator evaluator)
    {
        _logger = logger;
        _evaluator = evaluator;
    }

    public bool CanHandle(string eventType) => eventType == "appointment.created";

    public async Task HandleAsync(DomainEventRecord record, CancellationToken ct = default)
    {
        _logger.LogInformation("Appointment created {AggregateId} ({OccurredAt}).", record.AggregateId, record.OccurredAtUtc);
        if (record.BusinessId.HasValue)
        {
            await _evaluator.EvaluateAsync(
                record.BusinessId.Value, record.EventType, record.AggregateId,
                record.PayloadJson, record.OccurredAtUtc, ct);
        }
    }
}