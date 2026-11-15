using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Payment-verification filtering on the owner appointment dashboard.
///
/// <para>Payment is not a column on an appointment — one booking can hold a deposit,
/// a balance and a reversal — so the filter is answered from the payment ledger. These
/// tests pin the rules that are easy to get wrong: only settled money counts, a claim
/// awaiting verification never does, an outstanding-balance row is not a payment, and
/// several settled rows aggregate into one fully-paid appointment.</para>
/// </summary>
public class AppointmentPaymentFilterTests
{
    private static readonly DateTime Start = DateTime.UtcNow.Date.AddDays(3).AddHours(10);

    private sealed class Env
    {
        public User Owner = null!;
        public Business Business = null!;
        public User Customer = null!;
        public ServiceCatalogItem Service = null!;

        public Appointment Paid = null!;
        public Appointment Verified = null!;
        public Appointment Partial = null!;
        public Appointment Awaiting = null!;
        public Appointment Unpaid = null!;
        public Appointment Failed = null!;
        public Appointment Refunded = null!;
        public Appointment Split = null!;
    }

    private static Payment Pay(
        Guid businessId, Guid customerId, Guid appointmentId, PaymentStatus status, decimal amount) => new()
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

    private static Appointment Booking(Guid businessId, Guid customerId, int index) =>
        BookingTestBase.CreateAppointment(
            businessId, customerId, null, Start.AddHours(index), Start.AddHours(index).AddMinutes(60));

    private static async Task<Env> SeedAsync(SqliteConnectionHolder holder)
    {
        var context = holder.Context;
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var customer = BookingTestBase.CreateCustomer();
        var service = BookingTestBase.CreateService(business.Id, "Cut", 60, 1000m);

        var env = new Env { Owner = owner, Business = business, Customer = customer, Service = service };
        var bookings = new[]
        {
            env.Paid = Booking(business.Id, customer.Id, 0),
            env.Verified = Booking(business.Id, customer.Id, 1),
            env.Partial = Booking(business.Id, customer.Id, 2),
            env.Awaiting = Booking(business.Id, customer.Id, 3),
            env.Unpaid = Booking(business.Id, customer.Id, 4),
            env.Failed = Booking(business.Id, customer.Id, 5),
            env.Refunded = Booking(business.Id, customer.Id, 6),
            env.Split = Booking(business.Id, customer.Id, 7),
        };

        var entities = new List<object> { owner, business, customer, service };
        entities.AddRange(bookings);
        foreach (var booking in bookings)
            entities.Add(BookingTestBase.CreateResource(booking.Id, service.Id, "Cut", 60, 1000m, 0));

        // One row per state, plus one appointment whose money arrives in two settled rows.
        entities.Add(Pay(business.Id, customer.Id, env.Paid.Id, PaymentStatus.Success, 1000m));
        entities.Add(Pay(business.Id, customer.Id, env.Verified.Id, PaymentStatus.Verified, 1000m));
        entities.Add(Pay(business.Id, customer.Id, env.Partial.Id, PaymentStatus.Success, 400m));
        entities.Add(Pay(business.Id, customer.Id, env.Awaiting.Id, PaymentStatus.Submitted, 1000m));
        entities.Add(PaymentOutstanding.Create(business.Id, customer.Id, env.Unpaid.Id, 1000m, null, DateTime.UtcNow));
        entities.Add(Pay(business.Id, customer.Id, env.Failed.Id, PaymentStatus.Failed, 1000m));
        entities.Add(Pay(business.Id, customer.Id, env.Refunded.Id, PaymentStatus.Refunded, 1000m));
        entities.Add(Pay(business.Id, customer.Id, env.Split.Id, PaymentStatus.Success, 300m));
        entities.Add(Pay(business.Id, customer.Id, env.Split.Id, PaymentStatus.Verified, 700m));

        await BookingTestBase.SeedAsync(context, entities.ToArray());
        return env;
    }

    private static async Task<List<AppointmentDashboardItemResponse>> RunAsync(
        ApplicationDbContext context, Guid ownerId, string? paymentStatus)
    {
        var service = BookingTestBase.CreateAppointmentService(context);
        var result = await service.GetAppointmentDashboardAsync(
            ownerId, null, null, null, null, null, null, null, "scheduledAt", "asc", 1, 50,
            paymentStatus: paymentStatus);
        Assert.True(result.Success, result.Error?.Message);
        return result.Data!.Appointments;
    }

