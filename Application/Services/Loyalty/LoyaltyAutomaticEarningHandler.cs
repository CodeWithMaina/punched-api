using Microsoft.Extensions.Logging;
using PunchedApi.Application.Loyalty;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Loyalty;

/// <summary>
/// Evaluates a program's Active + Automatic earning rules when another module
/// publishes a qualifying domain event, and credits stamps through the single
/// stamping write path.
///
/// Ownership boundaries respected here:
/// <list type="bullet">
/// <item>Reads appointment/referral records; never mutates them.</item>
/// <item>Only <see cref="StampingMode.Automatic"/> rules are event-driven —
/// Manual rules require an explicit staff action.</item>
/// <item>No retroactive stamping: a rule only fires for events at/after its
/// <see cref="LoyaltyEarningRule.ActivatedAt"/>, and nothing scans history.</item>
/// <item>A customer with no card for the program is skipped rather than
/// auto-enrolled.</item>
/// </list>
/// </summary>
public sealed partial class LoyaltyAutomaticEarningHandler : ILoyaltyEventHandler
{
    private readonly ApplicationDbContext _context;
    private readonly ILoyaltyStampingService _stampingService;
    private readonly IModuleEntitlementService _entitlementService;
    private readonly ILogger<LoyaltyAutomaticEarningHandler> _logger;

    public LoyaltyAutomaticEarningHandler(
        ApplicationDbContext context,
        ILoyaltyStampingService stampingService,
        IModuleEntitlementService entitlementService,
        ILogger<LoyaltyAutomaticEarningHandler> logger)
    {
        _context = context;
        _stampingService = stampingService;
        _entitlementService = entitlementService;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanHandle(ILoyaltyDomainEvent domainEvent) =>
        domainEvent is AppointmentCompletedEvent or ReferralCompletedEvent;

    /// <inheritdoc />
    public async Task HandleAsync(ILoyaltyDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        switch (domainEvent)
        {
            case AppointmentCompletedEvent appointment:
                await ProcessAppointmentCompletedAsync(appointment, cancellationToken);
                break;

            case ReferralCompletedEvent referral:
                await ProcessReferralCompletedAsync(referral, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// A completed appointment awards stamps for the program's Active + Automatic
    /// appointment rule.
    ///
    /// Double-reward guard (spec §16): when an appointment rule is active it is
    /// authoritative and the service rule is NOT evaluated, so one completed
    /// appointment can never award two sets of stamps. The service rule only
    /// applies when no appointment rule is active, and then only for a matching
    /// service.
    /// </summary>
    private async Task ProcessAppointmentCompletedAsync(
        AppointmentCompletedEvent domainEvent, CancellationToken cancellationToken)
    {
        var appointmentRuleCredited = await CreditForSourceAsync(
            domainEvent.BusinessId, EarningSource.Appointment, domainEvent.OccurredAt,
            domainEvent.CustomerId, domainEvent.AppointmentId,
            qualifyingServiceIds: null, reason: "Completed appointment", cancellationToken);

        if (appointmentRuleCredited)
        {
            _logger.LogDebug(
                "Appointment {AppointmentId} stamped via the appointment rule; service rule skipped to avoid a double reward.",
                domainEvent.AppointmentId);
            return;
        }

        await CreditForSourceAsync(
            domainEvent.BusinessId, EarningSource.Service, domainEvent.OccurredAt,
            domainEvent.CustomerId, domainEvent.AppointmentId,
            qualifyingServiceIds: domainEvent.ServiceIds, reason: "Completed service", cancellationToken);
    }

    /// <summary>
    /// A successful referral awards stamps to the REFERRER. The referred customer
    /// never receives referral stamps.
    ///
    /// Referral earning is a cross-module capability: without the Referrals module
    /// entitlement, referral rules are not evaluated at all.
    /// </summary>
    private async Task ProcessReferralCompletedAsync(
        ReferralCompletedEvent domainEvent, CancellationToken cancellationToken)
    {
        if (!await _entitlementService.IsModuleEnabledAsync(domainEvent.BusinessId, "referral"))
        {
            _logger.LogInformation(
                "ReferralCompleted ignored for business {BusinessId}: the referral module is not enabled.",
                domainEvent.BusinessId);
            return;
        }

        await CreditForSourceAsync(
            domainEvent.BusinessId, EarningSource.Referral, domainEvent.OccurredAt,
            domainEvent.ReferrerId, domainEvent.ReferralId,
            qualifyingServiceIds: null, reason: "Successful referral", cancellationToken);
    }
}