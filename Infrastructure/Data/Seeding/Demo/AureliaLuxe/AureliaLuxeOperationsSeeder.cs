using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Seeding.Demo.AureliaLuxe;

internal sealed record AureliaLuxeOperationsResult(
    int AttendanceSessions,
    int AttendanceEvents,
    int Reviews,
    int Notifications,
    int NotificationLogs,
    int Segments,
    int Insights);

/// <summary>
/// Generates the atelier's day-to-day operational records: staff attendance
/// (with late arrivals, absences and an open shift), moderated customer reviews,
/// in-app notifications, the notification ledger, customer segments and business
/// insights.
///
/// Nothing here is delivered externally. In-app notifications are internal rows;
/// the notification ledger is written in terminal states only (<c>sent</c> /
/// <c>failed</c>) so the delivery worker can never pick one up and email or text
/// a real address, and no reminder job is left pending.
/// </summary>
internal static class AureliaLuxeOperationsSeeder
{
    public static async Task<AureliaLuxeOperationsResult> SeedAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var nowUtc = DateTime.UtcNow;

        var location = await db.AttendanceLocations
            .FirstOrDefaultAsync(l => l.BusinessId == demo.BusinessId, cancellationToken);

        var credential = await db.AttendanceQrCredentials
            .FirstOrDefaultAsync(c => c.BusinessId == demo.BusinessId, cancellationToken);

        var attendance = WriteAttendance(db, demo, today, nowUtc, location, credential, cancellationToken);
        var reviews = WriteReviews(db, demo, nowUtc, cancellationToken);
        var engagement = await WriteEngagementAsync(db, demo, today, nowUtc, cancellationToken);

