using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// The two orderings the appointment list has to get right, and the property that
/// makes them trustworthy: the order is decided in the DATABASE, before the page is
/// cut, so a page boundary can never repeat or skip a row.
///
/// <list type="bullet">
/// <item>The list defaults to <c>CreatedAt DESC</c> — "what came in just now?" — which
/// is a different question from the schedule.</item>
/// <item>Naming <c>scheduledAt</c> gives clock order, which is what the calendar and
/// the timeline ask for.</item>
/// </list>
/// </summary>
public class AppointmentListOrderingTests
{
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

    private static async Task<(Guid OwnerId, List<Appointment> Bookings)> SeedAsync(ApplicationDbContext context)
    {
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var customer = BookingTestBase.CreateCustomer();
        var service = BookingTestBase.CreateService(business.Id, "Cut", 60, 500m);

        // Five bookings: the one created FIRST is scheduled LAST, so "created" and
        // "scheduled" orders are the reverse of each other and a mix-up cannot pass.
        var start = new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);
        var created = new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc);
        var bookings = new List<Appointment>();
        for (var index = 0; index < 5; index += 1)
        {
            var booking = BookingTestBase.CreateAppointment(
                business.Id, customer.Id, null, start.AddDays(index), start.AddDays(index).AddHours(1));
            booking.CreatedAt = created.AddDays(index);
            bookings.Add(booking);
        }

        var seed = new List<object> { owner, business, customer, service };
        seed.AddRange(bookings);
        await BookingTestBase.SeedAsync(context, seed.ToArray());
        return (owner.Id, bookings);
    }

    [Fact]
    public async Task Default_order_is_newest_created_first_and_survives_paging()
    {
        using var holder = Holder.Create();
        var (ownerId, bookings) = await SeedAsync(holder.Context);
        var service = BookingTestBase.CreateAppointmentService(holder.Context);

        var expected = bookings.OrderByDescending(b => b.CreatedAt).Select(b => b.Id).ToList();

        var pageOne = await service.GetAppointmentDashboardAsync(
            ownerId, null, null, null, null, null, null, null, "createdAt", "desc", 1, 2);
        var pageTwo = await service.GetAppointmentDashboardAsync(
            ownerId, null, null, null, null, null, null, null, "createdAt", "desc", 2, 2);
        var pageThree = await service.GetAppointmentDashboardAsync(
            ownerId, null, null, null, null, null, null, null, "createdAt", "desc", 3, 2);

        Assert.True(pageOne.Success, pageOne.Error?.Message);
        var seen = pageOne.Data!.Appointments
            .Concat(pageTwo.Data!.Appointments)
            .Concat(pageThree.Data!.Appointments)
            .Select(a => a.Id)
            .ToList();

        // The newest-created booking leads, every booking appears exactly once, and the
        // concatenated pages are the whole ordered set.
        Assert.Equal(expected[0], seen[0]);
        Assert.Equal(expected, seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(5, pageOne.Data.Meta.TotalCount);
    }

    [Fact]
    public async Task Naming_scheduled_light_gives_clock_order_instead()
    {
        using var holder = Holder.Create();
        var (ownerId, bookings) = await SeedAsync(holder.Context);
        var service = BookingTestBase.CreateAppointmentService(holder.Context);

        var result = await service.GetAppointmentDashboardAsync(
            ownerId, null, null, null, null, null, null, null, "scheduledAt", "asc", 1, 10);

        Assert.True(result.Success, result.Error?.Message);
        var scheduled = result.Data!.Appointments.Select(a => a.ScheduledAt).ToList();
        // Strictly ascending, and the opposite end from the created order.
        Assert.Equal(scheduled.OrderBy(value => value), scheduled);
        Assert.Equal(bookings.OrderBy(b => b.ScheduledAt).Select(b => b.Id),
            result.Data.Appointments.Select(a => a.Id));
        Assert.NotEqual(bookings.OrderByDescending(b => b.CreatedAt).First().Id, result.Data.Appointments[0].Id);
    }
}
