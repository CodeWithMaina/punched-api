using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Loyalty;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Appointment-completion qualification trigger for referrals.
///
/// Ownership (spec §6): Referrals owns determining when a referral becomes
/// successful. Loyalty only reacts to the published fact, so it never inspects
/// referral state itself.
/// </summary>
public partial class ReferralService
{
    /// <summary>
    /// A referral becomes successful when the referred customer completes their
    /// FIRST qualifying appointment at the business.
    ///
    /// "Referral created" is NOT the same as "referral successful": creating a
    /// referral, or merely booking an appointment, must never qualify it.
    /// </summary>
    public async Task ProcessAppointmentCompletionAsync(
        Guid refereeId, Guid businessId, Guid appointmentId)
    {
        try
        {
            // Only the FIRST completed appointment qualifies. Any later completion
            // is a repeat of the same milestone and must not re-qualify.
            var completedCount = await _context.Appointments
                .AsNoTracking()
                .CountAsync(a => a.CustomerId == refereeId
                    && a.BusinessId == businessId
                    && a.Status == "completed");

            if (completedCount != 1)
            {
                _logger.LogDebug(
                    "Referral qualification skipped for referee {RefereeId}: {Count} completed appointments (first only qualifies).",
                    refereeId, completedCount);
                return;
            }

            // Is a referral actually waiting on this referee?
            var referral = await _context.Referrals
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.RefereeId == refereeId
                    && r.BusinessId == businessId
                    && (r.Status == ReferralStatus.Pending || r.Status == ReferralStatus.Activated)
                    && r.ExpiresAt > DateTime.UtcNow);

            if (referral == null) return;

            // Reuse the shared qualification + reward routine. It is idempotent,
            // so a concurrent or retried trigger cannot double-qualify.
            await ProcessFirstStampReferralAsync(refereeId, businessId);

            // Publish the fact AFTER the qualifying state is committed, so Loyalty
            // reacting (or failing) can never roll back the referral itself.
            await _loyaltyEvents.PublishAsync(new ReferralCompletedEvent(
                ReferralId: referral.Id,
                BusinessId: businessId,
                ReferrerId: referral.ReferrerId,
                RefereeId: refereeId,
                OccurredAt: DateTime.UtcNow));

            _logger.LogInformation(
                "Referral {ReferralId} completed via first appointment {AppointmentId}; ReferralCompleted published.",
                referral.Id, appointmentId);
        }
        catch (Exception ex)
        {
            // Referral qualification must never break the appointment transition
            // that triggered it — the appointment is already committed.
            _logger.LogError(
                ex,
                "Error processing appointment-completion referral for referee {RefereeId}, business {BusinessId}",
                refereeId, businessId);
        }
    }
}