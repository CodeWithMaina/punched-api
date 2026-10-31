using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Seeding.Demo.AureliaLuxe;

internal sealed record AureliaLuxeScheduleResult(
    int Appointments,
    int Resources,
    int StatusHistory,
    int RescheduleRequests,
    int Payments,
    int PaymentAttempts);

/// <summary>
/// Generates a realistic appointment book and its money: completed, upcoming,
/// cancelled and no-show visits spread across the previous three months and the
/// next month, plus payments in every supported state (paid, deposit + balance,
/// outstanding, failed, refunded, awaiting confirmation).
///
/// Scheduling is generated per staff member per day with a strictly advancing
/// cursor, so no two active appointments can overlap — the database's
/// <c>appointments_no_staff_overlap</c> exclusion constraint is satisfied by
/// construction rather than by trial and error.
/// </summary>
internal static class AureliaLuxeAppointmentSeeder
{
    private static readonly TimeZoneInfo BusinessTimeZone = ResolveBusinessTimeZone();

    public static async Task<AureliaLuxeScheduleResult> SeedAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var serviceRows = await db.ServiceCatalogItems
            .Where(s => s.BusinessId == demo.BusinessId)
            .ToListAsync(cancellationToken);

        var servicesByKey = new Dictionary<string, ServiceCatalogItem>(StringComparer.Ordinal);
        foreach (var definition in AureliaLuxeDemoData.Services)
        {
            var row = serviceRows.FirstOrDefault(s => s.Id == AureliaLuxeDemoIds.Service(definition.Key));
            if (row is not null)
            {
                servicesByKey[definition.Key] = row;
            }
        }

        if (servicesByKey.Count == 0 || demo.Customers.Count == 0)
        {
            return new AureliaLuxeScheduleResult(0, 0, 0, 0, 0, 0);
        }

        var assignments = await db.StaffServiceAssignments
            .Where(a => a.BusinessId == demo.BusinessId)
            .Select(a => new { a.StaffUserId, a.ServiceCatalogItemId })
            .ToListAsync(cancellationToken);

        var assignedStaffIds = assignments.Select(a => a.StaffUserId).ToHashSet();

        var serviceStaff = demo.Staff
            .Where(s => s.Definition.ServiceKeys.Length > 0 && assignedStaffIds.Contains(s.User.Id))
            .ToList();

        if (serviceStaff.Count == 0)
        {
            return new AureliaLuxeScheduleResult(0, 0, 0, 0, 0, 0);
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var nowUtc = DateTime.UtcNow;
        var appointments = new List<PendingAppointment>();
        var index = 0;

        BuildBook(serviceStaff, servicesByKey, demo, today, nowUtc, appointments, ref index, cancellationToken);

        WriteAppointments(db, demo, appointments);
        await db.SaveChangesAsync(cancellationToken);

        var payments = AureliaLuxePaymentSeeder.Write(db, demo, appointments, today, nowUtc);
        await db.SaveChangesAsync(cancellationToken);

        var statusHistory = appointments.Sum(a => 2 + (a.Status is "completed" or "no_show" ? 1 : 0));
        var rescheduleRequests = await db.RescheduleRequests
            .CountAsync(r => db.Appointments.Any(a => a.Id == r.AppointmentId && a.BusinessId == demo.BusinessId),
                cancellationToken);

        return new AureliaLuxeScheduleResult(
            appointments.Count,
            appointments.Sum(a => a.Services.Count),
            statusHistory,
            rescheduleRequests,
            payments.Payments,
            payments.Attempts);
    }

    // ═══════════════════════════════════════════════════════════
    //  BOOK GENERATION
    // ═══════════════════════════════════════════════════════════

