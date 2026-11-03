using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// <b>Staff time blocking.</b> A booking reserves its assigned staff member for the
/// whole appointment interval — start to end, computed from the services actually
/// booked — so no other client can take an overlapping slot.
///
/// <para>The clock is pinned (<see cref="TestClock"/>) and every date and shift is
/// fixed, so these assertions hold at any hour of the day and in any timezone.</para>
///
/// <para>Behaviour that depends on database-level locking or the exclusion
/// constraint lives in <see cref="AppointmentConcurrencyTests"/>, which needs
/// PostgreSQL.</para>
/// </summary>
public class AppointmentStaffBlockingTests
{
    private static readonly TestClock Clock = TestClock.AtUtc(2026, 3, 2, 6, 0);

    /// <summary>Business-local day the fixtures book on (the test business runs on UTC).</summary>
    private static readonly DateOnly Day = new(2026, 3, 2);
    private static readonly DateOnly NextDay = new(2026, 3, 3);

    private static DateTime At(int hour, int minute = 0) =>
        new(2026, 3, 2, hour, minute, 0, DateTimeKind.Utc);

    private static DateTime AtNextDay(int hour, int minute = 0) =>
        new(2026, 3, 3, hour, minute, 0, DateTimeKind.Utc);

    private sealed class Env
    {
        public ApplicationDbContext Context = null!;
        public AppointmentService Appointments = null!;
        public AppointmentAvailabilityService Availability = null!;
        public Business Business = null!;
        public Business OtherBusiness = null!;
        public User Owner = null!;
        public User OtherOwner = null!;
        public User Staff = null!;
        public User OtherStaff = null!;
        public User Customer = null!;
        public User OtherCustomer = null!;
        public ServiceCatalogItem ShortService = null!;
        public ServiceCatalogItem HourService = null!;
        public ServiceCatalogItem LongService = null!;
        public ServiceCatalogItem OtherBusinessService = null!;
    }

