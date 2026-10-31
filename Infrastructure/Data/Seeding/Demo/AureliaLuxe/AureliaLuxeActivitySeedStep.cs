using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Seeding.Demo.AureliaLuxe;

/// <summary>
/// Generates the atelier's operating activity: loyalty ledger, appointments,
/// payments, attendance, reviews and engagement records.
///
/// The step is <b>refreshing</b> rather than purely additive: before writing it
/// deletes only the rows it previously created (identified by deterministic
/// <see cref="AureliaLuxeDemoIds"/> identifiers) for this tenant. Rows created
/// by a human through the app have different identifiers and are never touched,
/// which is what makes "re-run the seeder" a safe way to refresh the demo.
/// </summary>
public sealed class AureliaLuxeActivitySeedStep : ISeedStep
{
    public string Name => "AureliaLuxeActivity";

    public async Task ExecuteAsync(SeedExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(context, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg)
        {
            // Postgres redacts the message unless the connection string opts into
            // error detail; the structured fields are always available and make a
            // failed demo seed diagnosable.
            context.Logger.LogError(
                "Aurelia demo activity failed: SQLSTATE={SqlState} constraint={Constraint} table={Table} column={Column} detail={Detail}",
                pg.SqlState, pg.ConstraintName, pg.TableName, pg.ColumnName, pg.Detail);
            throw;
        }
    }

