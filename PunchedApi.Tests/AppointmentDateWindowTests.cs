using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// The UTC boundary contract the appointments list filters on, and the tenant wall
/// around it.
///
/// <para>The dashboard is asked for an inclusive <c>from</c>..<c>to</c> window of
/// instants. The client resolves those instants from the BUSINESS's own calendar
/// (business-local midnight to business-local midnight), so the API must include an
/// appointment exactly on either boundary and exclude one a millisecond outside — no
/// duplicated or missing rows at midnight.</para>
///
/// <para>Every filter — including the payment-verification one, which reads the
/// ledger — stays inside the caller's own business.</para>
/// </summary>
public class AppointmentDateWindowTests
{
    private sealed class Env
    {
        public User Owner = null!;
        public Business Business = null!;
        public User Customer = null!;
        public ServiceCatalogItem Service = null!;
    }

    private static Payment Pay(Guid businessId, Guid customerId, Guid appointmentId, PaymentStatus status, decimal amount) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        CustomerId = customerId,
        AppointmentId = appointmentId,
        Type = PaymentType.FullPayment,
        Method = PaymentMethod.Mpesa,
        Provider = PaymentProviderKind.DarajaStk,
        Status = status,
        Amount = amount,
        Currency = "KES",
        Reference = PaymentReferences.New(),
        CreatedAt = DateTime.UtcNow
    };

    private sealed class Holder : IDisposable
    {
        public Microsoft.Data.Sqlite.SqliteConnection Connection = null!;
        public ApplicationDbContext Context = null!;

        public static Holder Create()
        {
            var connection = BookingTestBase.CreateConnection();
            return new Holder { Connection = connection, Context = BookingTestBase.CreateContext(connection) };
        }

        public void Dispose()
        {
            Context.Dispose();
            Connection.Dispose();
        }
    }

    /// <summary>One business with three appointments inside the window and one outside.</summary>
    private static async Task<Env> SeedWindowAsync(ApplicationDbContext context)
    {
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var customer = BookingTestBase.CreateCustomer();
        var service = BookingTestBase.CreateService(business.Id, "Cut", 60, 500m);

        var from = new DateTime(2026, 5, 11, 21, 0, 0, DateTimeKind.Utc); // 12 May 00:00 in Nairobi
        var to = new DateTime(2026, 5, 12, 20, 59, 59, 999, DateTimeKind.Utc); // 12 May 23:59:59.999

        var atStart = BookingTestBase.CreateAppointment(business.Id, customer.Id, null, from, from.AddMinutes(30));
        var inside = BookingTestBase.CreateAppointment(business.Id, customer.Id, null, from.AddHours(9), from.AddHours(10));
        var atEnd = BookingTestBase.CreateAppointment(business.Id, customer.Id, null, to, to);
        var afterEnd = BookingTestBase.CreateAppointment(business.Id, customer.Id, null, to.AddMilliseconds(1), to.AddMinutes(30));

        await BookingTestBase.SeedAsync(context, owner, business, customer, service,
            atStart, inside, atEnd, afterEnd);
        return new Env { Owner = owner, Business = business, Customer = customer, Service = service };
    }

    [Fact]
    public async Task DateWindow_includes_both_boundaries_and_excludes_the_instant_after()
    {
        using var holder = Holder.Create();
        var env = await SeedWindowAsync(holder.Context);
        var service = BookingTestBase.CreateAppointmentService(holder.Context);

        var from = new DateTime(2026, 5, 11, 21, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 5, 12, 20, 59, 59, 999, DateTimeKind.Utc);

        var result = await service.GetAppointmentDashboardAsync(
            env.Owner.Id, from, to, null, null, null, null, null, "scheduledAt", "asc", 1, 50);

        Assert.True(result.Success, result.Error?.Message);
        // The appointment at `from` and the one at `to` are both in; the one a
        // millisecond past `to` is not.
        Assert.Equal(3, result.Data!.Meta.TotalCount);
    }

    [Fact]
    public async Task DateWindow_splits_midnight_without_duplicating_or_dropping()
    {
        using var holder = Holder.Create();
        var env = await SeedWindowAsync(holder.Context);
        var service = BookingTestBase.CreateAppointmentService(holder.Context);

        // The Nairobi day 12 May, as UTC instants.
        var day = new DateTime(2026, 5, 11, 21, 0, 0, DateTimeKind.Utc);
        var nextDay = new DateTime(2026, 5, 12, 21, 0, 0, DateTimeKind.Utc);

        var today = await service.GetAppointmentDashboardAsync(
            env.Owner.Id, day, nextDay.AddMilliseconds(-1), null, null, null, null, null, "scheduledAt", "asc", 1, 50);
        var tomorrow = await service.GetAppointmentDashboardAsync(
            env.Owner.Id, nextDay, nextDay.AddDays(1).AddMilliseconds(-1), null, null, null, null, null, "scheduledAt", "asc", 1, 50);

        Assert.True(today.Success, today.Error?.Message);
        Assert.True(tomorrow.Success, tomorrow.Error?.Message);
        // Nothing appears in both days, and nothing falls between them.
        var todayIds = today.Data!.Appointments.Select(a => a.Id).ToHashSet();
        var tomorrowIds = tomorrow.Data!.Appointments.Select(a => a.Id).ToHashSet();
        Assert.Empty(todayIds.Intersect(tomorrowIds));
        Assert.Equal(4, todayIds.Count + tomorrowIds.Count);
    }

    [Fact]
    public async Task PaymentFilter_never_leaks_across_businesses()
    {
        using var holder = Holder.Create();
        var context = holder.Context;

        var ownerA = BookingTestBase.CreateOwner("owner-a@test.com");
        var businessA = BookingTestBase.CreateBusiness(ownerA.Id, "Business A");
        var customerA = BookingTestBase.CreateCustomer("customer-a@test.com");
        var serviceA = BookingTestBase.CreateService(businessA.Id, "Cut", 60, 1000m);

        var ownerB = BookingTestBase.CreateOwner("owner-b@test.com");
        var businessB = BookingTestBase.CreateBusiness(ownerB.Id, "Business B");
        var customerB = BookingTestBase.CreateCustomer("customer-b@test.com");
        var serviceB = BookingTestBase.CreateService(businessB.Id, "Cut", 60, 1000m);

        var when = new DateTime(2026, 5, 12, 9, 0, 0, DateTimeKind.Utc);
        var bookingA = BookingTestBase.CreateAppointment(businessA.Id, customerA.Id, null, when, when.AddHours(1));
        var bookingB = BookingTestBase.CreateAppointment(businessB.Id, customerB.Id, null, when, when.AddHours(1));

        await BookingTestBase.SeedAsync(context,
            ownerA, businessA, customerA, serviceA,
            ownerB, businessB, customerB, serviceB,
            bookingA, bookingB,
            BookingTestBase.CreateResource(bookingA.Id, serviceA.Id, "Cut", 60, 1000m, 0),
            BookingTestBase.CreateResource(bookingB.Id, serviceB.Id, "Cut", 60, 1000m, 0),
            Pay(businessA.Id, customerA.Id, bookingA.Id, PaymentStatus.Success, 1000m),
            Pay(businessB.Id, customerB.Id, bookingB.Id, PaymentStatus.Success, 1000m));

        var service = BookingTestBase.CreateAppointmentService(context);
        var result = await service.GetAppointmentDashboardAsync(
            ownerA.Id, null, null, null, null, null, null, null, "scheduledAt", "asc", 1, 50,
            paymentStatus: "verified");

        Assert.True(result.Success, result.Error?.Message);
        var item = Assert.Single(result.Data!.Appointments);
        Assert.Equal(bookingA.Id, item.Id);
        Assert.Equal(businessA.Id, item.BusinessId);
    }

    [Fact]
    public async Task PaymentFilter_returns_nothing_when_no_booking_matches()
    {
        using var holder = Holder.Create();
        var env = await SeedWindowAsync(holder.Context);
        var service = BookingTestBase.CreateAppointmentService(holder.Context);

        var result = await service.GetAppointmentDashboardAsync(
            env.Owner.Id, null, null, null, null, null, null, null, "scheduledAt", "asc", 1, 50,
            paymentStatus: "refunded");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Empty(result.Data!.Appointments);
        Assert.Equal(0, result.Data.Meta.TotalCount);
    }
}