    private static void BuildBook(
        IReadOnlyList<AureliaLuxeStaffHandle> serviceStaff,
        IReadOnlyDictionary<string, ServiceCatalogItem> servicesByKey,
        AureliaLuxeDemoContext demo,
        DateOnly today,
        DateTime nowUtc,
        List<PendingAppointment> appointments,
        ref int index,
        CancellationToken cancellationToken)
    {
        for (var offset = -AureliaLuxeDemoData.HistoryDays; offset <= AureliaLuxeDemoData.UpcomingDays; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var date = today.AddDays(offset);
            if (date.DayOfWeek == DayOfWeek.Sunday)
            {
                continue;
            }

            for (var s = 0; s < serviceStaff.Count; s++)
            {
                var staff = serviceStaff[s];
                var rng = new AureliaDemoRandom((date.DayNumber * 17) + (s * 31));

                if (!rng.Chance(0.68))
                {
                    continue;
                }

                var count = offset < 0 ? 1 + (rng.Chance(0.45) ? 1 : 0) : rng.Next(0, 3);
                if (count == 0)
                {
                    continue;
                }

                var candidateKeys = staff.Definition.ServiceKeys.Where(servicesByKey.ContainsKey).ToList();
                if (candidateKeys.Count == 0)
                {
                    continue;
                }

                var startHour = staff.Definition.ShiftStartHour;
                var endHour = staff.Definition.ShiftEndHour;
                if (date.DayOfWeek == DayOfWeek.Saturday)
                {
                    startHour = Math.Min(startHour, AureliaLuxeDemoData.SaturdayOpenHour);
                    endHour = Math.Min(endHour, AureliaLuxeDemoData.SaturdayCloseHour);
                }

                var cursor = startHour * 60;
                var limit = endHour * 60;

                for (var k = 0; k < count; k++)
                {
                    cursor += rng.Next(0, 30);

                    var primaryKey = rng.Pick(candidateKeys);
                    var lineKeys = new List<string> { primaryKey };

                    // A second, complementary service (wash + treatment, etc.).
                    if (rng.Chance(0.22) && candidateKeys.Count > 1)
                    {
                        lineKeys.Add(rng.Pick(candidateKeys.Where(x => x != primaryKey).ToList()));
                    }

                    var totalMinutes = lineKeys.Sum(key => servicesByKey[key].DurationMinutes);
                    if (cursor + totalMinutes > limit)
                    {
                        break;
                    }

                    var localStart = date.ToDateTime(new TimeOnly(cursor / 60, cursor % 60));
                    var scheduledAt = LocalToUtc(localStart);
                    cursor += totalMinutes;

                    var customerIndex = rng.Chance(0.62) && demo.Customers.Count >= 20
                        ? rng.Next(0, 20)
                        : rng.Next(0, demo.Customers.Count);
                    var customer = demo.Customers[customerIndex];

                    index++;
                    appointments.Add(new PendingAppointment(
                        index,
                        staff.User.Id,
                        customer,
                        scheduledAt,
                        scheduledAt.AddMinutes(totalMinutes),
                        ResolveStatus(rng, offset, scheduledAt, nowUtc),
                        lineKeys.Select(key => servicesByKey[key]).ToList()));
                }
            }
        }
    }

    /// <summary>
    /// A past visit is always in a terminal state (completed / cancelled /
    /// no-show); today's earlier slots are already served; future slots are
    /// booked or confirmed.
    /// </summary>
    private static string ResolveStatus(AureliaDemoRandom rng, int offset, DateTime scheduledAt, DateTime nowUtc)
    {
        if (offset < 0)
        {
            var roll = rng.NextDouble();
            return roll < 0.84 ? "completed" : roll < 0.93 ? "cancelled" : "no_show";
        }

        if (offset == 0 && scheduledAt < nowUtc)
        {
            return rng.Chance(0.93) ? "completed" : "no_show";
        }

        return rng.Chance(0.55) ? "confirmed" : "booked";
    }

    // ═══════════════════════════════════════════════════════════
    //  APPOINTMENTS, RESOURCES, HISTORY
    // ═══════════════════════════════════════════════════════════

    private static void WriteAppointments(
        ApplicationDbContext db,
        AureliaLuxeDemoContext demo,
        List<PendingAppointment> appointments)
    {
        foreach (var appointment in appointments)
        {
            var id = AureliaLuxeDemoIds.Appointment(appointment.Index);
            var createdAt = appointment.ScheduledAt.AddDays(-12);

            var entity = new Appointment
            {
                Id = id,
                BusinessId = demo.BusinessId,
                CustomerId = appointment.Customer.User.Id,
                StaffUserId = appointment.StaffUserId,
                ScheduledAt = appointment.ScheduledAt,
                EndAt = appointment.EndAt,
                Status = appointment.Status,
                ReminderOptOut = false,
                CreatedAt = createdAt,
            };

            var line = 0;
            foreach (var service in appointment.Services)
            {
                entity.Resources.Add(new AppointmentResource
                {
                    Id = AureliaLuxeDemoIds.AppointmentResource(appointment.Index, line),
                    AppointmentId = id,
                    ServiceCatalogItemId = service.Id,
                    Name = service.Name,
                    DurationMinutes = service.DurationMinutes,
                    Price = service.Price ?? 0m,
                    SortOrder = line,
                    CreatedAt = createdAt,
                });
                line++;
            }

            db.Appointments.Add(entity);

            WriteStatusHistory(db, demo, appointment, id, createdAt);
        }

        WriteRescheduleRequests(db, demo, appointments);
    }

