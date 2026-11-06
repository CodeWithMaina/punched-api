using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// The appointment state machine: which transitions exist, which are refused,
/// and the guarantee that repeating a transition never repeats a side effect.
/// </summary>
public partial class AppointmentLifecycleTests : IAsyncLifetime
{
    /// <summary>
    /// The instant the fixture pins "now" to.
    ///
    /// <para>Every booking rule here is time-relative (lead time, "the new time must
    /// be in the future", shift windows, proposal expiry), so a fixture built on
    /// <c>DateTime.UtcNow</c> only exercises them at certain hours — which is exactly
    /// why four reschedule tests used to fail in the evening: they booked
    /// <c>UtcNow + 2h</c> against an 08:00–20:00 shift, and from about 18:00 that
    /// landed outside it. The clock is now fixed and the appointment times are fixed
    /// with it, so the rules are tested identically at any hour, in any timezone, on
    /// any day.</para>
    /// </summary>
    private static readonly TestClock Clock = TestClock.AtUtc(2026, 3, 2, 6, 0);

    /// <summary>Booked start: 10:00 UTC — inside the 08:00–20:00 shift.</summary>
    private static readonly DateTime BookedStart = new(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The staff rota the fixture seeds: 08:00–20:00 on the booked day plus the days a
    /// reschedule proposal may move into.
    /// </summary>
    private static readonly DateOnly[] RosteredDays =
    [
        new(2026, 3, 2),
        new(2026, 3, 3),
        new(2026, 3, 4),
        new(2026, 3, 5)
    ];


    private Microsoft.Data.Sqlite.SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;
    private AppointmentService _appointments = null!;
    private ICommissionService _commissions = null!;

    private User _owner = null!;
    private Business _business = null!;
    private User _staff = null!;
    private User _customer = null!;
    private ServiceCatalogItem _service = null!;

    public async Task InitializeAsync()
    {
        _connection = BookingTestBase.CreateConnection();
        _context = BookingTestBase.CreateContext(_connection);
        _commissions = BusinessOperationsTestBase.CreateCommissionService(_context);
        _appointments = BusinessOperationsTestBase.CreateAppointmentService(_context, _commissions, Clock);

        _owner = BookingTestBase.CreateOwner("lifecycle-owner@test.com");
        _business = BookingTestBase.CreateBusiness(_owner.Id, "Lifecycle Salon");
        _staff = BookingTestBase.CreateStaff(_business.Id, "lifecycle-staff@test.com");
        _customer = BookingTestBase.CreateCustomer("lifecycle-customer@test.com");
        _service = BookingTestBase.CreateService(_business.Id, "Cut", 60, 1000m);

        await BookingTestBase.SeedAsync(_context, _owner, _business, _staff, _customer, _service,
            BookingTestBase.CreateAssignment(_business.Id, _staff.Id, _service.Id));

        // The rota is seeded as its own call so the array is the params array rather
        // than a single (unusable) entity element.
        await BookingTestBase.SeedAsync(_context,
            RosteredDays
                .Select(day => BookingTestBase.CreateShift(_business.Id, _staff.Id, day, 8, 20))
                .ToArray());

        await BusinessOperationsTestBase.ProvisionAsync(_context, _business.Id);

        await BookingTestBase.SeedAsync(_context, BusinessOperationsTestBase.EnabledPercentageConfig(_business.Id, 10m));
    }

    public Task DisposeAsync()
    {
        _context.Dispose();
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Appointment> BookedAppointmentAsync()
    {
        var start = BookedStart;
        var appointment = BookingTestBase.CreateAppointment(
            _business.Id, _customer.Id, _staff.Id, start, start.AddHours(1), "booked");

        await BookingTestBase.SeedAsync(_context,
            appointment,
            BookingTestBase.CreateResource(appointment.Id, _service.Id, _service.Name, 60, 1000m, 0));

        return appointment;
    }
}

public partial class AppointmentLifecycleTests
{
    // ===========================================================
    //  STATE MACHINE (pure)
    // ===========================================================

    [Theory]
    [InlineData("booked", "confirmed")]
    [InlineData("booked", "cancelled")]
    [InlineData("booked", "no_show")]
    [InlineData("confirmed", "completed")]
    [InlineData("confirmed", "cancelled")]
    [InlineData("confirmed", "no_show")]
    public void Permitted_transitions_are_allowed(string from, string to)
    {
        Assert.True(AppointmentStatusMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData("cancelled", "confirmed")]
    [InlineData("cancelled", "completed")]
    [InlineData("completed", "confirmed")]
    [InlineData("completed", "cancelled")]
    [InlineData("no_show", "completed")]
    [InlineData("booked", "completed")]
    [InlineData("draft", "confirmed")]
    [InlineData("pending", "confirmed")]
    [InlineData("in_progress", "completed")]
    public void Impossible_transitions_are_refused(string from, string to)
    {
        Assert.False(AppointmentStatusMachine.CanTransition(from, to));
    }

    [Fact]
    public void Repeating_a_transition_is_a_no_op_not_an_error()
    {
        Assert.True(AppointmentStatusMachine.CanTransition("completed", "completed"));
        Assert.True(AppointmentStatusMachine.CanTransition("booked", "booked"));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("no_show")]
    public void Terminal_statuses_have_no_outgoing_transitions(string status)
    {
        Assert.True(AppointmentStatuses.IsTerminal(status));
        Assert.Empty(AppointmentStatusMachine.AllowedFrom(status));
    }

    [Fact]
    public void The_authoritative_status_set_has_no_draft_or_pending()
    {
        // pending_payment is the one status added for booking-fee collection: it holds
        // the slot while the payment is verified. It is deliberately distinct from the
        // rejected `pending`, which would have meant "a pending reschedule" — a separate
        // workflow (RescheduleRequest) that is never an appointment status.
        Assert.Equal(
            new[] { "pending_payment", "booked", "confirmed", "completed", "cancelled", "no_show" },
            AppointmentStatuses.All);
        Assert.False(AppointmentStatuses.IsKnown("draft"));
        Assert.False(AppointmentStatuses.IsKnown("pending"));
        Assert.False(AppointmentStatuses.IsKnown("in_progress"));
    }

    [Fact]
    public void A_pending_payment_booking_holds_its_slot_and_can_only_be_confirmed_or_cancelled()
    {
        // The slot is held: the customer has committed money to that time.
        Assert.True(AppointmentReservations.ReservesStaffTime(AppointmentStatuses.PendingPayment));
        Assert.Contains(AppointmentStatuses.PendingPayment, AppointmentReservations.ReservingStatuses);

        // It is never moved straight to completed or no_show — nobody has confirmed the
        // visit will happen until the payment is verified.
        Assert.True(AppointmentStatusMachine.CanTransition("pending_payment", "confirmed"));
        Assert.True(AppointmentStatusMachine.CanTransition("pending_payment", "cancelled"));
        Assert.False(AppointmentStatusMachine.CanTransition("pending_payment", "completed"));
        Assert.False(AppointmentStatusMachine.CanTransition("pending_payment", "no_show"));

        // Not terminal: a decision must be able to follow it.
        Assert.False(AppointmentStatuses.IsTerminal(AppointmentStatuses.PendingPayment));
    }

    [Fact]
    public void Every_transition_names_the_permission_it_requires()
    {
        Assert.Equal("appointments.confirm", AppointmentStatusMachine.RequiredPermission("confirmed"));
        Assert.Equal("appointments.complete", AppointmentStatusMachine.RequiredPermission("completed"));
        Assert.Equal("appointments.cancel", AppointmentStatusMachine.RequiredPermission("cancelled"));
        Assert.Equal("appointments.no_show", AppointmentStatusMachine.RequiredPermission("no_show"));
    }
}

public partial class AppointmentLifecycleTests
{
    // ===========================================================
    //  ENFORCEMENT THROUGH THE SERVICE
    // ===========================================================

    [Fact]
    public async Task Confirming_then_completing_walks_the_lifecycle_and_records_history()
    {
        var appointment = await BookedAppointmentAsync();

        var confirmed = await _appointments.ConfirmAsync(_owner.Id, "Business", appointment.Id);
        Assert.True(confirmed.Success, confirmed.Error?.Message);
        Assert.Equal("confirmed", confirmed.Data!.Status);

        var completed = await _appointments.CompleteAsync(_owner.Id, "Business", appointment.Id);
        Assert.True(completed.Success, completed.Error?.Message);
        Assert.Equal("completed", completed.Data!.Status);

        var history = await _context.AppointmentStatusHistory.AsNoTracking()
            .Where(h => h.AppointmentId == appointment.Id)
            .Select(h => h.Status)
            .ToListAsync();

        Assert.Equal(2, history.Count);
        Assert.Contains("confirmed", history);
        Assert.Contains("completed", history);
    }

    [Fact]
    public async Task Completing_a_cancelled_appointment_is_refused()
    {
        var appointment = await BookedAppointmentAsync();
        await _appointments.CancelAsync(_owner.Id, "Business", appointment.Id, new CancelAppointmentRequest());

        var result = await _appointments.CompleteAsync(_owner.Id, "Business", appointment.Id);

        Assert.False(result.Success);
        Assert.Equal("INVALID_STATUS_TRANSITION", result.Error?.Code);
    }

    [Fact]
    public async Task Confirming_a_completed_appointment_is_refused()
    {
        var appointment = await BookedAppointmentAsync();
        await _appointments.ConfirmAsync(_owner.Id, "Business", appointment.Id);
        await _appointments.CompleteAsync(_owner.Id, "Business", appointment.Id);

        var result = await _appointments.ConfirmAsync(_owner.Id, "Business", appointment.Id);

        Assert.False(result.Success);
        Assert.Equal("INVALID_STATUS_TRANSITION", result.Error?.Code);
    }

    [Fact]
    public async Task Cancelling_a_completed_appointment_is_refused()
    {
        var appointment = await BookedAppointmentAsync();
        await _appointments.ConfirmAsync(_owner.Id, "Business", appointment.Id);
        await _appointments.CompleteAsync(_owner.Id, "Business", appointment.Id);

        var result = await _appointments.CancelAsync(
            _owner.Id, "Business", appointment.Id, new CancelAppointmentRequest());

        Assert.False(result.Success);
        Assert.Equal("INVALID_STATUS_TRANSITION", result.Error?.Code);
    }

    [Fact]
    public async Task Marking_a_booked_appointment_as_no_show_is_allowed()
    {
        var appointment = await BookedAppointmentAsync();

        var result = await _appointments.MarkNoShowAsync(_owner.Id, "Business", appointment.Id);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("no_show", result.Data!.Status);
    }

    [Fact]
    public async Task Repeating_completion_is_idempotent_and_does_not_duplicate_side_effects()
    {
        var appointment = await BookedAppointmentAsync();
        await _appointments.ConfirmAsync(_owner.Id, "Business", appointment.Id);

        var first = await _appointments.CompleteAsync(_owner.Id, "Business", appointment.Id);
        var second = await _appointments.CompleteAsync(_owner.Id, "Business", appointment.Id);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal("completed", second.Data!.Status);

        // No duplicate history row.
        var completions = await _context.AppointmentStatusHistory.AsNoTracking()
            .CountAsync(h => h.AppointmentId == appointment.Id && h.Status == "completed");
        Assert.Equal(1, completions);

        // No duplicate commission entry.
        var entries = await _context.CommissionEntries.AsNoTracking()
            .CountAsync(e => e.AppointmentId == appointment.Id);
        Assert.Equal(1, entries);
    }
}

public partial class AppointmentLifecycleTests
{
    // ===========================================================
    //  INTEGRATION: COMPLETION -> COMMISSION
    // ===========================================================

    [Fact]
    public async Task Completing_an_appointment_accrues_commission_exactly_once()
    {
        var appointment = await BookedAppointmentAsync();
        await _appointments.ConfirmAsync(_owner.Id, "Business", appointment.Id);

        var completed = await _appointments.CompleteAsync(_owner.Id, "Business", appointment.Id);
        Assert.True(completed.Success, completed.Error?.Message);

        var entry = await _context.CommissionEntries.AsNoTracking()
            .SingleAsync(e => e.AppointmentId == appointment.Id);

        Assert.Equal(100m, entry.Amount);
        Assert.Equal(CommissionEntryStatus.Pending, entry.Status);
        Assert.Equal(_staff.Id, entry.StaffUserId);
    }

    [Fact]
    public async Task Cancelling_an_appointment_accrues_no_commission()
    {
        var appointment = await BookedAppointmentAsync();

        var cancelled = await _appointments.CancelAsync(
            _owner.Id, "Business", appointment.Id, new CancelAppointmentRequest { Note = "Customer called" });

        Assert.True(cancelled.Success, cancelled.Error?.Message);
        Assert.False(await _context.CommissionEntries.AsNoTracking()
            .AnyAsync(e => e.AppointmentId == appointment.Id));
    }

    // ===========================================================
    //  RESCHEDULE WORKFLOW
    // ===========================================================

    [Fact]
    public async Task A_customer_reschedule_request_is_recorded_with_its_original_schedule()
    {
        var appointment = await BookedAppointmentAsync();
        var proposed = appointment.ScheduledAt.AddDays(1);

        await BookingTestBase.SeedAsync(_context,
            BookingTestBase.CreateShift(_business.Id, _staff.Id, DateOnly.FromDateTime(proposed), 8, 20));

        var result = await _appointments.RescheduleAsync(_customer.Id, "Customer", appointment.Id,
            new RescheduleAppointmentRequest { ScheduledAt = proposed, Note = "Work conflict" });

        Assert.True(result.Success, result.Error?.Message);

        var request = await _context.RescheduleRequests.AsNoTracking()
            .SingleAsync(r => r.AppointmentId == appointment.Id);

        Assert.Equal("pending", request.Status);
        Assert.Equal(appointment.ScheduledAt, request.OriginalScheduledAt);
        Assert.Equal(proposed, request.ProposedScheduledAt);
        Assert.Equal(_staff.Id, request.OriginalStaffUserId);
        Assert.Equal("Work conflict", request.RequestReason);
        Assert.NotNull(request.ExpiresAt);

        // The appointment itself is untouched while the proposal is pending.
        var stored = await _context.Appointments.AsNoTracking().SingleAsync(a => a.Id == appointment.Id);
        Assert.Equal(appointment.ScheduledAt, stored.ScheduledAt);
    }

    [Fact]
    public async Task Withdrawing_a_pending_request_leaves_the_appointment_untouched()
    {
        var appointment = await BookedAppointmentAsync();
        var proposed = appointment.ScheduledAt.AddDays(1);
        await BookingTestBase.SeedAsync(_context,
            BookingTestBase.CreateShift(_business.Id, _staff.Id, DateOnly.FromDateTime(proposed), 8, 20));

        await _appointments.RescheduleAsync(_customer.Id, "Customer", appointment.Id,
            new RescheduleAppointmentRequest { ScheduledAt = proposed });

        var result = await _appointments.WithdrawRescheduleRequestAsync(_customer.Id, "Customer", appointment.Id);
        Assert.True(result.Success, result.Error?.Message);

        var request = await _context.RescheduleRequests.AsNoTracking()
            .SingleAsync(r => r.AppointmentId == appointment.Id);
        Assert.Equal("withdrawn", request.Status);
        Assert.NotNull(request.ResolvedAt);

        var stored = await _context.Appointments.AsNoTracking().SingleAsync(a => a.Id == appointment.Id);
        Assert.Equal(appointment.ScheduledAt, stored.ScheduledAt);
    }

    [Fact]
    public async Task A_customer_cannot_withdraw_somebody_else_request()
    {
        var appointment = await BookedAppointmentAsync();
        var proposed = appointment.ScheduledAt.AddDays(1);
        await BookingTestBase.SeedAsync(_context,
            BookingTestBase.CreateShift(_business.Id, _staff.Id, DateOnly.FromDateTime(proposed), 8, 20));

        await _appointments.RescheduleAsync(_customer.Id, "Customer", appointment.Id,
            new RescheduleAppointmentRequest { ScheduledAt = proposed });

        // A different customer owns no appointment here, so ownership fails first.
        var otherCustomer = BookingTestBase.CreateCustomer("withdraw-thief@test.com");
        await BookingTestBase.SeedAsync(_context, otherCustomer);

        var result = await _appointments.WithdrawRescheduleRequestAsync(
            otherCustomer.Id, "Customer", appointment.Id);

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.Error?.Code);
    }

    [Fact]
    public async Task Cancelling_an_appointment_retires_its_open_reschedule_request()
    {
        var appointment = await BookedAppointmentAsync();
        var proposed = appointment.ScheduledAt.AddDays(1);
        await BookingTestBase.SeedAsync(_context,
            BookingTestBase.CreateShift(_business.Id, _staff.Id, DateOnly.FromDateTime(proposed), 8, 20));

        await _appointments.RescheduleAsync(_customer.Id, "Customer", appointment.Id,
            new RescheduleAppointmentRequest { ScheduledAt = proposed });

        await _appointments.CancelAsync(_owner.Id, "Business", appointment.Id, new CancelAppointmentRequest());

        var request = await _context.RescheduleRequests.AsNoTracking()
            .SingleAsync(r => r.AppointmentId == appointment.Id);

        Assert.Equal("withdrawn", request.Status);
        Assert.NotNull(request.DecisionNotes);
    }

    [Fact]
    public async Task The_reschedule_history_preserves_every_proposal()
    {
        var appointment = await BookedAppointmentAsync();
        var first = appointment.ScheduledAt.AddDays(1);
        var second = appointment.ScheduledAt.AddDays(2);
        await BookingTestBase.SeedAsync(_context,
            BookingTestBase.CreateShift(_business.Id, _staff.Id, DateOnly.FromDateTime(first), 8, 20),
            BookingTestBase.CreateShift(_business.Id, _staff.Id, DateOnly.FromDateTime(second), 8, 20));

        await _appointments.RescheduleAsync(_customer.Id, "Customer", appointment.Id,
            new RescheduleAppointmentRequest { ScheduledAt = first });
        await _appointments.WithdrawRescheduleRequestAsync(_customer.Id, "Customer", appointment.Id);
        await _appointments.RescheduleAsync(_customer.Id, "Customer", appointment.Id,
            new RescheduleAppointmentRequest { ScheduledAt = second });

        var history = await _appointments.GetRescheduleHistoryAsync(_owner.Id, "Business", appointment.Id);

        Assert.True(history.Success, history.Error?.Message);
        Assert.Equal(2, history.Data!.Count);
        Assert.Contains(history.Data, h => h.Status == "withdrawn");
        Assert.Contains(history.Data, h => h.Status == "pending");
    }
}