        return new AureliaLuxeOperationsResult(
            attendance.Sessions,
            attendance.Events,
            reviews,
            engagement.Notifications,
            engagement.NotificationLogs,
            engagement.Segments,
            engagement.Insights);
    }

    // ═══════════════════════════════════════════════════════════
    //  ATTENDANCE
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Writes the attendance ledger in four passes. The schema deliberately has
    /// an FK cycle (sessions ↔ events), so events are inserted first without a
    /// session link, sessions second, then the closing events, and finally the
    /// sessions are stamped with their closing event — four round trips in
    /// total rather than four per shift.
    /// </summary>
    private static (int Sessions, int Events) WriteAttendance(
        ApplicationDbContext db,
        AureliaLuxeDemoContext demo,
        DateOnly today,
        DateTime nowUtc,
        AttendanceLocation? location,
        AttendanceQrCredential? credential,
        CancellationToken cancellationToken)
    {
        var events = 0;
        var sessions = 0;
        var openings = new List<ShiftAttendance>();

        for (var offset = -AureliaLuxeDemoData.AttendanceDays; offset <= 0; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var date = today.AddDays(offset);
            if (date.DayOfWeek == DayOfWeek.Sunday)
            {
                continue;
            }

            for (var s = 0; s < demo.Staff.Count; s++)
            {
                var staff = demo.Staff[s];
                var rng = new AureliaDemoRandom((date.DayNumber * 41) + (s * 13));

                // Roughly one absence per staff member per month.
                if (rng.Chance(0.05))
                {
                    continue;
                }

                var shiftStart = staff.Definition.ShiftStartHour;
                var shiftEnd = staff.Definition.ShiftEndHour;
                if (date.DayOfWeek == DayOfWeek.Saturday)
                {
                    shiftStart = Math.Min(shiftStart, AureliaLuxeDemoData.SaturdayOpenHour);
                    shiftEnd = Math.Min(shiftEnd, AureliaLuxeDemoData.SaturdayCloseHour);
                }

                // 15% of shifts start late (5–35 minutes).
                var lateMinutes = rng.Chance(0.15) ? rng.Next(5, 36) : -rng.Next(0, 9);
                var openedAt = date.ToDateTime(new TimeOnly(shiftStart, 0), DateTimeKind.Utc)
                    .AddMinutes(lateMinutes);

                var closedAt = date.ToDateTime(new TimeOnly(shiftEnd, 0), DateTimeKind.Utc)
                    .AddMinutes(rng.Next(-10, 31));

                if (openedAt > nowUtc)
                {
                    continue;
                }

                var isOpenShift = offset == 0 && nowUtc < closedAt;
                openings.Add(new ShiftAttendance(staff, date, openedAt, closedAt, isOpenShift));
            }
        }

        // Pass 1 — CLOCK_IN events.
        foreach (var shift in openings)
        {
            db.AttendanceEvents.Add(BuildEvent(demo, shift, "ClockIn", shift.OpenedAt, null, location, credential));
            events++;
        }

        db.SaveChanges();

        // Pass 2 — sessions. A closed session must be inserted already closed:
        // the partial unique index allows only ONE open (closed_at IS NULL) row
        // per staff member, so leaving history rows open until pass 4 would
        // collide. `closed_at` in turn requires a non-null closing event, and the
        // real ClockOut event cannot exist yet (it references the session), so
        // the session is written pointing at its opening event and corrected in
        // pass 4 — all inside the same transaction, leaving exactly the state the
        // application produces.
        foreach (var shift in openings)
        {
            var sessionId = AureliaLuxeDemoIds.AttendanceSession(shift.Staff.Key, shift.Date);
            var openingEventId = AureliaLuxeDemoIds.AttendanceEvent(shift.Staff.Key, shift.Date, "ClockIn");
            shift.SessionId = sessionId;

            db.AttendanceSessions.Add(new AttendanceSession
            {
                Id = sessionId,
                BusinessId = demo.BusinessId,
                StaffUserId = shift.Staff.User.Id,
                OpeningEventId = openingEventId,
                OpeningLocationId = location?.Id,
                OpenedAt = shift.OpenedAt,
                ClosedAt = shift.IsOpen ? null : shift.ClosedAt,
                ClosingEventId = shift.IsOpen ? null : openingEventId,
                Status = shift.IsOpen ? AttendanceSessionStatus.Open : AttendanceSessionStatus.Closed,
                CreatedAt = shift.OpenedAt,
            });
            sessions++;
        }

        db.SaveChanges();

        // Pass 3 — CLOCK_OUT events (closed shifts only).
        foreach (var shift in openings.Where(s => !s.IsOpen))
        {
            db.AttendanceEvents.Add(BuildEvent(demo, shift, "ClockOut", shift.ClosedAt, shift.SessionId, location, credential));
            events++;
        }

        db.SaveChanges();

        // Pass 4 — stamp sessions with their closing event.
        foreach (var shift in openings.Where(s => !s.IsOpen))
        {
            var session = db.AttendanceSessions.Local.FirstOrDefault(s => s.Id == shift.SessionId);
            if (session is null)
            {
                continue;
            }

            session.ClosingEventId = AureliaLuxeDemoIds.AttendanceEvent(shift.Staff.Key, shift.Date, "ClockOut");
            session.ClosedAt = shift.ClosedAt;
            session.ClosingLocationId = location?.Id;
            session.WorkedMinutes = (int)Math.Max(0, (shift.ClosedAt - shift.OpenedAt).TotalMinutes);
        }

        db.SaveChanges();

        return (sessions, events);
    }

    private static AttendanceEvent BuildEvent(
        AureliaLuxeDemoContext demo,
        ShiftAttendance shift,
        string kind,
        DateTime occurredAt,
        Guid? sessionId,
        AttendanceLocation? location,
        AttendanceQrCredential? credential)
    {
        var checkedAt = occurredAt.ToString("O", CultureInfo.InvariantCulture);

        return new AttendanceEvent
        {
            Id = AureliaLuxeDemoIds.AttendanceEvent(shift.Staff.Key, shift.Date, kind),
            BusinessId = demo.BusinessId,
            StaffUserId = shift.Staff.User.Id,
            EventType = kind == "ClockIn" ? AttendanceEventType.ClockIn : AttendanceEventType.ClockOut,
            OccurredAt = occurredAt,
            AttendanceLocationId = location?.Id,
            AttendanceQrCredentialId = credential?.Id,
            AttendanceSessionId = sessionId,
            Source = AttendanceEventSource.Standard,
            VerificationSummaryJson =
                $"{{\"method\":\"QR\",\"actorUserId\":\"{shift.Staff.User.Id}\","
                + $"\"credentialId\":\"{credential?.Id}\",\"locationId\":\"{location?.Id}\","
                + "\"verifiers\":[{\"type\":\"AUTHENTICATED_USER\",\"passed\":true,\"checkedAt\":\""
                + checkedAt + "\"},{\"type\":\"QR\",\"passed\":true,\"checkedAt\":\"" + checkedAt + "\"}]}",
            ClientIdempotencyKey = $"aurelia-{shift.Staff.Key}-{shift.Date:yyyy-MM-dd}-{kind.ToLowerInvariant()}",
            CreatedByUserId = shift.Staff.User.Id,
            RecordedAt = occurredAt,
            CreatedAt = occurredAt,
        };
    }

    private sealed class ShiftAttendance
    {
        public AureliaLuxeStaffHandle Staff { get; }
        public DateOnly Date { get; }
        public DateTime OpenedAt { get; }
        public DateTime ClosedAt { get; }
        public bool IsOpen { get; }
        public Guid? SessionId { get; set; }

        public ShiftAttendance(
            AureliaLuxeStaffHandle staff, DateOnly date, DateTime openedAt, DateTime closedAt, bool isOpen)
        {
            Staff = staff;
            Date = date;
            OpenedAt = openedAt;
            ClosedAt = closedAt;
            IsOpen = isOpen;
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  REVIEWS
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Reviews are written against real completed appointments (the platform
    /// requires an appointment id), mostly published with a few awaiting
    /// moderation.
    /// </summary>
    private static int WriteReviews(
        ApplicationDbContext db,
        AureliaLuxeDemoContext demo,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var completed = db.Appointments.Local
            .Where(a => a.BusinessId == demo.BusinessId && a.Status == "completed" && a.EndAt < nowUtc)
            .OrderBy(a => a.ScheduledAt)
            .ThenBy(a => a.Id)
            .ToList();

        var comments = new[]
        {
            "Absolutely loved my knotless braids — neat parts and no tension at all.",
            "The silk press lasted two weeks and my ends still look healthy.",
            "Warm welcome, spotless studio and my loc retwist was perfect.",
            "Best gel manicure in Westlands. Booking again next month.",
            "Great consultation — they explained exactly what my hair needed.",
            "The deep conditioning treatment rescued my hair after colouring.",
            "Bridal trial was flawless. My stylist understood the brief immediately.",
            "Professional team, easy M-PESA payment and finished right on time.",
            "Lovely space, but my appointment started a little late.",
            "Beautiful finish, though I expected a slightly longer scalp massage.",
        };

        var created = 0;

        for (var i = 0; i < completed.Count; i++)
        {
            var appointment = completed[i];
            var rng = new AureliaDemoRandom(9000 + i);

            if (!rng.Chance(0.34))
            {
                continue;
            }

            var roll = rng.NextDouble();
            var rating = roll switch
            {
                < 0.58 => 5,
                < 0.86 => 4,
                < 0.95 => 3,
                _ => 2,
            };

            var createdAt = appointment.EndAt.AddDays(rng.Next(1, 4));
            if (createdAt > nowUtc)
            {
                createdAt = nowUtc.AddHours(-6);
            }

            created++;
            db.Reviews.Add(new Review
            {
                Id = AureliaLuxeDemoIds.Review(i + 1),
                AppointmentId = appointment.Id,
                BusinessId = demo.BusinessId,
                CustomerId = appointment.CustomerId,
                StaffUserId = appointment.StaffUserId,
                Rating = rating,
                Comment = comments[(i * 3) % comments.Length],
                Status = rng.Chance(0.9) ? ReviewStatuses.Published : ReviewStatuses.Hidden,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            });
        }

        return created;
    }

    // ═══════════════════════════════════════════════════════════
    //  ENGAGEMENT: NOTIFICATIONS, SEGMENTS, INSIGHTS
    // ═══════════════════════════════════════════════════════════

    private sealed record EngagementResult(int Notifications, int NotificationLogs, int Segments, int Insights);

    private static async Task<EngagementResult> WriteEngagementAsync(
        ApplicationDbContext db,
        AureliaLuxeDemoContext demo,
        DateOnly today,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var notifications = WriteNotifications(db, demo, nowUtc);
        var logs = WriteNotificationLogs(db, demo, today, nowUtc);
        var segments = await WriteSegmentsAsync(db, demo, nowUtc, cancellationToken);
        var insights = WriteInsights(db, demo, nowUtc);

        await db.SaveChangesAsync(cancellationToken);

        return new EngagementResult(notifications, logs, segments, insights);
    }

    /// <summary>
    /// In-app notification inbox rows. These are internal records only — no
    /// email, SMS or push delivery is attempted for them.
    /// </summary>
    private static int WriteNotifications(ApplicationDbContext db, AureliaLuxeDemoContext demo, DateTime nowUtc)
    {
        var created = 0;
        var serviceStaff = demo.Staff.Where(s => s.Definition.ServiceKeys.Length > 0).ToList();

        // Staff goal + reward nudges over the last two weeks.
        for (var s = 0; s < serviceStaff.Count; s++)
        {
            var staff = serviceStaff[s];
            for (var d = 0; d < 6; d++)
            {
                var at = nowUtc.AddDays(-(d * 2)).AddHours(-(s + 1));
                created++;
                db.Notifications.Add(new Notification
                {
                    Id = AureliaLuxeDemoIds.Notification($"staff-goal:{staff.Key}:{d}"),
                    UserId = staff.User.Id,
                    BusinessId = demo.BusinessId,
                    Type = d % 3 == 0 ? "RewardReady" : "GoalReached",
                    StampsCount = AureliaLuxeDemoData.DefaultDailyStampGoal,
                    IsRead = d > 1,
                    PayloadJson = $"{{\"businessName\":\"{AureliaLuxeDemoData.Name}\",\"stamps\":{AureliaLuxeDemoData.DefaultDailyStampGoal}}}",
                    CreatedAt = at,
                });
            }
        }

        // Guest-facing lifecycle notifications for their own appointments.
        var upcoming = db.Appointments.Local
            .Where(a => a.BusinessId == demo.BusinessId && a.ScheduledAt > nowUtc)
            .OrderBy(a => a.ScheduledAt)
            .Take(40)
            .ToList();

        for (var i = 0; i < upcoming.Count; i++)
        {
            var appointment = upcoming[i];
            created++;
            db.Notifications.Add(new Notification
            {
                Id = AureliaLuxeDemoIds.Notification($"appt-confirmed:{appointment.Id:N}"),
                UserId = appointment.CustomerId,
                BusinessId = demo.BusinessId,
                Type = "AppointmentConfirmed",
                AppointmentId = appointment.Id,
                IsRead = i % 3 == 0,
                PayloadJson = $"{{\"businessName\":\"{AureliaLuxeDemoData.Name}\",\"scheduledAt\":\"{appointment.ScheduledAt:O}\"}}",
                CreatedAt = appointment.CreatedAt.AddDays(1),
            });

            if (i % 2 == 0)
            {
                created++;
                db.Notifications.Add(new Notification
                {
                    Id = AureliaLuxeDemoIds.Notification($"appt-reminder:{appointment.Id:N}"),
                    UserId = appointment.CustomerId,
                    BusinessId = demo.BusinessId,
                    Type = "AppointmentReminder",
                    AppointmentId = appointment.Id,
                    IsRead = false,
                    PayloadJson = $"{{\"businessName\":\"{AureliaLuxeDemoData.Name}\",\"scheduledAt\":\"{appointment.ScheduledAt:O}\"}}",
                    CreatedAt = nowUtc.AddHours(-6),
                });
            }
        }

        // Reward-ready nudges for guests whose card just completed.
        var entitlements = db.RewardEntitlements.Local
            .Where(e => e.BusinessId == demo.BusinessId && e.Status == RewardEntitlementStatus.Unlocked)
            .ToList();

        foreach (var entitlement in entitlements)
        {
            created++;
            db.Notifications.Add(new Notification
            {
                Id = AureliaLuxeDemoIds.Notification($"reward-ready:{entitlement.Id:N}"),
                UserId = entitlement.CustomerId,
                BusinessId = demo.BusinessId,
                Type = "RewardReady",
                StampsCount = entitlement.RequiredStamps,
                IsRead = false,
                PayloadJson = $"{{\"businessName\":\"{AureliaLuxeDemoData.Name}\",\"reward\":\"{entitlement.RewardName}\"}}",
                CreatedAt = entitlement.UnlockedAt,
            });
        }

        return created;
    }

    /// <summary>
    /// The notification ledger. Rows are written in terminal states
    /// (<c>sent</c> / <c>failed</c>) with past delivery timestamps, so the
    /// outbox worker's <c>status = 'pending'</c> claim can never pick one up and
    /// deliver it to a real address.
    /// </summary>
    private static int WriteNotificationLogs(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, DateOnly today, DateTime nowUtc)
    {
        var created = 0;
        var templates = new[] { "appointment.booked", "appointment.reminder", "appointment.cancelled", "loyalty.reward_ready" };

        var appointments = db.Appointments.Local
            .Where(a => a.BusinessId == demo.BusinessId)
            .OrderByDescending(a => a.ScheduledAt)
            .ThenBy(a => a.Id)
            .Take(180)
            .ToList();

        for (var i = 0; i < appointments.Count; i++)
        {
            var appointment = appointments[i];
            var rng = new AureliaDemoRandom(7000 + i);
            var channel = i % 3 == 0 ? "sms" : "email";
            var sentAt = appointment.Status == "completed"
                ? appointment.EndAt.AddHours(2)
                : appointment.ScheduledAt.AddDays(-1);
            if (sentAt > nowUtc)
            {
                sentAt = nowUtc.AddHours(-3);
            }

            var failed = rng.Chance(0.06);
            created++;

            db.NotificationLogs.Add(new NotificationLog
            {
                Id = AureliaLuxeDemoIds.NotificationLog($"appt:{appointment.Id:N}"),
                UserId = appointment.CustomerId,
                BusinessId = demo.BusinessId,
                Channel = channel,
                TemplateType = templates[i % templates.Length],
                Status = failed ? "failed" : "sent",
                SentAt = sentAt,
                DeliveredAt = failed ? null : sentAt.AddMinutes(2),
                OpenedAt = !failed && rng.Chance(0.45) ? sentAt.AddHours(1) : null,
                PayloadJson = $"{{\"appointmentId\":\"{appointment.Id}\",\"scheduledAt\":\"{appointment.ScheduledAt:O}\"}}",
                Attempts = 1,
                DeliveryAttemptsJson =
                    $"[{{\"channel\":\"{channel}\",\"attemptedAt\":\"{sentAt:O}\",\"outcome\":\"{(failed ? "failed" : "sent")}\"}}]",
                NextAttemptAt = sentAt,
                DeliverAfterUtc = sentAt,
                TemplateKey = $"platform|default|{templates[i % templates.Length]}",
                IdempotencyKey = $"aurelia-log:{appointment.Id:N}",
                Error = failed ? "Provider rejected the request (simulated demo record)." : null,
                UpdatedAt = sentAt,
                CreatedAt = sentAt,
            });
        }

        _ = today;
        return created;
    }

    /// <summary>
    /// Customer segmentation rows, computed from each guest's real loyalty card
    /// (recency, lifetime stamps and completed cycles) rather than invented.
    /// </summary>
    private static async Task<int> WriteSegmentsAsync(
        ApplicationDbContext db,
        AureliaLuxeDemoContext demo,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var cards = await db.LoyaltyCards
            .Where(c => c.BusinessId == demo.BusinessId)
            .Select(c => new { c.CustomerId, c.LifetimeStamps, c.TotalRedemptions, c.LastStampAt })
            .ToListAsync(cancellationToken);

        var created = 0;

        foreach (var card in cards)
        {
            var daysSinceLastStamp = card.LastStampAt is null
                ? 999
                : (int)(nowUtc - card.LastStampAt.Value).TotalDays;

            var segment = (card.LifetimeStamps, card.TotalRedemptions, daysSinceLastStamp) switch
            {
                (0, _, _) => "new",
                (_, >= 2, <= 30) => "vip",
                (_, _, <= 30) => "active",
                (_, _, <= 60) => "at_risk",
                _ => "lapsed",
            };

            var score = Math.Clamp(
                (card.LifetimeStamps * 3) + (card.TotalRedemptions * 8) + Math.Max(0, 40 - daysSinceLastStamp),
                0, 100);

            created++;
            db.CustomerSegments.Add(new CustomerSegment
            {
                BusinessId = demo.BusinessId,
                CustomerId = card.CustomerId,
                Segment = segment,
                Score = score,
                LastStampAt = card.LastStampAt,
                ComputedAt = nowUtc,
            });
        }

        return created;
    }

    /// <summary>Business insights surfaced on the owner dashboard.</summary>
    private static int WriteInsights(ApplicationDbContext db, AureliaLuxeDemoContext demo, DateTime nowUtc)
    {
        var rows = new (string Key, string Category, string Metric, string Severity, string Confidence,
            string Title, string Message, string Recommendation)[]
        {
            ("retention", "Retention", "repeat_rate", "MEDIUM", "HIGH",
                "Repeat visits are holding above target",
                "More than half of the last 90 days' appointments came from returning guests.",
                "Keep the 8-stamp Aurelia Gold card as the anchor offer and nudge guests one stamp from completion."),

            ("lapsed", "Retention", "lapsed_customers", "HIGH", "MEDIUM",
                "A cluster of guests has not visited in 60+ days",
                "Several loyalty cards have not recorded a stamp for over two months.",
                "Run the win-back automation on the lapsed segment with the weekday cornrow special."),

            ("braiding", "Revenue", "top_service", "LOW", "HIGH",
                "Braiding carries the highest ticket value",
                "Knotless and goddess braids are the top revenue services at the atelier.",
                "Protect braiding chair capacity at weekends and consider a midweek incentive."),

            ("outstanding", "Payments", "outstanding_balance", "MEDIUM", "HIGH",
                "Outstanding balances need follow-up",
                "A number of completed appointments still have no successful payment recorded.",
                "Enable the failed-payment follow-up automation and reconcile cash at close of day."),

            ("reviews", "Experience", "review_average", "LOW", "MEDIUM",
                "Guest sentiment is strong",
                "Published reviews average well above four stars.",
                "Showcase the best reviews on the storefront and ask for a review after every fifth visit."),

            ("attendance", "Operations", "late_arrivals", "LOW", "MEDIUM",
                "A small number of late clock-ins",
                "A handful of shifts started later than the rostered time this month.",
                "Review the rota against the first appointment of the day for the affected specialists."),
        };

        foreach (var row in rows)
        {
            db.Insights.Add(new Insight
            {
                Id = AureliaLuxeDemoIds.Insight(row.Key),
                Audience = "business",
                BusinessId = demo.BusinessId,
                Category = row.Category,
                Metric = row.Metric,
                Severity = row.Severity,
                Confidence = row.Confidence,
                Title = row.Title,
                Message = row.Message,
                Recommendation = row.Recommendation,
                DataJson = "{\"source\":\"demo-seed\"}",
                GeneratedAt = nowUtc.AddHours(-2),
                ExpiresAt = nowUtc.AddDays(14),
                CreatedAt = nowUtc.AddHours(-2),
            });
        }

        return rows.Length;
    }
}
