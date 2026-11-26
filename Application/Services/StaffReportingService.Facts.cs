using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// The per-staff aggregates both reports are built from, loaded once per window.
/// </summary>
public partial class StaffReportingService
{
    private sealed record StatusFacts(int Completed, int Cancelled, int NoShow, int Upcoming, int Other);
    private sealed record ServiceFacts(decimal Value, int Lines, int Minutes);
    private sealed record RatingFacts(decimal Sum, int Count);

    /// <summary>Everything one window contributes, keyed by staff user id.</summary>
    private sealed record WindowFacts(
        Dictionary<Guid, StatusFacts> Appointments,
        Dictionary<Guid, ServiceFacts> Services,
        Dictionary<Guid, decimal> Collected,
        Dictionary<Guid, decimal> Refunded,
        Dictionary<Guid, int> PendingVerification,
        Dictionary<Guid, RatingFacts> Ratings,
        Dictionary<Guid, int> RepeatAppointments);

    private static readonly StatusFacts EmptyStatus = new(0, 0, 0, 0, 0);

    /// <summary>
    /// Loads a window with seven aggregate queries (appointment states, completed
    /// service lines, settled payments, refunds, claims awaiting a decision, reviews
    /// and repeat-customer pairs). Every query is grouped by staff id in the database;
    /// nothing is enumerated per staff member.
    /// </summary>
    private async Task<WindowFacts> LoadWindowAsync(
        Guid businessId, DateTime fromUtc, DateTime endExclusiveUtc, CancellationToken cancellationToken)
    {
        var appointmentsInWindow = _context.Appointments.AsNoTracking()
            .Where(a => a.BusinessId == businessId
                && a.StaffUserId != null
                && a.ScheduledAt >= fromUtc
                && a.ScheduledAt < endExclusiveUtc);

        // 1. Appointment states.
        var statusRows = await appointmentsInWindow
            .GroupBy(a => new { a.StaffUserId, a.Status })
            .Select(g => new { g.Key.StaffUserId, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var statuses = new Dictionary<Guid, StatusFacts>();
        foreach (var row in statusRows)
        {
            if (row.StaffUserId is not Guid staffId) continue;

            var current = statuses.GetValueOrDefault(staffId, EmptyStatus);
            statuses[staffId] = row.Status switch
            {
                AppointmentStatuses.Completed => current with { Completed = row.Count },
                AppointmentStatuses.Cancelled => current with { Cancelled = row.Count },
                AppointmentStatuses.NoShow => current with { NoShow = row.Count },
                AppointmentStatuses.Booked or AppointmentStatuses.Confirmed
                    or AppointmentStatuses.PendingPayment
                    => current with { Upcoming = current.Upcoming + row.Count },
                _ => current with { Other = current.Other + row.Count }
            };
        }

        // 2. Completed service lines: the booked price snapshot, line count and minutes.
        var serviceRows = await _context.AppointmentResources.AsNoTracking()
            .Join(appointmentsInWindow.Where(a => a.Status == AppointmentStatuses.Completed),
                r => r.AppointmentId, a => a.Id,
                (r, a) => new { a.StaffUserId, r.Price, r.DurationMinutes })
            .GroupBy(x => x.StaffUserId)
            .Select(g => new
            {
                StaffUserId = g.Key,
                // Summed as double because SQLite cannot aggregate decimal in SQL;
                // PostgreSQL can, but the cast keeps the same group-by query portable.
                // Every result is rounded back to 2dp money before it is reported.
                Value = (decimal)g.Sum(x => (double)x.Price),
                Lines = g.Count(),
                Minutes = g.Sum(x => x.DurationMinutes)
            })
            .ToListAsync(cancellationToken);

        var services = serviceRows
            .Where(r => r.StaffUserId is not null)
            .ToDictionary(r => r.StaffUserId!.Value, r => new ServiceFacts(r.Value, r.Lines, r.Minutes));

        // 3. Money actually received, attributed through the appointment.
        var collectedRows = await _context.Payments.AsNoTracking()
            .Where(p => p.BusinessId == businessId && p.AppointmentId != null
                && SettledStatuses.Contains(p.Status))
            .Join(appointmentsInWindow, p => p.AppointmentId!.Value, a => a.Id,
                (p, a) => new { a.StaffUserId, p.Amount })
            .GroupBy(x => x.StaffUserId)
            .Select(g => new { StaffUserId = g.Key, Amount = (decimal)g.Sum(x => (double)x.Amount) })
            .ToListAsync(cancellationToken);

        var collected = collectedRows
            .Where(r => r.StaffUserId is not null)
            .ToDictionary(r => r.StaffUserId!.Value, r => r.Amount);

        // 4. Refunds and reversals against the same appointments.
        var refundedRows = await _context.Payments.AsNoTracking()
            .Where(p => p.BusinessId == businessId && p.AppointmentId != null
                && RefundedStatuses.Contains(p.Status))
            .Join(appointmentsInWindow, p => p.AppointmentId!.Value, a => a.Id,
                (p, a) => new { a.StaffUserId, p.Amount })
            .GroupBy(x => x.StaffUserId)
            .Select(g => new { StaffUserId = g.Key, Amount = (decimal)g.Sum(x => (double)x.Amount) })
            .ToListAsync(cancellationToken);

        var refunded = refundedRows
            .Where(r => r.StaffUserId is not null)
            .ToDictionary(r => r.StaffUserId!.Value, r => r.Amount);

        // 5. Manual M-PESA claims waiting on a human decision.
        var pendingRows = await _context.Payments.AsNoTracking()
            .Where(p => p.BusinessId == businessId && p.AppointmentId != null
                && AwaitingDecisionStatuses.Contains(p.Status))
            .Join(appointmentsInWindow, p => p.AppointmentId!.Value, a => a.Id,
                (p, a) => a.StaffUserId)
            .GroupBy(staffUserId => staffUserId)
            .Select(g => new { StaffUserId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var pending = pendingRows
            .Where(r => r.StaffUserId is not null)
            .ToDictionary(r => r.StaffUserId!.Value, r => r.Count);

        // 6. Published review ratings captured in the window.
        var ratingRows = await _context.Reviews.AsNoTracking()
            .Where(r => r.BusinessId == businessId
                && r.StaffUserId != null
                && r.Status == ReviewStatuses.Published
                && r.CreatedAt >= fromUtc
                && r.CreatedAt < endExclusiveUtc)
            .GroupBy(r => r.StaffUserId)
            .Select(g => new { StaffUserId = g.Key, Sum = (decimal)g.Sum(r => (double)r.Rating), Count = g.Count() })
            .ToListAsync(cancellationToken);

        var ratings = ratingRows
            .Where(r => r.StaffUserId is not null)
            .ToDictionary(r => r.StaffUserId!.Value, r => new RatingFacts(r.Sum, r.Count));

        // 7. Repeat customers: a customer with more than one completed visit in the window.
        var pairRows = await appointmentsInWindow
            .Where(a => a.Status == AppointmentStatuses.Completed)
            .GroupBy(a => new { a.StaffUserId, a.CustomerId })
            .Select(g => new { g.Key.StaffUserId, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var repeat = new Dictionary<Guid, int>();
        foreach (var row in pairRows.Where(r => r.StaffUserId is not null && r.Count > 1))
        {
            var staffId = row.StaffUserId!.Value;
            repeat[staffId] = repeat.GetValueOrDefault(staffId) + row.Count;
        }

        return new WindowFacts(statuses, services, collected, refunded, pending, ratings, repeat);
    }
}

