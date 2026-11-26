using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Step 4 / G6 — fine-grained permission checks at the service level.
/// Verifies the two documented rules using the real PermissionMatrix:
///  • Staff may NOT manage business appointments (no appointments.manage) → FORBIDDEN.
///  • Staff MAY award stamps (holds stamps.award).
/// Also proves the staff member's own-appointment workflow is NOT blocked by
/// the manage guard (confirm/complete are staff transitions, not management).
/// </summary>
public class PermissionEnforcementTests : IDisposable
{
    private SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;

    private ApplicationDbContext NewContext()
    {
        if (_context == null)
        {
            _connection = BookingTestBase.CreateConnection();
            _context = BookingTestBase.CreateContext(_connection);
        }
        else
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options;
            _context = new ApplicationDbContext(options);
        }
        return _context;
    }

    // ── Role → permission authority (real static matrix) ─────────

    [Fact]
    public void Staff_HasStampAwardPermission_ButNotAppointmentManage()
    {
        var permissions = new PermissionService();

        Assert.True(permissions.HasPermission("Staff", "stamps.award"));
        Assert.False(permissions.HasPermission("Staff", "appointments.manage"));
        Assert.True(permissions.HasPermission("Business", "appointments.manage"));
    }

    // ── AppointmentService: staff creation is permission-governed ──

    /// <summary>
    /// The old contract forbade every Staff persona from creating an appointment.
    /// Creation is now a business permission (<c>appointments.create</c>), granted by
    /// the owner to a role — so the request passes the authorization gate and is
    /// answered by the same validation every other booking path uses. The unknown
    /// service proves the flow continued past authorization without creating anything.
    /// </summary>
    [Fact]
    public async Task Staff_CreatingBusinessAppointment_IsGovernedByBusinessPermission()
    {
        var context = NewContext();
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var staff = BookingTestBase.CreateStaff(business.Id);
        await BookingTestBase.SeedAsync(context, owner, business, staff);

        var service = BookingTestBase.CreateAppointmentService(context);
        var result = await service.CreateAppointmentOnBehalfAsync(
            staff.Id, "Staff",
            new CreateAppointmentOnBehalfRequest
            {
                BusinessId = business.Id,
                CustomerId = Guid.NewGuid(),
                ServiceIds = new[] { Guid.NewGuid() },
                ScheduledAt = DateTime.UtcNow.AddHours(1),
            });

        Assert.False(result.Success);
        Assert.Equal("SERVICE_NOT_FOUND", result.Error?.Code);
    }

    /// <summary>
    /// A service professional may book themselves in, but filling a colleague's
    /// diary is a management action: it needs <c>appointments.manage</c>. The guard
    /// runs before availability, so the refusal is authorization — not a calendar
    /// clash that happens to prevent it.
    /// </summary>
    [Fact]
    public async Task Staff_CannotBookAnotherStaffMember_WithoutManagePermission()
    {
        var context = NewContext();
        var owner = BookingTestBase.CreateOwner("owner-self@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var customer = BookingTestBase.CreateCustomer("customer-self@test.com");
        var caller = BookingTestBase.CreateStaff(business.Id, "caller@test.com");
        var colleague = BookingTestBase.CreateStaff(business.Id, "colleague@test.com");
        var service = BookingTestBase.CreateService(business.Id, "Cut", 60, 500m);

        var day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var enrollment = new CustomerBusinessEnrollment
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            BusinessId = business.Id,
            Status = CustomerBusinessEnrollmentStatus.Active,
            Source = "test",
            EnrolledAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await BookingTestBase.SeedAsync(context, owner, business, customer, caller, colleague, service,
            enrollment,
            BookingTestBase.CreateAssignment(business.Id, colleague.Id, service.Id),
            BookingTestBase.CreateShift(business.Id, colleague.Id, day, 9, 18));

        var appointmentService = BookingTestBase.CreateAppointmentService(context);
        var result = await appointmentService.CreateAppointmentOnBehalfAsync(
            caller.Id, "Staff",
            new CreateAppointmentOnBehalfRequest
            {
                BusinessId = business.Id,
                CustomerId = customer.Id,
                ServiceIds = new[] { service.Id },
                StaffUserId = colleague.Id,
                ScheduledAt = day.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc)
            });

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.Error?.Code);
    }

    // ── AppointmentService: staff may confirm their own booking ──

    [Fact]
    public async Task Staff_ConfirmingOwnAppointment_IsAllowed()
    {
        var context = NewContext();
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var customer = BookingTestBase.CreateCustomer();
        var staff = BookingTestBase.CreateStaff(business.Id);
        var now = DateTime.UtcNow;
        var appointment = BookingTestBase.CreateAppointment(
            business.Id, customer.Id, staff.Id,
            now.AddHours(1), now.AddHours(2), status: "booked");
        await BookingTestBase.SeedAsync(context, owner, business, customer, staff, appointment);

        var service = BookingTestBase.CreateAppointmentService(context);
        var result = await service.ConfirmAsync(staff.Id, "Staff", appointment.Id);

        Assert.True(result.Success);
        Assert.Equal("confirmed", result.Data?.Status);
    }

    public void Dispose()
    {
        _context?.Dispose();
        _connection?.Dispose();
    }
}