    private static CustomerBusinessEnrollment Enrolment(Guid customerId, Guid businessId) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = customerId,
        BusinessId = businessId,
        Status = CustomerBusinessEnrollmentStatus.Active,
        Source = "booking",
        EnrolledAt = Clock.GetUtcNow().UtcDateTime,
        UpdatedAt = Clock.GetUtcNow().UtcDateTime
    };

    /// <summary>
    /// One business with two staff members and 30/60/120-minute services, plus a
    /// second business with its own staff and service — the isolation fixtures.
    /// </summary>
    private static async Task<Env> CreateEnvAsync(SqliteConnection connection)
    {
        var context = BookingTestBase.CreateContext(connection);
        var commissions = BusinessOperationsTestBase.CreateCommissionService(context);
        var appointments = BusinessOperationsTestBase.CreateAppointmentService(context, commissions, Clock);

        var owner = BookingTestBase.CreateOwner("blocking-owner@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Blocking Salon");
        var staff = BookingTestBase.CreateStaff(business.Id, "blocking-staff@test.com");
        var otherStaff = BookingTestBase.CreateStaff(business.Id, "blocking-staff2@test.com");
        var customer = BookingTestBase.CreateCustomer("blocking-customer@test.com");
        var otherCustomer = BookingTestBase.CreateCustomer("blocking-customer2@test.com");

        var shortService = BookingTestBase.CreateService(business.Id, "Trim", 30, 500m);
        var hourService = BookingTestBase.CreateService(business.Id, "Cut", 60, 1000m);
        var longService = BookingTestBase.CreateService(business.Id, "Colour", 120, 4000m);

        var otherOwner = BookingTestBase.CreateOwner("blocking-other-owner@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other Salon");
        var otherBusinessService = BookingTestBase.CreateService(otherBusiness.Id, "Other Cut", 60, 1000m);

        await BookingTestBase.SeedAsync(context,
            owner, business, staff, otherStaff, customer, otherCustomer,
            shortService, hourService, longService,
            otherOwner, otherBusiness, otherBusinessService,
            BookingTestBase.CreateAssignment(business.Id, staff.Id, shortService.Id),
            BookingTestBase.CreateAssignment(business.Id, staff.Id, hourService.Id),
            BookingTestBase.CreateAssignment(business.Id, staff.Id, longService.Id),
            BookingTestBase.CreateAssignment(business.Id, otherStaff.Id, hourService.Id),
            BookingTestBase.CreateAssignment(otherBusiness.Id, otherOwner.Id, otherBusinessService.Id),
            Enrolment(customer.Id, business.Id),
            Enrolment(otherCustomer.Id, business.Id),
            Enrolment(customer.Id, otherBusiness.Id));

        // 08:00–20:00 rota for both staff members on both days.
        await BookingTestBase.SeedAsync(context,
            new[]
            {
                BookingTestBase.CreateShift(business.Id, staff.Id, Day, 8, 20),
                BookingTestBase.CreateShift(business.Id, staff.Id, NextDay, 8, 20),
                BookingTestBase.CreateShift(business.Id, otherStaff.Id, Day, 8, 20),
                BookingTestBase.CreateShift(otherBusiness.Id, otherOwner.Id, Day, 8, 20)
            });

        await BusinessOperationsTestBase.ProvisionAsync(context, business.Id);

        return new Env
        {
            Context = context,
            Appointments = appointments,
            Availability = BookingTestBase.CreateAvailabilityService(context, Clock),
            Business = business,
            OtherBusiness = otherBusiness,
            Owner = owner,
            OtherOwner = otherOwner,
            Staff = staff,
            OtherStaff = otherStaff,
            Customer = customer,
            OtherCustomer = otherCustomer,
            ShortService = shortService,
            HourService = hourService,
            LongService = longService,
            OtherBusinessService = otherBusinessService
        };
    }

    /// <summary>Books on the owner's behalf (the walk-in path) and returns the response.</summary>
    private static Task<ApiResponse<AppointmentResponse>> BookAsync(
        Env env,
        DateTime start,
        Guid? staffId,
        Guid? serviceId = null,
        Guid? customerId = null)
        => env.Appointments.CreateAppointmentOnBehalfAsync(
            env.Owner.Id,
            "Business",
            new CreateAppointmentOnBehalfRequest
            {
                BusinessId = env.Business.Id,
                ServiceIds = new[] { serviceId ?? env.LongService.Id },
                StaffUserId = staffId,
                ScheduledAt = start,
                CustomerId = customerId ?? env.Customer.Id
            });

    /// <summary>The pre-persist availability gate, asked for a concrete interval.</summary>
    private static Task<SlotResolution> ResolveAsync(
        Env env, DateTime start, int minutes, Guid? staffId, Guid? excludeAppointmentId = null)
        => env.Availability.ResolveBookingStaffAsync(
            env.Business.Id,
            new[] { env.LongService.Id },
            staffId,
            start,
            minutes,
            excludeAppointmentId,
            enforceLeadTime: false);

    // ═══════════════════════════════════════════════════════════════
    //  BASIC BLOCKING
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ATwoHourBooking_BlocksItsWholeTwoHourInterval()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        // 13:00–15:00 for a 120-minute service.
        var booked = await BookAsync(env, At(13), env.Staff.Id);
        Assert.True(booked.Success, booked.Error?.Message);
        Assert.Equal(At(13), booked.Data!.ScheduledAt);
        Assert.Equal(At(15), booked.Data.EndAt);

        // Every start that would overlap the two hours is refused.
        foreach (var offset in new[] { 0, 15, 30, 45, 60, 90, 105 })
        {
            var overlapping = await ResolveAsync(env, At(13).AddMinutes(offset), 120, env.Staff.Id);
            Assert.False(overlapping.Ok);
            Assert.Equal(AppointmentConflicts.StaffTimeUnavailable, overlapping.ErrorCode);
        }

        // 15:00 is the first bookable minute again (back-to-back is legal).
        var after = await ResolveAsync(env, At(15), 120, env.Staff.Id);
        Assert.True(after.Ok, after.ErrorMessage);
    }

    [Fact]
    public async Task ThirtyAndSixtyMinuteBookings_BlockExactlyTheirOwnInterval()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var thirty = await BookAsync(env, At(10), env.Staff.Id, env.ShortService.Id);
        Assert.True(thirty.Success, thirty.Error?.Message);
        Assert.Equal(At(10, 30), thirty.Data!.EndAt);

        // 10:30 starts exactly when the 30-minute booking ends → allowed.
        var sixty = await BookAsync(env, At(10, 30), env.Staff.Id, env.HourService.Id);
        Assert.True(sixty.Success, sixty.Error?.Message);
        Assert.Equal(At(11, 30), sixty.Data!.EndAt);

        // 10:45 sits inside the hour booking → refused.
        var inside = await ResolveAsync(env, At(10, 45), 30, env.Staff.Id);
        Assert.False(inside.Ok);
        Assert.Equal(AppointmentConflicts.StaffTimeUnavailable, inside.ErrorCode);

        // 11:30 is free again.
        Assert.True((await ResolveAsync(env, At(11, 30), 30, env.Staff.Id)).Ok);
    }

    [Fact]
    public async Task BackToBackBookings_AreAllowedAtBothEnds()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var first = await BookAsync(env, At(10), env.Staff.Id, env.HourService.Id);
        Assert.True(first.Success, first.Error?.Message);

        // Starts exactly when the previous ends.
        var after = await BookAsync(env, At(11), env.Staff.Id, env.HourService.Id);
        Assert.True(after.Success, after.Error?.Message);

        // Ends exactly when the following starts (booked backwards, then forwards).
        var before = await BookAsync(env, At(9), env.Staff.Id, env.HourService.Id);
        Assert.True(before.Success, before.Error?.Message);

        Assert.Equal(3, context.Appointments.Count(a => a.StaffUserId == env.Staff.Id));
    }

    [Fact]
    public async Task ADifferentStaffMember_AcceptsTheSameTime()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var first = await BookAsync(env, At(13), env.Staff.Id, env.HourService.Id);
        Assert.True(first.Success, first.Error?.Message);

        // Staff B is a different diary: the same hour is theirs to take.
        var second = await BookAsync(env, At(13), env.OtherStaff.Id, env.HourService.Id, env.OtherCustomer.Id);
        Assert.True(second.Success, second.Error?.Message);
        Assert.Equal(env.OtherStaff.Id, second.Data!.StaffUserId);
    }

    [Fact]
    public async Task AnotherBusiness_IsUnaffectedByABooking()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var booking = await BookAsync(env, At(13), env.Staff.Id, env.HourService.Id);
        Assert.True(booking.Success, booking.Error?.Message);

        // The other business books the same wall-clock hour with its own staff.
        var other = await env.Appointments.CreateAppointmentOnBehalfAsync(
            env.OtherOwner.Id,
            "Business",
            new CreateAppointmentOnBehalfRequest
            {
                BusinessId = env.OtherBusiness.Id,
                ServiceIds = new[] { env.OtherBusinessService.Id },
                StaffUserId = env.OtherOwner.Id,
                ScheduledAt = At(13),
                CustomerId = env.Customer.Id
            });

        Assert.True(other.Success, other.Error?.Message);

        // Availability for the other business sees only its own reservation.
        var calendar = await env.Availability.GetAvailabilityCalendarAsync(
            env.OtherBusiness.Id, new[] { env.OtherBusinessService.Id }, null, Day, Day);
        Assert.True(calendar.Success, calendar.Error?.Message);
        Assert.Contains(
            calendar.Data!.Days.SelectMany(d => d.Slots),
            slot => slot.LocalTime == "13:00" && slot.Status == SlotStatuses.Booked);
    }



    // ═══════════════════════════════════════════════════════════════
    //  OVERLAP SHAPES
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Every geometric way a request can collide with an existing 13:00–15:00
    /// booking. The half-open rule catches all of them and lets only genuine
    /// back-to-back neighbours through.
    /// </summary>
    [Theory]
    [InlineData(0, 120, "exactly the same interval")]
    [InlineData(60, 120, "starts inside, ends after")]
    [InlineData(-60, 120, "starts before, ends inside")]
    [InlineData(-60, 240, "completely contains the existing booking")]
    [InlineData(30, 30, "completely inside the existing booking")]
    [InlineData(90, 30, "ends exactly at the existing end")]
    [InlineData(-30, 45, "ends just after the existing start")]
    public async Task OverlappingRequests_AreRefused(int offsetMinutes, int minutes, string shape)
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var existing = await BookAsync(env, At(13), env.Staff.Id);
        Assert.True(existing.Success, existing.Error?.Message);

        var resolution = await ResolveAsync(env, At(13).AddMinutes(offsetMinutes), minutes, env.Staff.Id);
        Assert.False(resolution.Ok, $"Expected a conflict: {shape}");
        Assert.Equal(AppointmentConflicts.StaffTimeUnavailable, resolution.ErrorCode);
    }

    /// <summary>
    /// The pre-persist gate is not the only line of defence: the write path refuses
    /// the same interval with the same conflict code.
    /// </summary>
    [Fact]
    public async Task TheWritePath_RefusesAnOverlap()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var existing = await BookAsync(env, At(13), env.Staff.Id);
        Assert.True(existing.Success, existing.Error?.Message);

        // 14:00–15:00 sits inside the 13:00–15:00 booking.
        var overlapping = await BookAsync(env, At(14), env.Staff.Id, env.HourService.Id, env.OtherCustomer.Id);
        Assert.False(overlapping.Success);
        Assert.Equal(AppointmentConflicts.StaffTimeUnavailable, overlapping.Error?.Code);

        // Nothing was written for the refused attempt.
        Assert.Equal(1, context.Appointments.Count(a => a.StaffUserId == env.Staff.Id));
    }

    [Fact]
    public async Task MultipleExistingBookings_BlockDifferentPortionsOfTheRequest()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        // 10:00–11:00 and 13:00–14:00, then a 10:30–13:30 request spanning both.
        Assert.True((await BookAsync(env, At(10), env.Staff.Id, env.HourService.Id)).Success);
        Assert.True((await BookAsync(env, At(13), env.Staff.Id, env.HourService.Id)).Success);

        var spanning = await ResolveAsync(env, At(10, 30), 180, env.Staff.Id);
        Assert.False(spanning.Ok);
        Assert.Equal(AppointmentConflicts.StaffTimeUnavailable, spanning.ErrorCode);

        // A window that fits in the gap between them is still bookable.
        var gap = await ResolveAsync(env, At(11), 120, env.Staff.Id);
        Assert.True(gap.Ok, gap.ErrorMessage);
    }


    // ═══════════════════════════════════════════════════════════════
    //  LIFECYCLE: WHAT RESERVES AND WHAT RELEASES
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Cancelling_ReleasesTheInterval()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var booking = await BookAsync(env, At(13), env.Staff.Id, env.HourService.Id);
        Assert.True(booking.Success, booking.Error?.Message);
        Assert.False((await ResolveAsync(env, At(13), 60, env.Staff.Id)).Ok);

        var cancelled = await env.Appointments.CancelAsync(
            env.Owner.Id, "Business", booking.Data!.Id, new CancelAppointmentRequest { Note = "released" });
        Assert.True(cancelled.Success, cancelled.Error?.Message);
        Assert.Equal(AppointmentStatuses.Cancelled, cancelled.Data!.Status);

        // The freed hour is bookable again, immediately.
        var rebook = await BookAsync(env, At(13), env.Staff.Id, env.HourService.Id, env.OtherCustomer.Id);
        Assert.True(rebook.Success, rebook.Error?.Message);
    }

    [Fact]
    public async Task TerminalStatuses_DoNotBlockTheirInterval()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        // A future-dated appointment marked completed, and another marked no-show:
        // both are history, so neither may keep holding the staff member's time.
        var completed = await BookAsync(env, At(13), env.Staff.Id, env.HourService.Id);
        Assert.True(completed.Success, completed.Error?.Message);
        // The lifecycle requires confirmation before completion.
        Assert.True((await env.Appointments.ConfirmAsync(env.Owner.Id, "Business", completed.Data!.Id)).Success);
        Assert.True((await env.Appointments.CompleteAsync(env.Owner.Id, "Business", completed.Data!.Id)).Success);

        var noShow = await BookAsync(env, At(15), env.Staff.Id, env.HourService.Id);
        Assert.True(noShow.Success, noShow.Error?.Message);
        Assert.True((await env.Appointments.MarkNoShowAsync(env.Owner.Id, "Business", noShow.Data!.Id)).Success);

        Assert.True((await ResolveAsync(env, At(13), 60, env.Staff.Id)).Ok);
        Assert.True((await ResolveAsync(env, At(15), 60, env.Staff.Id)).Ok);
    }

    [Fact]
    public async Task ABookedAppointment_ReservesTimeBeforeItIsConfirmed()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var booking = await BookAsync(env, At(13), env.Staff.Id, env.HourService.Id);
        Assert.True(booking.Success, booking.Error?.Message);
        Assert.Equal(AppointmentStatuses.Booked, booking.Data!.Status);

        // `booked` is the workflow's pending state and is a real reservation.
        Assert.True(AppointmentReservations.ReservesStaffTime(AppointmentStatuses.Booked));
        Assert.False((await ResolveAsync(env, At(13), 60, env.Staff.Id)).Ok);

        var confirmed = await env.Appointments.ConfirmAsync(env.Owner.Id, "Business", booking.Data.Id);
        Assert.True(confirmed.Success, confirmed.Error?.Message);
        Assert.True(AppointmentReservations.ReservesStaffTime(AppointmentStatuses.Confirmed));
        Assert.False((await ResolveAsync(env, At(13), 60, env.Staff.Id)).Ok);
    }


    [Fact]
    public async Task Rescheduling_ReleasesTheOldIntervalAndReservesTheNewOne()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var booking = await BookAsync(env, At(13), env.Staff.Id, env.HourService.Id);
        Assert.True(booking.Success, booking.Error?.Message);

        var moved = await env.Appointments.RescheduleAsync(
            env.Owner.Id, "Business", booking.Data!.Id,
            new RescheduleAppointmentRequest { ScheduledAt = AtNextDay(13) });
        Assert.True(moved.Success, moved.Error?.Message);
        Assert.Equal(AtNextDay(13), moved.Data!.ScheduledAt);

        // Old interval free, new interval taken.
        Assert.True((await ResolveAsync(env, At(13), 60, env.Staff.Id)).Ok);
        Assert.False((await ResolveAsync(env, AtNextDay(13), 60, env.Staff.Id)).Ok);

        // The rescheduled appointment never conflicts with itself.
        var again = await env.Appointments.RescheduleAsync(
            env.Owner.Id, "Business", booking.Data.Id,
            new RescheduleAppointmentRequest { ScheduledAt = AtNextDay(14) });
        Assert.True(again.Success, again.Error?.Message);
    }

    [Fact]
    public async Task AFailedReschedule_LeavesTheOriginalBookingUntouched()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var mine = await BookAsync(env, At(13), env.Staff.Id, env.HourService.Id);
        Assert.True(mine.Success, mine.Error?.Message);

        // Somebody else already holds 15:00–16:00 for the same staff member.
        var theirs = await BookAsync(env, At(15), env.Staff.Id, env.HourService.Id, env.OtherCustomer.Id);
        Assert.True(theirs.Success, theirs.Error?.Message);

        var failed = await env.Appointments.RescheduleAsync(
            env.Owner.Id, "Business", mine.Data!.Id,
            new RescheduleAppointmentRequest { ScheduledAt = At(15) });
        Assert.False(failed.Success);
        Assert.Equal(AppointmentConflicts.StaffTimeUnavailable, failed.Error?.Code);

        // The original reservation stands, untouched.
        var stored = await context.Appointments.AsNoTracking().SingleAsync(a => a.Id == mine.Data.Id);
        Assert.Equal(At(13), stored.ScheduledAt);
        Assert.Equal(At(14), stored.EndAt);
        Assert.Equal(AppointmentStatuses.Booked, stored.Status);
        Assert.False((await ResolveAsync(env, At(13), 60, env.Staff.Id)).Ok);
    }

    [Fact]
    public async Task TheAvailabilityCalendar_ReflectsABookingImmediately()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var booked = await BookAsync(env, At(13), env.Staff.Id);
        Assert.True(booked.Success, booked.Error?.Message);

        var calendar = await env.Availability.GetAvailabilityCalendarAsync(
            env.Business.Id, new[] { env.LongService.Id }, env.Staff.Id, Day, Day);
        Assert.True(calendar.Success, calendar.Error?.Message);

        var slots = calendar.Data!.Days.Single(d => d.Date == "2026-03-02").Slots;
        var at13 = slots.Single(s => s.LocalTime == "13:00");
        Assert.Equal(SlotStatuses.Booked, at13.Status);
        Assert.False(at13.IsAvailable);

        // 15:00 — the first free minute after the two-hour booking — is available.
        var at15 = slots.Single(s => s.LocalTime == "15:00");
        Assert.Equal(SlotStatuses.Available, at15.Status);
        Assert.True(at15.IsAvailable);
    }

}
