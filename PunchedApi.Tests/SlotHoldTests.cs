using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Temporary slot holds: the claim a customer takes while they pay a booking fee.
///
/// <para>Two things are being proved, and they are the two that cost a business money when they
/// fail. First, that a live hold makes its interval genuinely unbookable for everybody else — not
/// "unavailable to the page" but unavailable to the server. Second, that a lapsed hold releases the
/// interval by itself: no sweep has to run, no booking has to be cleaned up, and the time can be
/// sold again the moment the deadline passes.</para>
/// </summary>
public class SlotHoldTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly BookingHoldService _holds;
    private readonly AppointmentAvailabilityService _availability;
    private readonly Business _business;
    private readonly User _staff;
    private readonly ServiceCatalogItem _service;

    /// <summary>A slot a week out, so the lead-time rule never interferes.</summary>
    private static readonly DateTime Slot = DateTime.UtcNow.Date.AddDays(7).AddHours(10);
    private static readonly DateOnly SlotDay = DateOnly.FromDateTime(Slot);

    public SlotHoldTests()
    {
        _connection = BookingTestBase.CreateConnection();
        _context = BookingTestBase.CreateContext(_connection);

        var owner = BookingTestBase.CreateOwner($"hold-owner-{Guid.NewGuid():N}@test.com");
        _business = BookingTestBase.CreateBusiness(owner.Id, "Hold Studio");
        _staff = BookingTestBase.CreateStaff(_business.Id, $"hold-staff-{Guid.NewGuid():N}@test.com");
        _service = BookingTestBase.CreateService(_business.Id, "Fade", 60, 900m);

        BookingTestBase.SeedAsync(_context, owner, _business, _staff, _service,
            BookingTestBase.CreateAssignment(_business.Id, _staff.Id, _service.Id),
            BookingTestBase.CreateShift(_business.Id, _staff.Id, SlotDay, 8, 20)).GetAwaiter().GetResult();

        _availability = BookingTestBase.CreateAvailabilityService(_context);
        _holds = new BookingHoldService(
            _context,
            _availability,
            new BookingReadContextLoader(_context),
            TestHelpers.CreateLogger<BookingHoldService>());
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private CreateBookingHoldRequest Hold(string? key = null, string phone = "0712345678") => new()
    {
        ServiceIds = [_service.Id],
        StaffUserId = _staff.Id,
        ScheduledAt = Slot,
        IdempotencyKey = key,
        PaymentOption = StorefrontPaymentOptions.PayUpfront,
        FullName = "Wanjiru Kariuki",
        PhoneNumber = phone,
        Email = "wanjiru@example.test",
    };

    /// <summary>Another customer's attempt at a given instant.</summary>
    private CreateBookingHoldRequest Other(DateTime at) => new()
    {
        ServiceIds = [_service.Id],
        StaffUserId = _staff.Id,
        ScheduledAt = at,
        PaymentOption = StorefrontPaymentOptions.PayUpfront,
        FullName = "Second Customer",
        PhoneNumber = "0722000111",
        Email = "second@example.test",
    };

    [Fact]
    public async Task ALiveHold_MakesItsIntervalUnbookableForAnotherCustomer()
    {
        var first = await _holds.CreateAsync(_business.Id, Hold(), null, null);
        Assert.True(first.Success, first.Error?.Message);

        // The same interval, seen by a different customer: refused, server-side.
        var second = await _holds.CreateAsync(_business.Id, Other(Slot), null, null);

        Assert.False(second.Success);
        Assert.Equal(AppointmentConflicts.SlotUnavailable, second.Error?.Code);
    }

    [Fact]
    public async Task AHold_BlocksEveryOverlappingInterval_NotJustItsStart()
    {
        var first = await _holds.CreateAsync(_business.Id, Hold(), null, null);
        Assert.True(first.Success, first.Error?.Message);

        // The hold runs 10:00–11:00 (60 minutes). Each of these overlaps part of it.
        foreach (var attempt in new[]
                 {
                     Slot.AddMinutes(30),   // starts inside
                     Slot.AddMinutes(-30),  // ends inside
                     Slot,                  // identical
                 })
        {
            var overlapping = await _holds.CreateAsync(_business.Id, Other(attempt), null, null);

            Assert.False(overlapping.Success);
            Assert.Equal(AppointmentConflicts.SlotUnavailable, overlapping.Error?.Code);
        }

        // …while the interval immediately after it is still free: half-open overlap keeps
        // back-to-back bookings legal.
        var backToBack = await _holds.CreateAsync(_business.Id, Other(Slot.AddHours(1)), null, null);
        Assert.True(backToBack.Success, backToBack.Error?.Message);
    }

    [Fact]
    public async Task TheAvailabilityGrid_ReportsAHeldSlotAsUnavailable()
    {
        var hold = await _holds.CreateAsync(_business.Id, Hold(), null, null);
        Assert.True(hold.Success, hold.Error?.Message);

        var calendar = await _availability.GetAvailabilityCalendarAsync(
            _business.Id, [_service.Id], _staff.Id, SlotDay, SlotDay);

        Assert.True(calendar.Success, calendar.Error?.Message);
        var slot = calendar.Data!.Days
            .SelectMany(day => day.Slots)
            .Single(entry => entry.StartAtUtc == Slot);

        Assert.False(slot.IsAvailable);
        Assert.Equal(SlotStatuses.Booked, slot.Status);
    }

    [Fact]
    public async Task AnExpiredHold_ReleasesTheSlotWithoutAnySweep()
    {
        // A hold whose deadline has already passed: written directly, because the service would
        // (correctly) refuse to create one.
        var expired = new AppointmentSlotHold
        {
            Id = Guid.NewGuid(),
            BusinessId = _business.Id,
            StaffUserId = _staff.Id,
            StartsAtUtc = Slot,
            EndsAtUtc = Slot.AddHours(1),
            ServiceIdsJson = System.Text.Json.JsonSerializer.Serialize(new[] { _service.Id }),
            DurationMinutes = 60,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1),
            Status = AppointmentSlotHoldStatuses.Active,
            CreatedAt = DateTime.UtcNow.AddMinutes(-11),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-11),
        };
        await _context.AppointmentSlotHolds.AddAsync(expired);
        await _context.SaveChangesAsync();

        // The grid already ignores it: the deadline is part of the read's predicate, so nothing has
        // to expire the row first.
        var calendar = await _availability.GetAvailabilityCalendarAsync(
            _business.Id, [_service.Id], _staff.Id, SlotDay, SlotDay);
        Assert.True(calendar.Success, calendar.Error?.Message);
        Assert.True(calendar.Data!.Days
            .SelectMany(day => day.Slots)
            .Single(entry => entry.StartAtUtc == Slot)
            .IsAvailable);

        // And another customer can claim it.
        var reclaimed = await _holds.CreateAsync(_business.Id, Other(Slot), null, null);
        Assert.True(reclaimed.Success, reclaimed.Error?.Message);

        // The housekeeping sweep then tidies the status, which is all it is for.
        Assert.Equal(1, await _holds.ExpireLapsedAsync());
    }

    [Fact]
    public async Task ARepeatedIntent_ResumesTheHoldItAlreadyHas()
    {
        var first = await _holds.CreateAsync(_business.Id, Hold(key: "intent-1"), null, null);
        Assert.True(first.Success, first.Error?.Message);

        var again = await _holds.CreateAsync(_business.Id, Hold(key: "intent-1"), null, null);

        Assert.True(again.Success, again.Error?.Message);
        Assert.Equal(first.Data!.HoldId, again.Data!.HoldId);
        Assert.Equal(1, await _context.AppointmentSlotHolds.CountAsync());
    }

    [Fact]
    public async Task ReleasingAHold_FreesTheSlotImmediately()
    {
        var hold = await _holds.CreateAsync(_business.Id, Hold(), null, null);
        Assert.True(hold.Success, hold.Error?.Message);

        var released = await _holds.ReleaseAsync(_business.Id, hold.Data!.HoldId);
        Assert.True(released.Success, released.Error?.Message);
        Assert.Equal(AppointmentSlotHoldStatuses.Released, released.Data!.Status);

        var reclaimed = await _holds.CreateAsync(_business.Id, Other(Slot), null, null);
        Assert.True(reclaimed.Success, reclaimed.Error?.Message);
    }

    [Fact]
    public async Task AHoldForAnotherBusiness_IsNotFound()
    {
        var hold = await _holds.CreateAsync(_business.Id, Hold(), null, null);
        Assert.True(hold.Success, hold.Error?.Message);

        var otherOwner = BookingTestBase.CreateOwner($"other-hold-owner-{Guid.NewGuid():N}@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other Hold Studio");
        await BookingTestBase.SeedAsync(_context, otherOwner, otherBusiness);

        // The hold id is not a capability outside the business it belongs to.
        var read = await _holds.GetAsync(otherBusiness.Id, hold.Data!.HoldId);
        Assert.False(read.Success);
        Assert.Equal("NOT_FOUND", read.Error?.Code);
    }
}
