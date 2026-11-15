using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.Services;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;
using PunchedApi.Infrastructure.Services.Payments;

namespace PunchedApi.Tests;

/// <summary>
/// How a payment is traced back to the service that earned it, and what the payments module
/// counts as money received.
///
/// <para><b>The relationship, in one line.</b> A payment points at its APPOINTMENT
/// (<see cref="Payment.AppointmentId"/>); the appointment carries an immutable snapshot of each
/// service it was booked for (name, price, duration at the time of booking); and every settled
/// payment writes a ledger entry that carries the payment id it came from. There is deliberately
/// no payment → service foreign key: a payment is for an appointment, not for a catalogue row,
/// and a direct key would break the moment a business renamed, re-priced or deleted a service.</para>
///
/// <para>These tests pin both halves: the trace works, and it survives a rename — which is the
/// only honest way to attribute historical revenue.</para>
/// </summary>
public class PaymentServiceAttributionTests
{
    private sealed class Env : IDisposable
    {
        /// <summary>Null when the env runs on the in-memory provider, which needs no connection.</summary>
        public Microsoft.Data.Sqlite.SqliteConnection? Connection = null!;
        public ApplicationDbContext Context = null!;
        public PaymentService Payments = null!;
        public Business Business = null!;
        public User Owner = null!;
        public User Customer = null!;
        public User Staff = null!;
        public ServiceCatalogItem Service = null!;

        public void Dispose()
        {
            Context.Dispose();
            Connection?.Dispose();
        }
    }

