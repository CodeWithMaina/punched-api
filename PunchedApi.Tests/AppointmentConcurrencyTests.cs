using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// <b>Concurrent booking.</b> Two customers can want the same staff member at the
/// same time; the platform must let exactly one of them have it.
///
/// <para>These tests run against PostgreSQL because the guarantee is enforced in the
/// database: the write paths take a transaction-scoped advisory lock keyed on
/// (business, staff member) and re-check availability inside it, with the
/// <c>appointments_no_staff_overlap</c> exclusion constraint as the backstop. A
/// check-then-insert without either of those lets both requests through, which is
/// precisely the bug this covers.</para>
/// </summary>
public class AppointmentConcurrencyTests : IClassFixture<AppointmentPostgresFixture>
{
    private static readonly TestClock Clock = TestClock.AtUtc(2026, 3, 2, 6, 0);
    private static readonly DateOnly Day = new(2026, 3, 2);

    private static DateTime At(int hour, int minute = 0) =>
        new(2026, 3, 2, hour, minute, 0, DateTimeKind.Utc);

    private readonly AppointmentPostgresFixture _postgres;

    public AppointmentConcurrencyTests(AppointmentPostgresFixture postgres) => _postgres = postgres;

    private sealed record Fixture(
        Guid BusinessId,
        Guid OwnerId,
        Guid StaffId,
        Guid OtherStaffId,
        Guid CustomerId,
        Guid OtherCustomerId,
        Guid HourServiceId);