    private static AppointmentDashboardItemResponse For(
        List<AppointmentDashboardItemResponse> items, Guid id) =>
        Assert.Single(items.Where(item => item.Id == id));

    /// <summary>Owns the SQLite connection for the lifetime of a test.</summary>
    private sealed class SqliteConnectionHolder : IDisposable
    {
        public Microsoft.Data.Sqlite.SqliteConnection Connection = null!;
        public ApplicationDbContext Context = null!;

        public static SqliteConnectionHolder Create()
        {
            var connection = BookingTestBase.CreateConnection();
            return new SqliteConnectionHolder
            {
                Connection = connection,
                Context = BookingTestBase.CreateContext(connection)
            };
        }

        public void Dispose()
        {
            Context.Dispose();
            Connection.Dispose();
        }
    }

    [Fact]
    public async Task NoFilter_ReturnsEveryBooking()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await SeedAsync(holder);

        var items = await RunAsync(holder.Context, env.Owner.Id, null);

        Assert.Equal(8, items.Count);
    }

    [Theory]
    [InlineData("verified", 3)] // fully paid + owner-verified + the split booking
    [InlineData("partial", 1)]
    [InlineData("awaiting", 1)]
    [InlineData("unpaid", 1)]
    [InlineData("failed", 1)]
    [InlineData("refunded", 1)]
    [InlineData("all", 8)]
    [InlineData("nonsense", 8)] // an unknown value means "no payment filter"
    public async Task PaymentFilter_ReturnsOnlyTheMatchingBookings(string filter, int expected)
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await SeedAsync(holder);

        var items = await RunAsync(holder.Context, env.Owner.Id, filter);

        Assert.Equal(expected, items.Count);
    }

    [Fact]
    public async Task ClaimAwaitingVerification_IsNeverCountedAsCollected()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await SeedAsync(holder);

        var items = await RunAsync(holder.Context, env.Owner.Id, "awaiting");
        var item = For(items, env.Awaiting.Id);

        // A submitted claim is NOT money: it must not settle, and the full balance stands.
        Assert.Equal("awaiting_verification", item.PaymentStatus);
        Assert.Equal(0m, item.AmountPaid);
        Assert.Equal(1000m, item.Balance);
    }

    [Fact]
    public async Task OutstandingBalanceRow_IsNotAPayment()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await SeedAsync(holder);

        var items = await RunAsync(holder.Context, env.Owner.Id, "unpaid");
        var item = For(items, env.Unpaid.Id);

        // The appointment owes 1000; the Created/Unspecified outstanding row is not a payment.
        Assert.Equal("unpaid", item.PaymentStatus);
        Assert.Equal(0m, item.AmountPaid);
    }

    [Fact]
    public async Task MultipleSettledRows_AggregateIntoFullyPaid()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await SeedAsync(holder);

        var items = await RunAsync(holder.Context, env.Owner.Id, "verified");
        var item = For(items, env.Split.Id);

        // Success 300 + Verified 700 = the 1000 that was owed.
        Assert.Equal("paid", item.PaymentStatus);
        Assert.Equal(1000m, item.AmountPaid);
        Assert.Equal(0m, item.Balance);
    }

    [Fact]
    public async Task PartiallyPaid_ReportsTheOutstandingBalance()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await SeedAsync(holder);

        var items = await RunAsync(holder.Context, env.Owner.Id, "partial");
        var item = For(items, env.Partial.Id);

        Assert.Equal("partially_paid", item.PaymentStatus);
        Assert.Equal(400m, item.AmountPaid);
        Assert.Equal(600m, item.Balance);
    }

    [Fact]
    public async Task Refunded_IsNotCountedAsCollected()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await SeedAsync(holder);

        var items = await RunAsync(holder.Context, env.Owner.Id, "refunded");
        var item = For(items, env.Refunded.Id);

        Assert.Equal("refunded", item.PaymentStatus);
        Assert.Equal(0m, item.AmountPaid);
    }
}