    private static async Task RunAsync(SeedExecutionContext context, CancellationToken cancellationToken)
    {
        var db = context.Db;

        // The whole seed run shares one DbContext, and the standard steps leave
        // their entities tracked. The refresh below deletes rows at the database
        // level (ExecuteDelete does not touch the change tracker), so a stale
        // tracked instance would collide with the row this step re-creates.
        // Starting from a clean tracker makes the step independent of whatever
        // ran before it.
        db.ChangeTracker.Clear();

        var business = await AureliaLuxeDemoResolver.ResolveBusinessAsync(db, cancellationToken);
        if (business is null)
        {
            context.Report.Warnings.Add("AureliaLuxeActivity skipped: demo business not found.");
            return;
        }

        var demo = await AureliaLuxeDemoResolver.ResolveContextAsync(context, business, cancellationToken);
        if (demo is null)
        {
            context.Report.Warnings.Add("AureliaLuxeActivity skipped: identities not resolved.");
            return;
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        counts["RowsRemoved"] = await RefreshDemoActivityAsync(db, demo, cancellationToken);

        // Referrals are generated first: their reward stamps are part of the
        // loyalty ledger, so the ledger must be able to see them.
        var referrals = await AureliaLuxeReferralSeeder.SeedAsync(db, demo, cancellationToken);
        counts["ReferralLinksCreated"] = referrals.Links;
        counts["ReferralsCreated"] = referrals.Referrals;
        counts["ReferralRewardStamps"] = referrals.RewardStamps;

        var loyalty = await AureliaLuxeLoyaltySeeder.SeedAsync(db, demo, cancellationToken);
        counts["LoyaltyCardsUpdated"] = loyalty.Cards;
        counts["StampsCreated"] = loyalty.Stamps;
        counts["QrTokensCreated"] = loyalty.QrTokens;
        counts["StampTransactionsCreated"] = loyalty.Transactions;
        counts["StampAdjustmentsCreated"] = loyalty.Adjustments;
        counts["RewardEntitlementsCreated"] = loyalty.Entitlements;
        counts["RedemptionsCreated"] = loyalty.Redemptions;

        var scheduling = await AureliaLuxeAppointmentSeeder.SeedAsync(db, demo, cancellationToken);
        counts["AppointmentsCreated"] = scheduling.Appointments;
        counts["AppointmentResourcesCreated"] = scheduling.Resources;
        counts["AppointmentStatusHistoryCreated"] = scheduling.StatusHistory;
        counts["RescheduleRequestsCreated"] = scheduling.RescheduleRequests;
        counts["PaymentsCreated"] = scheduling.Payments;
        counts["PaymentAttemptsCreated"] = scheduling.PaymentAttempts;

        var operations = await AureliaLuxeOperationsSeeder.SeedAsync(db, demo, cancellationToken);
        counts["AttendanceSessionsCreated"] = operations.AttendanceSessions;
        counts["AttendanceEventsCreated"] = operations.AttendanceEvents;
        counts["ReviewsCreated"] = operations.Reviews;
        counts["NotificationsCreated"] = operations.Notifications;
        counts["NotificationLogsCreated"] = operations.NotificationLogs;
        counts["CustomerSegmentsCreated"] = operations.Segments;
        counts["InsightsCreated"] = operations.Insights;

        foreach (var (key, value) in counts)
        {
            context.Report.Counts[$"Aurelia_{key}"] = value;
        }

        context.Logger.LogInformation(
            "Aurelia demo activity refreshed: {Appointments} appointments, {Payments} payments, {Stamps} stamps, {Sessions} attendance sessions.",
            scheduling.Appointments, scheduling.Payments, loyalty.Stamps, operations.AttendanceSessions);
    }

    /// <summary>
    /// Removes the rows this seeder previously wrote for the tenant. Every
    /// predicate is scoped to the atelier's business id <i>and</i> to the
    /// seeder's own records, so no other tenant and no human-created record is
    /// affected.
    /// </summary>
    private static async Task<int> RefreshDemoActivityAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var businessId = demo.BusinessId;
        var removed = 0;

        removed += await db.Reviews
            .Where(r => r.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        removed += await db.RescheduleRequests
            .Where(r => db.Appointments.Any(a => a.Id == r.AppointmentId && a.BusinessId == businessId))
            .ExecuteDeleteAsync(cancellationToken);

        removed += await db.Payments
            .Where(p => p.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        removed += await db.Appointments
            .Where(a => a.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        // Attendance has a deliberate FK cycle (sessions ↔ events) and the
        // ledger refuses a NULL session on a ClockOut row, so the references are
        // released in an order that keeps every check constraint satisfied at
        // each step: retype the ClockOut rows (which also frees their session
        // link), detach the remaining events from their sessions, delete the
        // sessions, then delete the events.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE attendance_events SET event_type = 'ManualAdjustment', attendance_session_id = NULL "
            + "WHERE business_id = {0} AND event_type = 'ClockOut';",
            [businessId], cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            "UPDATE attendance_events SET attendance_session_id = NULL WHERE business_id = {0};",
            [businessId], cancellationToken);

        removed += await db.AttendanceSessions
            .Where(s => s.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        removed += await db.AttendanceEvents
            .Where(e => e.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        removed += await db.StampAdjustments
            .Where(a => db.LoyaltyCards.Any(c => c.Id == a.CardId && c.BusinessId == businessId))
            .ExecuteDeleteAsync(cancellationToken);

        removed += await db.RewardEntitlements
            .Where(e => e.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        removed += await db.Redemptions
            .Where(r => r.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        removed += await db.StampTransactions
            .Where(t => t.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        removed += await db.Stamps
            .Where(s => db.LoyaltyCards.Any(c => c.Id == s.CardId && c.BusinessId == businessId))
            .ExecuteDeleteAsync(cancellationToken);

        removed += await db.QrTokens
            .Where(q => q.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        removed += await db.Referrals
            .Where(r => r.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        removed += await db.ReferralLinks
            .Where(l => l.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        removed += await db.Notifications
            .Where(n => n.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        removed += await db.NotificationLogs
            .Where(n => n.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        removed += await db.CustomerSegments
            .Where(s => s.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        removed += await db.Insights
            .Where(i => i.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        // Durable jobs/events for the tenant are cleared so no reminder or
        // win-back job can fire from a previous demo run.
        removed += await db.ScheduledJobs
            .Where(j => j.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        removed += await db.DomainEvents
            .Where(e => e.BusinessId == businessId)
            .ExecuteDeleteAsync(cancellationToken);

        return removed;
    }
}