    /// <summary>
    /// Seeds one business with two staff members, a one-hour service and two enrolled
    /// customers — the minimum a real booking needs.
    /// </summary>
    private async Task<Fixture> SeedAsync()
    {
        await using var context = _postgres.CreateContext();

        var owner = BookingTestBase.CreateOwner($"conc-owner-{Guid.NewGuid():N}@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id, $"Concurrency Salon {Guid.NewGuid():N}");
        var staff = BookingTestBase.CreateStaff(business.Id, $"conc-staff-{Guid.NewGuid():N}@test.com");
        var otherStaff = BookingTestBase.CreateStaff(business.Id, $"conc-staff2-{Guid.NewGuid():N}@test.com");
        var customer = BookingTestBase.CreateCustomer($"conc-customer-{Guid.NewGuid():N}@test.com");
        var otherCustomer = BookingTestBase.CreateCustomer($"conc-customer2-{Guid.NewGuid():N}@test.com");
        var service = BookingTestBase.CreateService(business.Id, "Cut", 60, 1000m);

        await BookingTestBase.SeedAsync(context,
            owner, business, staff, otherStaff, customer, otherCustomer, service,
            BookingTestBase.CreateAssignment(business.Id, staff.Id, service.Id),
            BookingTestBase.CreateAssignment(business.Id, otherStaff.Id, service.Id),
            Enrolment(customer.Id, business.Id),
            Enrolment(otherCustomer.Id, business.Id));

        await BookingTestBase.SeedAsync(context,
            new[]
            {
                BookingTestBase.CreateShift(business.Id, staff.Id, Day, 8, 20),
                BookingTestBase.CreateShift(business.Id, otherStaff.Id, Day, 8, 20)
            });

        return new Fixture(
            business.Id, owner.Id, staff.Id, otherStaff.Id, customer.Id, otherCustomer.Id, service.Id);
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
    /// One booking attempt on its own context — exactly what two simultaneous HTTP
    /// requests look like to the API.
    /// </summary>
    private async Task<ApiResponse<AppointmentResponse>> AttemptAsync(
        Fixture fixture, DateTime start, Guid staffId, Guid customerId)
    {
        await using var context = _postgres.CreateContext();
        var service = BusinessOperationsTestBase.CreateAppointmentService(
            context, commissions: null, timeProvider: Clock);

        return await service.CreateAppointmentOnBehalfAsync(
            fixture.OwnerId,
            "Business",
            new CreateAppointmentOnBehalfRequest
            {
                BusinessId = fixture.BusinessId,
                ServiceIds = new[] { fixture.HourServiceId },
                StaffUserId = staffId,
                ScheduledAt = start,
                CustomerId = customerId
            });
    }

    private async Task<int> CountAsync(Guid businessId, DateTime start)
    {
        await using var context = _postgres.CreateContext();
        return await context.Appointments
            .CountAsync(a => a.BusinessId == businessId && a.ScheduledAt == start);
    }

    [RequiresDockerFact]
    public async Task SimultaneousBookingsForTheSameInterval_ProduceExactlyOneReservation()
    {
        var fixture = await SeedAsync();
        var start = At(13);

        // Five customers, one staff member, one interval — all at once.
        var attempts = Enumerable.Range(0, 5)
            .Select(_ => AttemptAsync(fixture, start, fixture.StaffId, fixture.CustomerId))
            .ToArray();

        var results = await Task.WhenAll(attempts);

        var winners = results.Where(r => r.Success).ToList();
        var losers = results.Where(r => !r.Success).ToList();

        Assert.Single(winners);
        Assert.Equal(4, losers.Count);

        // Every loser is told the interval is taken — a conflict, not a generic failure.
        Assert.All(losers, loser => Assert.Equal(AppointmentConflicts.StaffTimeUnavailable, loser.Error?.Code));

        // And the database holds exactly one reservation for that interval.
        Assert.Equal(1, await CountAsync(fixture.BusinessId, start));
    }

    [RequiresDockerFact]
    public async Task SimultaneousOverlappingBookings_ProduceExactlyOneReservation()
    {
        var fixture = await SeedAsync();

        // 13:00–14:00 and 13:30–14:30 overlap, so only one may survive.
        var results = await Task.WhenAll(
            AttemptAsync(fixture, At(13), fixture.StaffId, fixture.CustomerId),
            AttemptAsync(fixture, At(13, 30), fixture.StaffId, fixture.OtherCustomerId));

        Assert.Single(results.Where(r => r.Success));
        Assert.Equal(
            AppointmentConflicts.StaffTimeUnavailable,
            results.Single(r => !r.Success).Error?.Code);

        await using var context = _postgres.CreateContext();
        var booked = await context.Appointments
            .Where(a => a.BusinessId == fixture.BusinessId && a.StaffUserId == fixture.StaffId)
            .ToListAsync();
        Assert.Single(booked);
    }

    [RequiresDockerFact]
    public async Task SimultaneousNonOverlappingBookings_BothSucceed()
    {
        var fixture = await SeedAsync();

        // Back-to-back intervals are legitimate, so the lock must not reject them.
        var results = await Task.WhenAll(
            AttemptAsync(fixture, At(13), fixture.StaffId, fixture.CustomerId),
            AttemptAsync(fixture, At(14), fixture.StaffId, fixture.OtherCustomerId));

        Assert.All(results, result => Assert.True(result.Success, result.Error?.Message));
        Assert.Equal(2, await CountAsync(fixture.BusinessId, At(13)) + await CountAsync(fixture.BusinessId, At(14)));
    }

    [RequiresDockerFact]
    public async Task SimultaneousBookingsForDifferentStaff_BothSucceed()
    {
        var fixture = await SeedAsync();

        // Different diaries: the staff-scoped lock must not serialise them into a conflict.
        var results = await Task.WhenAll(
            AttemptAsync(fixture, At(13), fixture.StaffId, fixture.CustomerId),
            AttemptAsync(fixture, At(13), fixture.OtherStaffId, fixture.OtherCustomerId));

        Assert.All(results, result => Assert.True(result.Success, result.Error?.Message));
        Assert.Equal(2, await CountAsync(fixture.BusinessId, At(13)));
    }

    [RequiresDockerFact]
    public async Task TheDatabaseConstraint_RefusesAnOverlapWrittenBehindTheService()
    {
        var fixture = await SeedAsync();

        var first = await AttemptAsync(fixture, At(13), fixture.StaffId, fixture.CustomerId);
        Assert.True(first.Success, first.Error?.Message);

        // A row inserted straight into the table (bypassing every service check) must
        // still be refused: this is the backstop that makes the guarantee absolute.
        await using var context = _postgres.CreateContext();
        context.Appointments.Add(new Appointment
        {
            Id = Guid.NewGuid(),
            BusinessId = fixture.BusinessId,
            CustomerId = fixture.OtherCustomerId,
            StaffUserId = fixture.StaffId,
            ScheduledAt = At(13, 30),
            EndAt = At(14, 30),
            Status = AppointmentStatuses.Booked,
            CreatedAt = Clock.GetUtcNow().UtcDateTime
        });

        var error = await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.True(AppointmentConflicts.IsSlotConflict(error), $"Unexpected error: {error.Message}");
    }

    [RequiresDockerFact]
    public async Task TheDatabaseConstraint_AllowsBackToBackAndReleasedIntervals()
    {
        var fixture = await SeedAsync();

        // Back-to-back rows are legal…
        await using (var context = _postgres.CreateContext())
        {
            context.Appointments.AddRange(
                NewRow(fixture, At(13), At(14), AppointmentStatuses.Booked),
                NewRow(fixture, At(14), At(15), AppointmentStatuses.Confirmed));
            await context.SaveChangesAsync();
        }

        // …and a terminal row does not hold its interval, so a live booking may take it.
        await using (var context = _postgres.CreateContext())
        {
            context.Appointments.AddRange(
                NewRow(fixture, At(16), At(17), AppointmentStatuses.Completed),
                NewRow(fixture, At(16), At(17), AppointmentStatuses.Booked));
            await context.SaveChangesAsync();
        }

        await using var verify = _postgres.CreateContext();
        Assert.Equal(4, await verify.Appointments.CountAsync(a => a.BusinessId == fixture.BusinessId));
    }

    private static Appointment NewRow(Fixture fixture, DateTime start, DateTime end, string status) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = fixture.BusinessId,
        CustomerId = fixture.CustomerId,
        StaffUserId = fixture.StaffId,
        ScheduledAt = start,
        EndAt = end,
        Status = status,
        CreatedAt = Clock.GetUtcNow().UtcDateTime
    };
}