    private static void WriteStatusHistory(
        ApplicationDbContext db,
        AureliaLuxeDemoContext demo,
        PendingAppointment appointment,
        Guid appointmentId,
        DateTime createdAt)
    {
        var step = 0;

        db.AppointmentStatusHistory.Add(new AppointmentStatusHistory
        {
            Id = AureliaLuxeDemoIds.AppointmentHistory(appointment.Index, ++step),
            AppointmentId = appointmentId,
            Status = "booked",
            ChangedAt = createdAt,
            ChangedByUserId = appointment.Customer.User.Id,
            Note = "Booked online.",
            CreatedAt = createdAt,
        });

        if (appointment.Status is "confirmed" or "completed" or "no_show")
        {
            var confirmedAt = appointment.ScheduledAt.AddDays(-2);
            db.AppointmentStatusHistory.Add(new AppointmentStatusHistory
            {
                Id = AureliaLuxeDemoIds.AppointmentHistory(appointment.Index, ++step),
                AppointmentId = appointmentId,
                Status = "confirmed",
                ChangedAt = confirmedAt,
                ChangedByUserId = demo.Owner.Id,
                Note = "Confirmed by the atelier.",
                CreatedAt = confirmedAt,
            });
        }

        var (terminalStatus, terminalAt, terminalBy, terminalNote) = appointment.Status switch
        {
            "completed" => ("completed", appointment.EndAt, appointment.StaffUserId, "Service delivered."),
            "cancelled" => ("cancelled", appointment.ScheduledAt.AddDays(-1), appointment.Customer.User.Id,
                "Cancelled by the guest."),
            "no_show" => ("no_show", appointment.EndAt.AddMinutes(30), appointment.StaffUserId,
                "Guest did not arrive."),
            _ => (null, appointment.EndAt, appointment.StaffUserId, string.Empty),
        };

        if (terminalStatus is null)
        {
            return;
        }

        db.AppointmentStatusHistory.Add(new AppointmentStatusHistory
        {
            Id = AureliaLuxeDemoIds.AppointmentHistory(appointment.Index, ++step),
            AppointmentId = appointmentId,
            Status = terminalStatus,
            ChangedAt = terminalAt,
            ChangedByUserId = terminalBy,
            Note = terminalNote,
            CreatedAt = terminalAt,
        });
    }

    /// <summary>
    /// Leaves three pending customer reschedule proposals on upcoming visits and
    /// two already-approved historical ones, so both sides of the reschedule
    /// workflow are visible.
    /// </summary>
    private static void WriteRescheduleRequests(
        ApplicationDbContext db,
        AureliaLuxeDemoContext demo,
        List<PendingAppointment> appointments)
    {
        var now = DateTime.UtcNow;
        var pending = 0;
        var approved = 0;

        foreach (var appointment in appointments)
        {
            if (pending < 3 && appointment.Status == "confirmed" && appointment.ScheduledAt > now.AddDays(2))
            {
                pending++;
                db.RescheduleRequests.Add(new RescheduleRequest
                {
                    Id = AureliaLuxeDemoIds.RescheduleRequest(appointment.Index),
                    AppointmentId = AureliaLuxeDemoIds.Appointment(appointment.Index),
                    RequestedByUserId = appointment.Customer.User.Id,
                    ProposedScheduledAt = appointment.ScheduledAt.AddDays(3),
                    ProposedEndAt = appointment.EndAt.AddDays(3),
                    ProposedStaffUserId = appointment.StaffUserId,
                    ProposedServicesJson = "[]",
                    Status = "pending",
                    CreatedAt = now.AddDays(-1),
                });
                continue;
            }

            if (approved < 2 && appointment.Status == "completed" && appointment.ScheduledAt < now.AddDays(-20))
            {
                approved++;
                db.RescheduleRequests.Add(new RescheduleRequest
                {
                    Id = AureliaLuxeDemoIds.RescheduleRequest(appointment.Index),
                    AppointmentId = AureliaLuxeDemoIds.Appointment(appointment.Index),
                    RequestedByUserId = appointment.Customer.User.Id,
                    ProposedScheduledAt = appointment.ScheduledAt,
                    ProposedEndAt = appointment.EndAt,
                    ProposedStaffUserId = appointment.StaffUserId,
                    ProposedServicesJson = "[]",
                    Status = "approved",
                    ResolvedAt = appointment.ScheduledAt.AddDays(-1),
                    ResolvedByUserId = demo.Owner.Id,
                    CreatedAt = appointment.ScheduledAt.AddDays(-4),
                });
            }

            if (pending >= 3 && approved >= 2)
            {
                return;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  TIME ZONE HELPERS
    // ═══════════════════════════════════════════════════════════

    /// <summary>Converts a business-local wall-clock time into UTC.</summary>
    private static DateTime LocalToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, BusinessTimeZone);
    }

    private static TimeZoneInfo ResolveBusinessTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(AureliaLuxeDemoData.TimeZoneId);
        }
        catch (Exception)
        {
            return TimeZoneInfo.CreateCustomTimeZone("EAT", TimeSpan.FromHours(3), "East Africa Time", "EAT");
        }
    }
}

internal sealed record PendingAppointment(
    int Index,
    Guid StaffUserId,
    AureliaLuxeCustomerHandle Customer,
    DateTime ScheduledAt,
    DateTime EndAt,
    string Status,
    IReadOnlyList<ServiceCatalogItem> Services);
