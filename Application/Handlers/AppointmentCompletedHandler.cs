using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DomainEvents;
using PunchedApi.Application.Loyalty;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Handlers;

/// <summary>
/// Bridges the durable <c>appointment.completed</c> domain event into the
/// authoritative automatic-earning path and triggers automation rule evaluation.
/// </summary>
public sealed class AppointmentCompletedHandler : IDomainEventHandler
{
    private readonly ApplicationDbContext _context;
    private readonly ILoyaltyEventBus _loyaltyEvents;
    private readonly ILogger<AppointmentCompletedHandler> _logger;
    private readonly AutomationRuleEvaluator _evaluator;

    public AppointmentCompletedHandler(
        ApplicationDbContext context,
        ILoyaltyEventBus loyaltyEvents,
        ILogger<AppointmentCompletedHandler> logger,
        AutomationRuleEvaluator evaluator)
    {
        _context = context;
        _loyaltyEvents = loyaltyEvents;
        _logger = logger;
        _evaluator = evaluator;
    }

    public bool CanHandle(string eventType) => eventType == "appointment.completed";

    public async Task HandleAsync(DomainEventRecord record, CancellationToken ct = default)
    {
        if (record.AggregateId == null || record.BusinessId == null)
        {
            _logger.LogDebug("Appointment completed event without aggregate/business scope; skipped.");
            return;
        }

        var appointment = await _context.Appointments
            .AsNoTracking()
            .Include(a => a.Resources)
            .FirstOrDefaultAsync(a => a.Id == record.AggregateId.Value, ct);
        if (appointment == null)
        {
            _logger.LogDebug("Appointment completed event for missing appointment {Appointment}; skipped.", record.AggregateId);
            return;
        }

        if (appointment.Status != "completed")
        {
            _logger.LogDebug("Appointment {Appointment} is {Status}, not completed; earning skipped.", appointment.Id, appointment.Status);
            return;
        }

        var serviceIds = appointment.Resources
            .Select(r => r.ServiceCatalogItemId)
            .Distinct()
            .ToList();

        await _loyaltyEvents.PublishAsync(new AppointmentCompletedEvent(
            appointment.Id,
            appointment.BusinessId,
            appointment.CustomerId,
            appointment.StaffUserId,
            serviceIds,
            record.OccurredAtUtc), ct);

        // Trigger automation rules for this completed event.
        await _evaluator.EvaluateAsync(
            record.BusinessId.Value, record.EventType, record.AggregateId,
            record.PayloadJson, record.OccurredAtUtc, ct);

        _logger.LogInformation("Appointment completed {AggregateId} bridged to loyalty earning.", record.AggregateId);
    }
}