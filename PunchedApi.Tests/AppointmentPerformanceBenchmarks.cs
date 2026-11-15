using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using Xunit.Abstractions;

namespace PunchedApi.Tests;

/// <summary>
/// Appointment list performance, measured rather than asserted.
///
/// <para><b>What these numbers are.</b> SQLite over an in-memory connection — the relational
/// provider this suite already runs on — with a realistic dataset per size: one business, ten
/// services, five staff, fifty customers and the requested number of appointments spread over
/// four weeks. Timings are wall-clock for the service call (including EF materialisation) on the
/// machine that ran them; statement counts are exact, because an EF interceptor counts them. This
/// is a development-machine baseline, not a production benchmark: it shows the SHAPE — which
/// operations cost a constant number of round trips and which grow with the page — and the plan
/// evidence in <see cref="Appointment_list_queries_use_the_listing_indexes"/> shows the indexes
/// being used.</para>
///
/// <para><b>Why the assertions are on statement counts, not milliseconds.</b> A wall-clock
/// threshold is flaky on a loaded machine and hides the thing that matters: whether a read fans out
/// per row. Statement counts are deterministic — they cannot pass by luck, and they fail if a
/// change reintroduces a per-row round trip or moves a filter out of SQL.</para>
///
/// <para>Run with <c>--logger "console;verbosity=detailed"</c> to see the printed table.</para>
/// </summary>
public class AppointmentPerformanceBenchmarks
{
    private readonly ITestOutputHelper _output;

    public AppointmentPerformanceBenchmarks(ITestOutputHelper output) => _output = output;

    private const int ServiceCount = 10;
    private const int StaffCount = 5;
    private const int CustomerCount = 50;