    private static PaymentCredentialProtector Protector()
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Development");
        return new PaymentCredentialProtector(Options.Create(new PaymentOptions()), env.Object);
    }

    private static PaymentService PaymentServiceFor(ApplicationDbContext context)
        => new(
            context,
            new UnitOfWork(context),
            new IPaymentProvider[] { new CashPaymentProvider(NullLogger<CashPaymentProvider>.Instance) },
            new PermissionService(),
            Protector(),
            Options.Create(new PaymentOptions()),
            BookingTestBase.CreateNotificationService(context),
            NullLogger<PaymentService>.Instance);

    private static async Task<Env> CreateEnvAsync(DateTime scheduledAt, bool inMemory = false)
    {
        Microsoft.Data.Sqlite.SqliteConnection? connection = null;
        ApplicationDbContext context;
        if (inMemory)
        {
            // The payments dashboard aggregates decimals, which the SQLite provider cannot SUM.
            // Those three assertions are about the service's own rule (which status counts as
            // received), not about SQL arithmetic, so they run on the in-memory provider — the
            // same choice ModuleEnforcementIntegrationTests makes for provider-agnostic logic.
            context = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options);
        }
        else
        {
            connection = BookingTestBase.CreateConnection();
            context = BookingTestBase.CreateContext(connection);
        }

        var owner = BookingTestBase.CreateOwner("attribution-owner@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Attribution Studio");
        var customer = BookingTestBase.CreateCustomer("attribution-customer@test.com");
        var staff = BookingTestBase.CreateStaff(business.Id, "attribution-staff@test.com");
        var service = BookingTestBase.CreateService(business.Id, "Balayage", 120, 4500m);

        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            StaffUserId = staff.Id,
            ScheduledAt = scheduledAt,
            EndAt = scheduledAt.AddMinutes(120),
            Status = AppointmentStatuses.Confirmed,
            CreatedAt = DateTime.UtcNow,
        };
        // The snapshot the booking wrote: what the customer actually bought, at the price it
        // was sold for. This is what attribution reads — not the catalogue row, which can move.
        appointment.Resources.Add(new AppointmentResource
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            ServiceCatalogItemId = service.Id,
            Name = "Balayage",
            Price = 4500m,
            DurationMinutes = 120,
        });

        context.Users.AddRange(owner, customer, staff);
        context.Businesses.Add(business);
        context.ServiceCatalogItems.Add(service);
        context.Appointments.Add(appointment);
        await context.SaveChangesAsync();

        return new Env
        {
            Connection = connection,
            Context = context,
            Payments = PaymentServiceFor(context),
            Business = business,
            Owner = owner,
            Customer = customer,
            Staff = staff,
            Service = service,
        };
    }

    private static async Task<Payment> OutstandingAsync(Env env, decimal amount)
    {
        var appointmentId = await env.Context.Appointments.Select(a => a.Id).SingleAsync();
        var payment = PaymentOutstanding.Create(
            env.Business.Id, env.Customer.Id, appointmentId, amount, env.Owner.Id, DateTime.UtcNow);
        env.Context.Payments.Add(payment);
        await env.Context.SaveChangesAsync();
        return payment;
    }

    // ── Attribution ─────────────────────────────────────────

    [Fact]
    public async Task APaymentIsTraceableToItsAppointmentAndThroughItToTheService()
    {
        using var env = await CreateEnvAsync(DateTime.UtcNow.AddDays(1));
        var payment = await OutstandingAsync(env, 4500m);

        env.Context.ChangeTracker.Clear();

        // 1. The payment names its appointment.
        var stored = await env.Context.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.NotNull(stored.AppointmentId);

        // 2. The appointment carries the snapshot of what was bought.
        var resource = await env.Context.AppointmentResources.AsNoTracking()
            .SingleAsync(r => r.AppointmentId == stored.AppointmentId);
        Assert.Equal(env.Service.Id, resource.ServiceCatalogItemId);
        Assert.Equal("Balayage", resource.Name);
        Assert.Equal(4500m, resource.Price);

        // 3. So "which service earned this?" is answered for the business's own reports:
        //    payment → appointment → snapshot → service.
        var attributed = await (
            from p in env.Context.Payments.AsNoTracking()
            where p.Id == payment.Id
            join a in env.Context.Appointments.AsNoTracking() on p.AppointmentId equals a.Id
            join r in env.Context.AppointmentResources.AsNoTracking() on a.Id equals r.AppointmentId
            select r.Name).SingleAsync();

        Assert.Equal("Balayage", attributed);
    }

    [Fact]
    public async Task AttributionSurvivesTheServiceBeingRenamedAndArchived()
    {
        using var env = await CreateEnvAsync(DateTime.UtcNow.AddDays(1));
        var payment = await OutstandingAsync(env, 4500m);

        // The business renames and re-prices the service, then retires it. None of that may
        // rewrite yesterday's revenue: the appointment's snapshot is what was sold.
        env.Service.Name = "Balayage (signature)";
        env.Service.Price = 6000m;
        env.Service.IsActive = false;
        await env.Context.SaveChangesAsync();
        env.Context.ChangeTracker.Clear();

        var resource = await env.Context.AppointmentResources.AsNoTracking()
            .SingleAsync(r => r.AppointmentId == payment.AppointmentId);
        Assert.Equal("Balayage", resource.Name);
        Assert.Equal(4500m, resource.Price);

        // And the payment is untouched by the catalogue change.
        var stored = await env.Context.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(4500m, stored.Amount);
    }

    [Fact]
    public async Task AttributionSurvivesTheServiceBeingDeleted()
    {
        using var env = await CreateEnvAsync(DateTime.UtcNow.AddDays(1));
        var payment = await OutstandingAsync(env, 4500m);

        // A deleted catalogue row is the edge case a payment → service foreign key could not
        // express at all: the sale must stay readable with the catalogue gone.
        env.Context.ServiceCatalogItems.Remove(env.Service);
        await env.Context.SaveChangesAsync();
        env.Context.ChangeTracker.Clear();

        var resource = await env.Context.AppointmentResources.AsNoTracking()
            .SingleAsync(r => r.AppointmentId == payment.AppointmentId);
        Assert.Equal("Balayage", resource.Name);
        Assert.Equal(env.Service.Id, resource.ServiceCatalogItemId);
    }

    // ── What the payments module counts ─────────────────────

    [Fact]
    public async Task AnOutstandingBalanceIsNotCountedAsMoneyReceived()
    {
        using var env = await CreateEnvAsync(DateTime.UtcNow.AddDays(1), inMemory: true);
        await OutstandingAsync(env, 4500m);
        env.Context.ChangeTracker.Clear();

        var dashboard = await env.Payments.GetDashboardAsync(env.Owner.Id, "Business");

        Assert.True(dashboard.Success, dashboard.Error?.Message);
        // Nothing has been received: the appointment is booked and the money is owed.
        Assert.Equal(0m, dashboard.Data!.TodayCollected);
        Assert.Equal(0m, dashboard.Data.MonthCollected);
        Assert.Equal(0m, dashboard.Data.CashCollectedToday);
        Assert.Equal(0m, dashboard.Data.MpesaCollectedToday);
        Assert.Equal(0, dashboard.Data.TodayCount);
    }

    [Fact]
    public async Task ConfirmingTheOutstandingBalanceCountsItAsCashReceived()
    {
        using var env = await CreateEnvAsync(DateTime.UtcNow.AddDays(1), inMemory: true);
        var outstanding = await OutstandingAsync(env, 4500m);

        var confirmed = await env.Payments.ConfirmCashAsync(
            outstanding.Id, "Taken at the desk", env.Owner.Id, "Business");
        Assert.True(confirmed.Success, confirmed.Error?.Message);

        env.Context.ChangeTracker.Clear();
        var dashboard = await env.Payments.GetDashboardAsync(env.Owner.Id, "Business");

        Assert.Equal(4500m, dashboard.Data!.TodayCollected);
        Assert.Equal(4500m, dashboard.Data.CashCollectedToday);
        Assert.Equal(0m, dashboard.Data.MpesaCollectedToday);
        Assert.Equal(1, dashboard.Data.TodayCount);
    }

    [Fact]
    public async Task APartiallyCollectedBalanceCountsOnlyWhatWasReceived()
    {
        using var env = await CreateEnvAsync(DateTime.UtcNow.AddDays(1), inMemory: true);
        var outstanding = await OutstandingAsync(env, 4500m);

        var partial = await env.Payments.ConfirmCashAsync(
            outstanding.Id, "Part payment", env.Owner.Id, "Business", amount: 1500m);
        Assert.True(partial.Success, partial.Error?.Message);

        env.Context.ChangeTracker.Clear();
        var dashboard = await env.Payments.GetDashboardAsync(env.Owner.Id, "Business");

        // Received: the 1,500 taken at the desk. Outstanding: the 3,000 still owed, which
        // stays a created payment and is therefore not revenue.
        Assert.Equal(1500m, dashboard.Data!.TodayCollected);
        var rows = await env.Context.Payments.AsNoTracking().OrderBy(p => p.Amount).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1500m, rows[0].Amount);
        Assert.Equal(PaymentStatus.Success, rows[0].Status);
        Assert.Equal(3000m, rows[1].Amount);
        Assert.Equal(PaymentStatus.Created, rows[1].Status);
    }
}
