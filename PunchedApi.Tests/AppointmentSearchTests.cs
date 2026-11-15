using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Server-side appointment search, multi-select filters and list ordering.
///
/// <para><b>Why these exercise a real provider.</b> Search used to call
/// <c>ToLowerInvariant()</c> inside the LINQ query, which EF Core cannot translate to SQL
/// (dotnet/efcore#18995, closed as not planned) — so every search failed the whole list
/// request instead of returning results. These tests run the real query against SQLite,
/// where an untranslatable predicate throws exactly as it does on PostgreSQL.</para>
/// </summary>
public class AppointmentSearchTests
{
    private static readonly DateTime Ten = new(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);

    private sealed class Env
    {
        public ApplicationDbContext Context = null!;
        public Business Business = null!;
        public User Owner = null!;
        public User Customer = null!;
        public User OtherCustomer = null!;
        public User Staff = null!;
        public ServiceCatalogItem Haircut = null!;
        public ServiceCatalogItem BeardTrim = null!;
    }

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

    private static CustomerBusinessEnrollment Enrollment(Guid customerId, Guid businessId) => new()
    {
        Id = Guid.NewGuid(), CustomerId = customerId, BusinessId = businessId,
        Status = CustomerBusinessEnrollmentStatus.Active, Source = "test",
        EnrolledAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    /// <summary>
    /// One business with two customers, one staff member and two services. Names and the
    /// phone number are set explicitly because they are what the search matches on.
    /// </summary>
    private static async Task<Env> CreateEnvAsync(SqliteConnectionHolder holder)
    {
        var context = holder.Context;
        var owner = BookingTestBase.CreateOwner("search-owner@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Search Studio");
        var customer = BookingTestBase.CreateCustomer("john.kamau@test.com");
        customer.FullName = "John Kamau";
        customer.PhoneNumber = "254712345678";
        var other = BookingTestBase.CreateCustomer("mary.wanjiku@test.com");
        other.FullName = "Mary Wanjiku";
        other.PhoneNumber = "254701111222";
        var staff = BookingTestBase.CreateStaff(business.Id, "staff.one@test.com");
        staff.FullName = "Staff Member A";
        var haircut = BookingTestBase.CreateService(business.Id, "Haircut", 60, 500m);
        var beard = BookingTestBase.CreateService(business.Id, "Beard Trim", 30, 300m);

        await BookingTestBase.SeedAsync(context, owner, business, customer, other, staff, haircut, beard,
            Enrollment(customer.Id, business.Id),
            Enrollment(other.Id, business.Id));

        return new Env
        {
            Context = context, Business = business, Owner = owner, Customer = customer,
            OtherCustomer = other, Staff = staff, Haircut = haircut, BeardTrim = beard
        };
    }

    private static Appointment AppointmentFor(
        Env env, User customer, Guid? staffUserId, DateTime start, Guid serviceId, string name,
        string status = "booked", DateTime? createdAt = null)
    {
        var appointment = BookingTestBase.CreateAppointment(
            env.Business.Id, customer.Id, staffUserId, start, start.AddMinutes(60), status);
        if (createdAt.HasValue) appointment.CreatedAt = createdAt.Value;
        appointment.Resources.Add(BookingTestBase.CreateResource(
            appointment.Id, serviceId, name, 60, 500m, 0));
        return appointment;
    }

    private static Task<ApiResponse<AppointmentDashboardResponse>> Search(
        AppointmentService service, Guid ownerId, string? search,
        string? status = null, Guid[]? serviceIds = null, Guid[]? customerIds = null,
        Guid[]? staffIds = null, string sortBy = "scheduledAt", string sortDirection = "desc",
        int page = 1, int pageSize = 20)
        => service.GetAppointmentDashboardAsync(
            ownerId, null, null, null, null, null, status, search,
            sortBy, sortDirection, page, pageSize, staffIds, customerIds, serviceIds);


    // ═══════════════════════════════════════════════════════════
    //  REFERENCE PARSING (the pure part of reference search)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Reference_MatchesTheFormatTheDashboardPrints()
    {
        var id = Guid.Parse("3f2a9c41-1111-2222-3333-444455556666");

        Assert.Equal("AP-3F2A9C41", AppointmentReferences.For(id));
    }

    [Fact]
    public void Reference_PrefixRange_BracketsTheWholePrefixInTheIdSpace()
    {
        // The boundaries are compared as ids by whichever provider stores them, so the
        // invariant that matters is what they are made of: the prefix followed by zeros,
        // and the NEXT prefix followed by zeros.
        Assert.True(AppointmentReferences.TryParsePrefixRange("AP-3F2A", out var four));

        Assert.StartsWith("3f2a", four.Low.ToString("N"));
        Assert.Equal(new string('0', 28), four.Low.ToString("N")[4..]);
        Assert.StartsWith("3f2b", four.High.ToString("N"));
        Assert.Equal(new string('0', 28), four.High.ToString("N")[4..]);

        // A full eight-digit reference is its own one-id-wide window.
        Assert.True(AppointmentReferences.TryParsePrefixRange("AP-3F2A9C41", out var full));
        Assert.StartsWith("3f2a9c41", full.Low.ToString("N"));
        Assert.StartsWith("3f2a9c42", full.High.ToString("N"));
    }

    [Fact]
    public void Reference_PrefixRange_RejectsTermsThatAreNotPrefixes()
    {
        Assert.False(AppointmentReferences.TryParsePrefixRange("XYZ", out _));
        Assert.False(AppointmentReferences.TryParsePrefixRange("john kamau", out _));
    }

    // ═══════════════════════════════════════════════════════════
    //  SEARCH
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Search_ByCustomerName_IsCaseInsensitive()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        await BookingTestBase.SeedAsync(env.Context,
            AppointmentFor(env, env.Customer, null, Ten, env.Haircut.Id, "Haircut"),
            AppointmentFor(env, env.OtherCustomer, null, Ten.AddHours(2), env.BeardTrim.Id, "Beard Trim"));

        var service = BookingTestBase.CreateAppointmentService(env.Context);

        var result = await Search(service, env.Owner.Id, "john");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("John Kamau", Assert.Single(result.Data!.Appointments).CustomerName);
        Assert.Equal(1, result.Data.Meta.TotalCount);
    }

