using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Data.Seeding.Demo.AureliaLuxe;

namespace PunchedApi.Tests;

/// <summary>
/// The guard rails on the demo/test appointment purge.
///
/// The command exists because a demo seeder and a verification script had been writing
/// appointments into a real tenant, and a business-wide delete had already shown how
/// destructive a "refresh" can be. So the tests pin both halves of the contract: the
/// rows it identifies (seeder id, verification marker, reserved demo identity) are
/// removed with their dependents, and a human-created appointment — with its history,
/// resource line, payment and reminder job — survives untouched. A dry run must delete
/// nothing at all.
/// </summary>
public class TestAppointmentCleanupTests
{
    private sealed record Env(
        ApplicationDbContext Ctx,
        Business Business,
        Appointment Keep,
        Appointment Marked,
        Appointment DemoIdentity,
        Payment KeepPayment,
        Payment MarkedPayment,
        ScheduledJob KeepJob,
        ScheduledJob MarkedJob);

    private static User AddUser(ApplicationDbContext ctx, string email, string fullName)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = fullName,
            Role = UserRole.Customer,
            CreatedAt = DateTime.UtcNow,
            // Users are keyed to their credential row by email; a user without one
            // cannot be persisted (the relationship is required).
            Auth = new UserAuth
            {
                Id = Guid.NewGuid(),
                Email = email,
                PasswordHash = "x",
                IsVerified = true,
                CreatedAt = DateTime.UtcNow,
            },
        };
        ctx.Users.Add(user);
        return user;
    }

    private static Appointment AddAppointment(
        ApplicationDbContext ctx,
        Guid businessId,
        Guid customerId,
        string? note,
        DateTime scheduledAt,
        Guid? id = null)
    {
        var appointment = new Appointment
        {
            Id = id ?? Guid.NewGuid(),
            BusinessId = businessId,
            CustomerId = customerId,
            ScheduledAt = scheduledAt,
            EndAt = scheduledAt.AddHours(1),
            Status = "confirmed",
            Note = note,
            CreatedAt = DateTime.UtcNow.AddDays(-2),
        };

        appointment.Resources.Add(new AppointmentResource
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            Name = "Wash & Blow Dry",
            DurationMinutes = 60,
            Price = 2500m,
            SortOrder = 0,
            CreatedAt = appointment.CreatedAt,
        });

        ctx.Appointments.Add(appointment);
        ctx.AppointmentStatusHistory.Add(new AppointmentStatusHistory
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            Status = "booked",
            ChangedAt = appointment.CreatedAt,
            Note = note,
            CreatedAt = appointment.CreatedAt,
        });

        return appointment;
    }

    private static Payment AddPayment(ApplicationDbContext ctx, Guid businessId, Appointment appointment) =>
        new()
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CustomerId = appointment.CustomerId,
            AppointmentId = appointment.Id,
            Type = PaymentType.FullPayment,
            Method = PaymentMethod.Cash,
            Provider = PaymentProviderKind.Cash,
            Status = PaymentStatus.Success,
            Amount = 2500m,
            Currency = "KES",
            Reference = $"PMT-{Guid.NewGuid():N}"[..16].ToUpperInvariant(),
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        };

    private static Env CreateEnv()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(conn).Options;
        var ctx = new ApplicationDbContext(options);
        ctx.Database.EnsureCreated();

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = "Aurelia Luxe Hair Atelier",
            Slug = "aurelia-luxe-hair-atelier",
            Category = "Salon",
            Location = "Nairobi",
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Businesses.Add(business);

        // A real customer: never a reserved demo domain.
        var realCustomer = AddUser(ctx, "jane.wanjiku@gmail.com", "Jane Wanjiku");
        var demoCustomer = AddUser(ctx, "diana.achieng.b101@demo.punched.app", "Diana Achieng");

        var start = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

        var keep = AddAppointment(ctx, business.Id, realCustomer.Id, null, start);
        var marked = AddAppointment(
            ctx, business.Id, realCustomer.Id, "Seeded verification booking.", start.AddDays(1));
        var demoIdentity = AddAppointment(ctx, business.Id, demoCustomer.Id, null, start.AddDays(2));
        var seeded = AddAppointment(
            ctx,
            business.Id,
            realCustomer.Id,
            null,
            start.AddDays(3),
            id: AureliaLuxeDemoIds.Appointment(1));

        var keepPayment = AddPayment(ctx, business.Id, keep);
        var markedPayment = AddPayment(ctx, business.Id, marked);
        ctx.Payments.AddRange(keepPayment, markedPayment, AddPayment(ctx, business.Id, seeded));

        var keepJob = new ScheduledJob
        {
            Id = Guid.NewGuid(),
            JobType = "appointment.reminder",
            DedupeKey = $"appt:{keep.Id}:r-1440m",
            BusinessId = business.Id,
            AggregateType = "appointment",
            AggregateId = keep.Id,
            PayloadJson = "{}",
            ScheduledForUtc = start.AddDays(-1),
            Status = "pending",
            UpdatedAt = DateTime.UtcNow,
        };
        var markedJob = new ScheduledJob
        {
            Id = Guid.NewGuid(),
            JobType = "appointment.reminder",
            DedupeKey = $"appt:{marked.Id}:r-1440m",
            BusinessId = business.Id,
            AggregateType = "appointment",
            AggregateId = marked.Id,
            PayloadJson = "{}",
            ScheduledForUtc = start,
            Status = "pending",
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.ScheduledJobs.AddRange(keepJob, markedJob);

        ctx.SaveChanges();

        return new Env(ctx, business, keep, marked, demoIdentity, keepPayment, markedPayment, keepJob, markedJob);
    }

    private static TestAppointmentCleanupService Service(ApplicationDbContext ctx) =>
        new(ctx, NullLogger<TestAppointmentCleanupService>.Instance);

    [Fact]
    public async Task DryRun_classifies_every_origin_and_deletes_nothing()
    {
        var env = CreateEnv();

        var report = await Service(env.Ctx).ExecuteAsync(
            new TestAppointmentCleanupRequest(env.Business.Id, Apply: false));

        Assert.False(report.Applied);
        Assert.Equal(4, report.TotalAppointments);
        Assert.Equal(1, report.SeededAppointments);
        Assert.Equal(1, report.VerificationAppointments);
        Assert.Equal(1, report.SyntheticIdentityAppointments);
        Assert.Equal(1, report.RetainedAppointments);
        Assert.Empty(report.RemovedRows);

        // Nothing was touched.
        Assert.Equal(4, await env.Ctx.Appointments.CountAsync());
        Assert.Equal(3, await env.Ctx.Payments.CountAsync());
        Assert.Equal(2, await env.Ctx.ScheduledJobs.CountAsync());
        Assert.Contains(report.Warnings, warning => warning.Contains("Dry run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_removes_demo_rows_and_their_dependents_but_keeps_a_real_booking()
    {
        var env = CreateEnv();

        var report = await Service(env.Ctx).ExecuteAsync(
            new TestAppointmentCleanupRequest(env.Business.Id, Apply: true));

        Assert.True(report.Applied);

        // The only appointment left is the human-created one.
        var remaining = await env.Ctx.Appointments.ToListAsync();
        Assert.Equal([env.Keep.Id], remaining.Select(a => a.Id));

        // Its dependents survive: resource line, history, payment and reminder job.
        Assert.True(await env.Ctx.AppointmentResources.AnyAsync(r => r.AppointmentId == env.Keep.Id));
        Assert.True(await env.Ctx.AppointmentStatusHistory.AnyAsync(h => h.AppointmentId == env.Keep.Id));
        Assert.True(await env.Ctx.Payments.AnyAsync(p => p.Id == env.KeepPayment.Id));
        Assert.True(await env.Ctx.ScheduledJobs.AnyAsync(j => j.Id == env.KeepJob.Id));

        // The demo rows' dependents are gone, including rows that would otherwise
        // reference a deleted appointment.
        Assert.False(await env.Ctx.Payments.AnyAsync(p => p.Id == env.MarkedPayment.Id));
        Assert.False(await env.Ctx.ScheduledJobs.AnyAsync(j => j.Id == env.MarkedJob.Id));
        Assert.False(await env.Ctx.AppointmentResources.AnyAsync(r => r.AppointmentId == env.Marked.Id));
        Assert.Equal(0, await env.Ctx.AppointmentStatusHistory.CountAsync(h => h.AppointmentId != env.Keep.Id));

        Assert.Equal(3, report.RemovedRows["appointments"]);
        Assert.Equal(2, report.RemovedRows["payments"]);

        // A second run finds nothing left to remove: the command is repeatable.
        var again = await Service(env.Ctx).ExecuteAsync(
            new TestAppointmentCleanupRequest(env.Business.Id, Apply: true));
        Assert.Equal(1, again.TotalAppointments);
        Assert.Equal(1, again.RetainedAppointments);
        Assert.Empty(again.RemovedRows);
    }

    [Fact]
    public async Task Another_business_book_is_never_touched()
    {
        var env = CreateEnv();

        var otherBusiness = new Business
        {
            Id = Guid.NewGuid(),
            Name = "Other Tenant",
            Slug = "other-tenant",
            Category = "Salon",
            Location = "Mombasa",
            CreatedAt = DateTime.UtcNow,
        };
        env.Ctx.Businesses.Add(otherBusiness);

        var otherCustomer = AddUser(env.Ctx, "other.customer@gmail.com", "Other Customer");
        var otherAppointment = AddAppointment(
            env.Ctx,
            otherBusiness.Id,
            otherCustomer.Id,
            "Seeded verification booking.",
            new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc),
            id: AureliaLuxeDemoIds.Appointment(2));
        await env.Ctx.SaveChangesAsync();

        await Service(env.Ctx).ExecuteAsync(
            new TestAppointmentCleanupRequest(env.Business.Id, Apply: true));

        Assert.True(await env.Ctx.Appointments.AnyAsync(a => a.Id == otherAppointment.Id));
        Assert.True(await env.Ctx.Appointments.AnyAsync(a => a.Id == env.Keep.Id));
    }
}
