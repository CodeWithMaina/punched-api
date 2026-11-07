using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// The optional-Staff-module contract for appointments.
///
/// <para>Three behaviours are asserted: the module gate is enforced on the server
/// (a client cannot bypass it by posting a staff id); a business without the module
/// can still book; and historical appointments keep their assigned staff even after
/// the module is switched off, only the *controls* disappear.</para>
/// </summary>
public class AppointmentStaffModuleTests
{
    private static readonly DateTime Ten = DateTime.UtcNow.Date.AddDays(7).AddHours(10);
    private static readonly DateOnly Day = DateOnly.FromDateTime(Ten);

    private sealed class Env
    {
        public ApplicationDbContext Context = null!;
        public Business Business = null!;
        public User Owner = null!;
        public User Customer = null!;
        public User Staff = null!;
        public ServiceCatalogItem Service = null!;
    }

    private static CustomerBusinessEnrollment Enrollment(Guid customerId, Guid businessId) => new()
    {
        Id = Guid.NewGuid(), CustomerId = customerId, BusinessId = businessId,
        Status = CustomerBusinessEnrollmentStatus.Active, Source = "test",
        EnrolledAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static async Task<Env> CreateEnvAsync(SqliteConnectionHolder holder)
    {
        var context = holder.Context;
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var customer = BookingTestBase.CreateCustomer();
        var staff = BookingTestBase.CreateStaff(business.Id);
        var service = BookingTestBase.CreateService(business.Id, "Cut", 60, 500m);

        await BookingTestBase.SeedAsync(context, owner, business, customer, staff, service,
            Enrollment(customer.Id, business.Id),
            BookingTestBase.CreateAssignment(business.Id, staff.Id, service.Id),
            BookingTestBase.CreateShift(business.Id, staff.Id, Day, 9, 18));

        return new Env
        {
            Context = context, Business = business, Owner = owner, Customer = customer,
            Staff = staff, Service = service
        };
    }

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

    private static CreateAppointmentOnBehalfRequest OnBehalf(Env env, Guid? staffUserId) => new()
    {
        BusinessId = env.Business.Id,
        ServiceIds = new[] { env.Service.Id },
        StaffUserId = staffUserId,
        ScheduledAt = Ten,
        CustomerId = env.Customer.Id
    };

    [Fact]
    public async Task StaffModule_Enabled_AllowsStaffAssignment()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        var service = BookingTestBase.CreateAppointmentService(
            env.Context, new StubModuleEntitlements("appointments", "staff"));

        var result = await service.CreateAppointmentOnBehalfAsync(
            env.Owner.Id, "Business", OnBehalf(env, env.Staff.Id));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(env.Staff.Id, result.Data!.StaffUserId);
    }

    [Fact]
    public async Task StaffModule_Disabled_RejectsStaffAssignment()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        // The business has appointments but NOT the optional staff module.
        var service = BookingTestBase.CreateAppointmentService(
            env.Context, new StubModuleEntitlements("appointments"));

        var result = await service.CreateAppointmentOnBehalfAsync(
            env.Owner.Id, "Business", OnBehalf(env, env.Staff.Id));

        Assert.False(result.Success);
        Assert.Equal("STAFF_MODULE_DISABLED", result.Error?.Code);
        Assert.Empty(await env.Context.Appointments.ToListAsync());
    }

    [Fact]
    public async Task StaffModule_Disabled_StillBooksWithoutAStaffMember()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        var service = BookingTestBase.CreateAppointmentService(
            env.Context, new StubModuleEntitlements("appointments"));

        // No staff id submitted: booking must not be blocked by the module being off.
        var result = await service.CreateAppointmentOnBehalfAsync(
            env.Owner.Id, "Business", OnBehalf(env, null));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Single(await env.Context.Appointments.ToListAsync());
    }

    [Fact]
    public async Task StaffModule_Enabled_RejectsCrossBusinessStaff()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        var service = BookingTestBase.CreateAppointmentService(
            env.Context, new StubModuleEntitlements("appointments", "staff"));

        // A staff member who belongs to a different business.
        var otherOwner = BookingTestBase.CreateOwner("other-owner@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other Co");
        var foreignStaff = BookingTestBase.CreateStaff(otherBusiness.Id, "foreign@test.com");
        await BookingTestBase.SeedAsync(env.Context, otherOwner, otherBusiness, foreignStaff);

        var result = await service.CreateAppointmentOnBehalfAsync(
            env.Owner.Id, "Business", OnBehalf(env, foreignStaff.Id));

        Assert.False(result.Success);
        Assert.Equal("STAFF_NOT_FOUND", result.Error?.Code);
    }

    private static Appointment Historic(Env env, Guid? staffUserId, int hourOffset)
    {
        var start = Ten.AddHours(hourOffset);
        var appointment = BookingTestBase.CreateAppointment(
            env.Business.Id, env.Customer.Id, staffUserId, start, start.AddHours(1));
        appointment.Resources.Add(BookingTestBase.CreateResource(
            appointment.Id, env.Service.Id, env.Service.Name, 60, 500m, 0));
        return appointment;
    }

    [Fact]
    public async Task Dashboard_StaffModuleDisabled_HidesStaffBucketButKeepsHistoricalStaff()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        await BookingTestBase.SeedAsync(env.Context, Historic(env, env.Staff.Id, 0));

        var service = BookingTestBase.CreateAppointmentService(
            env.Context, new StubModuleEntitlements("appointments"));

        var result = await service.GetAppointmentDashboardAsync(
            env.Owner.Id, null, null, null, null, null, null, null, "scheduledAt", "desc", 1, 25);

        Assert.True(result.Success, result.Error?.Message);
        Assert.False(result.Data!.StaffModuleEnabled);
        // No staff bucket → the UI has nothing to build a staff filter from.
        Assert.Empty(result.Data.Staff);
        // But the historical assignment is still rendered.
        var item = Assert.Single(result.Data.Appointments);
        Assert.Equal(env.Staff.Id, item.StaffUserId);
        Assert.Equal(env.Staff.FullName, item.StaffName);
    }

    [Fact]
    public async Task Dashboard_StaffModuleDisabled_IgnoresStaffFilter()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        await BookingTestBase.SeedAsync(env.Context,
            Historic(env, env.Staff.Id, 0),
            Historic(env, null, 2));

        var service = BookingTestBase.CreateAppointmentService(
            env.Context, new StubModuleEntitlements("appointments"));

        // A staffId is sent, but the module is off: the filter is inert.
        var result = await service.GetAppointmentDashboardAsync(
            env.Owner.Id, null, null, env.Staff.Id, null, null, null, null, "scheduledAt", "desc", 1, 25);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.Data!.Meta.TotalCount);
    }

    [Fact]
    public async Task Dashboard_StaffModuleEnabled_AppliesStaffFilter()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        await BookingTestBase.SeedAsync(env.Context,
            Historic(env, env.Staff.Id, 0),
            Historic(env, null, 2));

        var service = BookingTestBase.CreateAppointmentService(
            env.Context, new StubModuleEntitlements("appointments", "staff"));

        var result = await service.GetAppointmentDashboardAsync(
            env.Owner.Id, null, null, env.Staff.Id, null, null, null, null, "scheduledAt", "desc", 1, 25);

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(result.Data!.StaffModuleEnabled);
        Assert.Single(result.Data.Staff);
        Assert.Equal(1, result.Data.Meta.TotalCount);
    }
}