    [Fact]
    public async Task Search_ByCustomerPhone_IgnoresFormatting()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        await BookingTestBase.SeedAsync(env.Context,
            AppointmentFor(env, env.Customer, null, Ten, env.Haircut.Id, "Haircut"),
            AppointmentFor(env, env.OtherCustomer, null, Ten.AddHours(2), env.BeardTrim.Id, "Beard Trim"));

        var service = BookingTestBase.CreateAppointmentService(env.Context);

        // Typed the way a Kenyan number is written, against a number stored as 2547…
        var result = await Search(service, env.Owner.Id, "0712 345 678");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("John Kamau", Assert.Single(result.Data!.Appointments).CustomerName);
    }

    [Fact]
    public async Task Search_ByCustomerEmail_MatchesPartially()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        await BookingTestBase.SeedAsync(env.Context,
            AppointmentFor(env, env.Customer, null, Ten, env.Haircut.Id, "Haircut"),
            AppointmentFor(env, env.OtherCustomer, null, Ten.AddHours(2), env.BeardTrim.Id, "Beard Trim"));

        var service = BookingTestBase.CreateAppointmentService(env.Context);

        var result = await Search(service, env.Owner.Id, "mary.wanjiku@");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("Mary Wanjiku", Assert.Single(result.Data!.Appointments).CustomerName);
    }

    [Fact]
    public async Task Search_ByAppointmentReference_FindsTheBooking()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        var wanted = AppointmentFor(env, env.Customer, null, Ten, env.Haircut.Id, "Haircut");
        await BookingTestBase.SeedAsync(env.Context,
            wanted,
            AppointmentFor(env, env.OtherCustomer, null, Ten.AddHours(2), env.BeardTrim.Id, "Beard Trim"));

        var service = BookingTestBase.CreateAppointmentService(env.Context);

        var reference = AppointmentReferences.For(wanted.Id);
        var full = await Search(service, env.Owner.Id, reference);
        Assert.True(full.Success, full.Error?.Message);
        Assert.Equal(wanted.Id, Assert.Single(full.Data!.Appointments).Id);

        // …and the short prefix form works too.
        var prefix = await Search(service, env.Owner.Id, reference[..7]);
        Assert.True(prefix.Success, prefix.Error?.Message);
        Assert.Contains(prefix.Data!.Appointments, row => row.Id == wanted.Id);
    }

    [Fact]
    public async Task Search_ByFullAppointmentId_FindsTheBooking()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        var wanted = AppointmentFor(env, env.Customer, null, Ten, env.Haircut.Id, "Haircut");
        await BookingTestBase.SeedAsync(env.Context,
            wanted,
            AppointmentFor(env, env.OtherCustomer, null, Ten.AddHours(2), env.BeardTrim.Id, "Beard Trim"));

        var service = BookingTestBase.CreateAppointmentService(env.Context);

        var result = await Search(service, env.Owner.Id, wanted.Id.ToString());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(wanted.Id, Assert.Single(result.Data!.Appointments).Id);
    }


    [Fact]
    public async Task Search_ByServiceName_MatchesTheBookedServiceSnapshot()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        await BookingTestBase.SeedAsync(env.Context,
            AppointmentFor(env, env.Customer, null, Ten, env.Haircut.Id, "Haircut"),
            AppointmentFor(env, env.OtherCustomer, null, Ten.AddHours(2), env.BeardTrim.Id, "Beard Trim"));

        var service = BookingTestBase.CreateAppointmentService(env.Context);

        var result = await Search(service, env.Owner.Id, "beard");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(env.OtherCustomer.Id, Assert.Single(result.Data!.Appointments).CustomerId);
    }

    [Fact]
    public async Task Search_ByStaffName_OnlyWhileTheStaffModuleIsOn()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        await BookingTestBase.SeedAsync(env.Context,
            AppointmentFor(env, env.Customer, env.Staff.Id, Ten, env.Haircut.Id, "Haircut"),
            AppointmentFor(env, env.OtherCustomer, null, Ten.AddHours(2), env.BeardTrim.Id, "Beard Trim"));

        // Module on: the assigned professional is a search criterion.
        var withStaff = BookingTestBase.CreateAppointmentService(
            env.Context, new StubModuleEntitlements("appointments", "staff"));
        var found = await Search(withStaff, env.Owner.Id, "Staff Member A");
        Assert.True(found.Success, found.Error?.Message);
        Assert.Single(found.Data!.Appointments);

        // Module off: the same term must not match on a staff member's name.
        var withoutStaff = BookingTestBase.CreateAppointmentService(
            env.Context, new StubModuleEntitlements("appointments"));
        var hidden = await Search(withoutStaff, env.Owner.Id, "Staff Member A");
        Assert.True(hidden.Success, hidden.Error?.Message);
        Assert.Empty(hidden.Data!.Appointments);
    }

    [Fact]
    public async Task Search_MatchesNothing_ReturnsAnEmptyPageRatherThanAnError()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        await BookingTestBase.SeedAsync(env.Context,
            AppointmentFor(env, env.Customer, null, Ten, env.Haircut.Id, "Haircut"));

        var service = BookingTestBase.CreateAppointmentService(env.Context);

        var result = await Search(service, env.Owner.Id, "nobody with this name");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Empty(result.Data!.Appointments);
        Assert.Equal(0, result.Data.Meta.TotalCount);
    }

    [Fact]
    public async Task Search_IsTenantScoped_AnotherBusinessesBookingIsNeverReturned()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);

        var foreignOwner = BookingTestBase.CreateOwner("foreign-owner@test.com");
        var foreignBusiness = BookingTestBase.CreateBusiness(foreignOwner.Id, "Foreign Co");
        var foreignCustomer = BookingTestBase.CreateCustomer("john.kamau@foreign.test");
        foreignCustomer.FullName = "John Kamau";  // same name, different tenant
        foreignCustomer.PhoneNumber = "254712345678";
        var foreignAppointment = BookingTestBase.CreateAppointment(
            foreignBusiness.Id, foreignCustomer.Id, null, Ten, Ten.AddMinutes(60));
        foreignAppointment.Resources.Add(BookingTestBase.CreateResource(
            foreignAppointment.Id, Guid.NewGuid(), "Haircut", 60, 500m, 0));
        await BookingTestBase.SeedAsync(env.Context, foreignOwner, foreignBusiness, foreignCustomer, foreignAppointment);

        await BookingTestBase.SeedAsync(env.Context,
            AppointmentFor(env, env.Customer, null, Ten.AddHours(2), env.Haircut.Id, "Haircut"));

        var service = BookingTestBase.CreateAppointmentService(env.Context);
        var result = await Search(service, env.Owner.Id, "john kamau");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(env.Customer.Id, Assert.Single(result.Data!.Appointments).CustomerId);
    }

    // ═══════════════════════════════════════════════════════════
    //  FILTERS & ORDERING
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Filters_MultipleServices_Customers_StaffAndStatus_AreAppliedTogether()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        await BookingTestBase.SeedAsync(env.Context,
            // Matches every filter.
            AppointmentFor(env, env.Customer, env.Staff.Id, Ten, env.Haircut.Id, "Haircut", "confirmed"),
            // Same service, wrong customer.
            AppointmentFor(env, env.OtherCustomer, env.Staff.Id, Ten.AddHours(1), env.Haircut.Id, "Haircut", "confirmed"),
            // Same customer, wrong status.
            AppointmentFor(env, env.Customer, env.Staff.Id, Ten.AddHours(2), env.Haircut.Id, "Haircut", "cancelled"),
            // Same customer and status, wrong service.
            AppointmentFor(env, env.Customer, env.Staff.Id, Ten.AddHours(3), env.BeardTrim.Id, "Beard Trim", "confirmed"));

        var service = BookingTestBase.CreateAppointmentService(
            env.Context, new StubModuleEntitlements("appointments", "staff"));

        var result = await Search(
            service, env.Owner.Id, null, "confirmed",
            serviceIds: [env.Haircut.Id], customerIds: [env.Customer.Id], staffIds: [env.Staff.Id]);

        Assert.True(result.Success, result.Error?.Message);
        var row = Assert.Single(result.Data!.Appointments);
        Assert.Equal(env.Customer.Id, row.CustomerId);
        Assert.Equal("confirmed", row.Status);
    }

    [Fact]
    public async Task Filters_MultiSelectService_MatchesAnyOfTheChosenServices()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        await BookingTestBase.SeedAsync(env.Context,
            AppointmentFor(env, env.Customer, null, Ten, env.Haircut.Id, "Haircut"),
            AppointmentFor(env, env.OtherCustomer, null, Ten.AddHours(2), env.BeardTrim.Id, "Beard Trim"));

        var service = BookingTestBase.CreateAppointmentService(env.Context);

        var result = await Search(service, env.Owner.Id, null,
            serviceIds: [env.Haircut.Id, env.BeardTrim.Id]);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.Data!.Meta.TotalCount);
    }

    [Fact]
    public async Task Filters_StaffSelection_IsIgnoredWhileTheModuleIsOff()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        await BookingTestBase.SeedAsync(env.Context,
            AppointmentFor(env, env.Customer, env.Staff.Id, Ten, env.Haircut.Id, "Haircut"),
            AppointmentFor(env, env.OtherCustomer, null, Ten.AddHours(2), env.BeardTrim.Id, "Beard Trim"));

        var service = BookingTestBase.CreateAppointmentService(
            env.Context, new StubModuleEntitlements("appointments"));

        var result = await Search(service, env.Owner.Id, null, staffIds: [env.Staff.Id]);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.Data!.Meta.TotalCount);
    }

    [Fact]
    public async Task Ordering_RecentlyCreated_PutsTheNewestBookingFirst_NotTheLatestSlot()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        var first = AppointmentFor(env, env.Customer, null, Ten.AddHours(5), env.Haircut.Id, "Haircut",
            createdAt: new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc));
        var second = AppointmentFor(env, env.OtherCustomer, null, Ten, env.BeardTrim.Id, "Beard Trim",
            createdAt: new DateTime(2026, 1, 2, 8, 0, 0, DateTimeKind.Utc));
        await BookingTestBase.SeedAsync(env.Context, first, second);

        var service = BookingTestBase.CreateAppointmentService(env.Context);

        var recent = await Search(service, env.Owner.Id, null, sortBy: "createdAt", sortDirection: "desc");
        Assert.True(recent.Success, recent.Error?.Message);
        Assert.Equal(second.Id, recent.Data!.Appointments[0].Id);

        var oldest = await Search(service, env.Owner.Id, null, sortBy: "createdAt", sortDirection: "asc");
        Assert.Equal(first.Id, oldest.Data!.Appointments[0].Id);

        // Scheduled order is a different question with a different answer.
        var scheduled = await Search(service, env.Owner.Id, null, sortBy: "scheduledAt", sortDirection: "asc");
        Assert.Equal(second.Id, scheduled.Data!.Appointments[0].Id);
    }

    [Fact]
    public async Task Pagination_CombinesWithFiltersWithoutLosingTheTotalOrOverlappingPages()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        for (var index = 0; index < 5; index++)
        {
            await BookingTestBase.SeedAsync(env.Context,
                AppointmentFor(env, env.Customer, null, Ten.AddHours(index), env.Haircut.Id, "Haircut",
                    createdAt: new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc).AddMinutes(index)));
        }

        await BookingTestBase.SeedAsync(env.Context,
            AppointmentFor(env, env.OtherCustomer, null, Ten.AddHours(9), env.BeardTrim.Id, "Beard Trim"));

        var service = BookingTestBase.CreateAppointmentService(env.Context);

        var firstPage = await Search(service, env.Owner.Id, null,
            serviceIds: [env.Haircut.Id], sortBy: "createdAt", page: 1, pageSize: 2);
        var secondPage = await Search(service, env.Owner.Id, null,
            serviceIds: [env.Haircut.Id], sortBy: "createdAt", page: 2, pageSize: 2);

        Assert.True(firstPage.Success, firstPage.Error?.Message);
        Assert.Equal(5, firstPage.Data!.Meta.TotalCount);
        Assert.Equal(3, firstPage.Data.Meta.TotalPages);
        Assert.Equal(2, firstPage.Data.Appointments.Count);
        Assert.Equal(2, secondPage.Data!.Appointments.Count);
        // Deterministic ordering: the two pages cannot overlap.
        Assert.Empty(firstPage.Data.Appointments
            .Select(row => row.Id)
            .Intersect(secondPage.Data.Appointments.Select(row => row.Id)));
    }
}

