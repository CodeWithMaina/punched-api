using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Booking at a business that has no staff.
///
/// The rule under test is that a business is never required to invent staff accounts
/// to be bookable: with no active staff, the owner is the provider. When staff DO
/// exist, the existing staff-selection workflow is preserved unchanged.
/// </summary>
public class SoloBusinessBookingTests
{
    /// <summary>Business-local placeholder day; the engine treats it as local, so it must be future.</summary>
    private static readonly DateOnly Day = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(7));

    private static AppointmentAvailabilityService Availability(ApplicationDbContext context)
        => BookingTestBase.CreateAvailabilityService(context);

    private static CustomerBusinessEnrollment Enrollment(Guid customerId, Guid businessId) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = customerId,
        BusinessId = businessId,
        Status = CustomerBusinessEnrollmentStatus.Active,
        Source = "discovery",
        EnrolledAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    /// <summary>A bookable time inside the business's default window (09:00–18:00 local).</summary>
    private static DateTime Slot(int hour = 10) =>
        new(Day.Year, Day.Month, Day.Day, hour, 0, 0, DateTimeKind.Utc);

    private static BusinessPaymentConfig ManualMpesaConfig(Guid businessId, decimal feePercentage) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        CashEnabled = true,
        ManualMpesaEnabled = true,
        MpesaPaybillNumber = "247247",
        MpesaPaybillAccount = "12345",
        BookingFeePercentage = feePercentage,
        CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task Availability_ForABusinessWithNoStaff_OffersSlotsToTheOwner()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);

        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var service = BookingTestBase.CreateService(business.Id, "Cut", 60, 500m);

        // No staff member, and no staff-service assignment: the owner is the provider.
        await BookingTestBase.SeedAsync(context, owner, business, service);

        var result = await Availability(context)
            .GetAvailableSlotsAsync(business.Id, new[] { service.Id }, null, Day, Day);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotEmpty(result.Data!);
        // Every offered slot is the owner's: there is nobody else it could be.
        Assert.All(result.Data!, slot => Assert.Equal(owner.Id, slot.StaffUserId));
    }

    [Fact]
    public async Task Booking_AtABusinessWithNoStaff_AssignsTheOwnerAutomatically()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);

        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var customer = BookingTestBase.CreateCustomer();
        var service = BookingTestBase.CreateService(business.Id, "Cut", 60, 500m);

        await BookingTestBase.SeedAsync(context, owner, business, customer, service,
            Enrollment(customer.Id, business.Id));

        var created = await BookingTestBase.CreateAppointmentService(context)
            .CreateAppointmentAsync(customer.Id, "Customer", new CreateAppointmentRequest
            {
                BusinessId = business.Id,
                ServiceIds = new[] { service.Id },
                ScheduledAt = Slot()
            });

        Assert.True(created.Success, created.Error?.Message);

        context.ChangeTracker.Clear();
        var stored = await context.Appointments.SingleAsync();
        // The owner IS the assignee — no fake staff row was needed, and the
        // appointment actually occupies somebody's diary.
        Assert.Equal(owner.Id, stored.StaffUserId);
        // Nothing is owed up front, so it is an ordinary booking.
        Assert.Equal(AppointmentStatuses.Booked, stored.Status);
    }

    [Fact]
    public async Task Booking_WhenStaffExist_UsesTheStaffMemberAndNotTheOwner()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);

        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var staff = BookingTestBase.CreateStaff(business.Id);
        var customer = BookingTestBase.CreateCustomer();
        var service = BookingTestBase.CreateService(business.Id, "Cut", 60, 500m);

        await BookingTestBase.SeedAsync(context, owner, business, staff, customer, service,
            Enrollment(customer.Id, business.Id),
            BookingTestBase.CreateAssignment(business.Id, staff.Id, service.Id));

        var created = await BookingTestBase.CreateAppointmentService(context)
            .CreateAppointmentAsync(customer.Id, "Customer", new CreateAppointmentRequest
            {
                BusinessId = business.Id,
                ServiceIds = new[] { service.Id },
                ScheduledAt = Slot()
            });

        Assert.True(created.Success, created.Error?.Message);

        context.ChangeTracker.Clear();
        // The existing staff-selection workflow is preserved: the eligible staff
        // member takes the slot, and the owner is not silently substituted.
        Assert.Equal(staff.Id, (await context.Appointments.SingleAsync()).StaffUserId);
    }

    [Fact]
    public async Task Booking_WithAManualMpesaFee_StartsAwaitingPayment()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);

        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var customer = BookingTestBase.CreateCustomer();
        var service = BookingTestBase.CreateService(business.Id, "Cut", 60, 1000m);

        await BookingTestBase.SeedAsync(context, owner, business, customer, service,
            Enrollment(customer.Id, business.Id),
            ManualMpesaConfig(business.Id, 25m));

        var created = await BookingTestBase.CreateAppointmentService(context)
            .CreateAppointmentAsync(customer.Id, "Customer", new CreateAppointmentRequest
            {
                BusinessId = business.Id,
                ServiceIds = new[] { service.Id },
                ScheduledAt = Slot()
            });

        Assert.True(created.Success, created.Error?.Message);

        context.ChangeTracker.Clear();
        var stored = await context.Appointments.SingleAsync();
        // The slot is held, but nothing is confirmed until the fee is verified.
        Assert.Equal(AppointmentStatuses.PendingPayment, stored.Status);
        Assert.True(AppointmentReservations.ReservesStaffTime(stored.Status));
        Assert.Equal(owner.Id, stored.StaffUserId);
    }

    [Fact]
    public async Task ASecondCustomer_CannotTakeASlotHeldByAnUnpaidBooking()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);

        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var first = BookingTestBase.CreateCustomer("first@test.com");
        var second = BookingTestBase.CreateCustomer("second@test.com");
        var service = BookingTestBase.CreateService(business.Id, "Cut", 60, 1000m);

        await BookingTestBase.SeedAsync(context, owner, business, first, second, service,
            Enrollment(first.Id, business.Id),
            Enrollment(second.Id, business.Id),
            ManualMpesaConfig(business.Id, 50m));

        var appointments = BookingTestBase.CreateAppointmentService(context);

        var held = await appointments.CreateAppointmentAsync(first.Id, "Customer", new CreateAppointmentRequest
        {
            BusinessId = business.Id,
            ServiceIds = new[] { service.Id },
            ScheduledAt = Slot()
        });
        Assert.True(held.Success, held.Error?.Message);

        var clash = await appointments.CreateAppointmentAsync(second.Id, "Customer", new CreateAppointmentRequest
        {
            BusinessId = business.Id,
            ServiceIds = new[] { service.Id },
            ScheduledAt = Slot()
        });

        Assert.False(clash.Success);
        Assert.Equal("STAFF_TIME_UNAVAILABLE", clash.Error?.Code);
    }
}