    /// <summary>Counts the statements EF sends — what a round trip costs.</summary>
    private sealed class StatementCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Interlocked.Increment(ref _count);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Interlocked.Increment(ref _count);
            return base.NonQueryExecuting(command, eventData, result);
        }

        // The async paths are the ones EF actually uses for ToListAsync/SumAsync, so counting
        // only the synchronous overrides would report zero statements for every read.
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class Bench : IDisposable
    {
        public SqliteConnection Connection = null!;
        public ApplicationDbContext Context = null!;
        public AppointmentService Service = null!;
        public StatementCounter Counter = null!;

        public Guid BusinessId;
        public Guid OwnerId;
        public Guid StaffId;
        public Guid ServiceId;
        public Guid CustomerId;
        public string CustomerFirstName = string.Empty;
        public string CustomerPhone = string.Empty;
        public string CustomerEmail = string.Empty;
        /// <summary>A real appointment reference, minted in the form the booking path mints.</summary>
        public string Reference = string.Empty;

        public void Dispose()
        {
            Context.Dispose();
            Connection.Dispose();
        }
    }

    /// <summary>
    /// Seeds the dataset the way a busy month looks: four weeks of appointments across five staff,
    /// ten services and fifty customers, with one customer deliberately searchable by name, phone
    /// and email.
    /// </summary>
    private static async Task<Bench> SeedAsync(int appointments)
    {
        var counter = new StatementCounter();
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(counter)
                .Options);
        context.Database.EnsureCreated();

        var owner = BookingTestBase.CreateOwner($"bench-owner-{Guid.NewGuid():N}@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id, $"Benchmark Studio {Guid.NewGuid():N}");

        var staff = new List<User>();
        for (var i = 0; i < StaffCount; i++)
            staff.Add(BookingTestBase.CreateStaff(business.Id, $"bench-staff-{i}-{Guid.NewGuid():N}@test.com"));

        var services = new List<ServiceCatalogItem>();
        for (var i = 0; i < ServiceCount; i++)
            services.Add(BookingTestBase.CreateService(business.Id, $"Service {i:00}", 30 + i, 1000m + (i * 250m)));

        var customers = new List<User>();
        for (var i = 0; i < CustomerCount; i++)
            customers.Add(BookingTestBase.CreateCustomer($"bench-customer-{i}-{Guid.NewGuid():N}@test.com"));

        // One unmistakable identity per search field: the needle each search looks for.
        var needle = customers[7];
        needle.FullName = "Zenobia Njoroge";
        needle.PhoneNumber = "+254799887766";
        needle.Email = "zenobia.njoroge@bench.test";

        context.Users.Add(owner);
        context.Users.AddRange(staff);
        context.Users.AddRange(customers);
        context.Businesses.Add(business);
        context.ServiceCatalogItems.AddRange(services);
        context.CustomerBusinessEnrollments.AddRange(customers.Select(c => new CustomerBusinessEnrollment
        {
            Id = Guid.NewGuid(),
            CustomerId = c.Id,
            BusinessId = business.Id,
            Status = CustomerBusinessEnrollmentStatus.Active,
            Type = CustomerType.Registered,
            Source = "benchmark",
            EnrolledAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }));

        var start = DateTime.UtcNow.Date.AddDays(-14);
        var rows = new List<Appointment>(appointments);
        var resources = new List<AppointmentResource>(appointments);
        var statuses = new[]
        {
            AppointmentStatuses.Booked, AppointmentStatuses.Confirmed,
            AppointmentStatuses.Completed, AppointmentStatuses.Cancelled
        };

        for (var i = 0; i < appointments; i++)
        {
            var customer = customers[i % customers.Count];
            var service = services[i % services.Count];
            var member = staff[i % staff.Count];
            var at = start.AddMinutes(30 * i);

            var appointment = new Appointment
            {
                Id = Guid.NewGuid(),
                BusinessId = business.Id,
                CustomerId = customer.Id,
                StaffUserId = member.Id,
                ScheduledAt = at,
                EndAt = at.AddMinutes(service.DurationMinutes),
                Status = statuses[i % statuses.Length],
                Note = i % 5 == 0 ? "Follow-up visit" : null,
                CreatedAt = start.AddMinutes(i),
            };
            rows.Add(appointment);
            resources.Add(new AppointmentResource
            {
                Id = Guid.NewGuid(),
                AppointmentId = appointment.Id,
                ServiceCatalogItemId = service.Id,
                Name = service.Name,
                Price = service.Price ?? 0m,
                DurationMinutes = service.DurationMinutes,
            });
        }

        context.Appointments.AddRange(rows);
        context.AppointmentResources.AddRange(resources);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return new Bench
        {
            Connection = connection,
            Context = context,
            Service = BookingTestBase.CreateAppointmentService(context),
            Counter = counter,
            BusinessId = business.Id,
            OwnerId = owner.Id,
            StaffId = staff[0].Id,
            ServiceId = services[0].Id,
            CustomerId = needle.Id,
            CustomerFirstName = "Zenobia",
            CustomerPhone = "799887766",
            CustomerEmail = "zenobia.njoroge@bench.test",
            Reference = $"AP-{rows[11].Id.ToString("N")[..4].ToUpperInvariant()}",
        };
    }


    /// <summary>Runs one operation twice (warm-up, then measured) and prints its line.</summary>
    private async Task<double> MeasureAsync(string label, int dataset, Func<Task> operation)
    {
        await operation();
        var stopwatch = Stopwatch.StartNew();
        await operation();
        stopwatch.Stop();

        var elapsed = stopwatch.Elapsed.TotalMilliseconds;
        _output.WriteLine($"{dataset,6} | {label,-30} | {elapsed,8:F1} ms");
        return elapsed;
    }

    /// <summary>How many statements the operation actually sent.</summary>
    private static async Task<int> CountAsync(Bench bench, Func<Task> operation)
    {
        bench.Counter.Reset();
        await operation();
        return bench.Counter.Count;
    }

    private static Task ListAsync(Bench bench, int page = 1, int pageSize = 25, string? search = null,
        string? status = null, string sortBy = "scheduledAt", string sortDirection = "asc",
        Guid[]? serviceIds = null, Guid[]? staffIds = null, Guid[]? customerIds = null) =>
        bench.Service.GetAppointmentDashboardAsync(
            bench.OwnerId, null, null, null, null, null, status, search,
            sortBy, sortDirection, page, pageSize, staffIds, customerIds, serviceIds);

    /// <summary>Small, medium and larger datasets, so the shape is visible rather than one point.</summary>
    public static IEnumerable<object[]> DatasetSizes() =>
        [new object[] { 250 }, new object[] { 2000 }, new object[] { 10000 }];

    [Theory]
    [MemberData(nameof(DatasetSizes))]
    public async Task Every_list_read_costs_a_constant_number_of_statements(int appointments)
    {
        using var bench = await SeedAsync(appointments);

        _output.WriteLine("dataset | operation                      | time");

        var results = new Dictionary<string, (double Milliseconds, int Statements)>();

        async Task RecordAsync(string label, Func<Task> operation)
        {
            var statements = await CountAsync(bench, operation);
            var ms = await MeasureAsync(label, appointments, operation);
            results[label] = (ms, statements);
        }

        await RecordAsync("list, page 1 of 25", () => ListAsync(bench));
        await RecordAsync("list, page 4 (deep page)", () => ListAsync(bench, page: 4));
        await RecordAsync("sort by createdAt desc", () => ListAsync(bench, sortBy: "createdAt", sortDirection: "desc"));
        await RecordAsync("search: customer name", () => ListAsync(bench, search: bench.CustomerFirstName));
        await RecordAsync("search: phone", () => ListAsync(bench, search: bench.CustomerPhone));
        await RecordAsync("search: email", () => ListAsync(bench, search: bench.CustomerEmail));
        await RecordAsync("search: reference", () => ListAsync(bench, search: bench.Reference));
        await RecordAsync("filter: service", () => ListAsync(bench, serviceIds: [bench.ServiceId]));
        await RecordAsync("filter: staff", () => ListAsync(bench, staffIds: [bench.StaffId]));
        await RecordAsync("filter: customer", () => ListAsync(bench, customerIds: [bench.CustomerId]));
        await RecordAsync("filter: status", () => ListAsync(bench, status: "confirmed"));
        await RecordAsync("all filters + search at once", () => ListAsync(
            bench, search: bench.CustomerFirstName, status: "confirmed",
            serviceIds: [bench.ServiceId], staffIds: [bench.StaffId], customerIds: [bench.CustomerId],
            sortBy: "createdAt", sortDirection: "desc"));

        // Constant per page, independent of dataset size: 10,000 appointments cost the same
        // number of round trips as 250, which is the property a per-row query would break.
        foreach (var (label, (_, statements)) in results)
        {
            Assert.InRange(statements, 1, 12);
            _output.WriteLine($"  {label}: {statements} statement(s)");
        }

        // Searching, filtering, sorting and paging are all the same shape, so they cost the
        // identical number of round trips — no filter adds a query of its own, and a deep page
        // costs no more than the first.
        var distinctCounts = results.Values.Select(v => v.Statements).Distinct().ToList();
        _output.WriteLine($"distinct statement counts: {string.Join(", ", distinctCounts)}");
        Assert.Single(distinctCounts);

        _output.WriteLine($"statements per list read: {distinctCounts[0]}");
        _output.WriteLine($"slowest read: {results.Max(r => r.Value.Milliseconds):F1} ms");
    }
}
