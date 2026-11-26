using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Deactivating a staff member, and handing their future work to a colleague.
///
/// <para><b>What these tests protect.</b> Two things that are expensive to get
/// wrong: a deactivation must never quietly strand a customer's booking, and a
/// reassignment must never move history — a completed appointment re-attributed to
/// somebody else would restate a month that has already been reported. They also
/// pin the tenant boundary: neither operation may reach a member of another
/// business, whatever id the caller supplies.</para>
/// </summary>
public partial class StaffLifecycleTests : IAsyncLifetime
{
    private Microsoft.Data.Sqlite.SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;
    private StaffManagementService _staff = null!;

    private User _owner = null!;
    private Business _business = null!;
    private User _member = null!;
    private User _colleague = null!;
    private User _customer = null!;
    private ServiceCatalogItem _service = null!;

    private User _otherOwner = null!;
    private Business _otherBusiness = null!;
    private User _otherMember = null!;

    public async Task InitializeAsync()
    {
        _connection = BookingTestBase.CreateConnection();
        _context = BookingTestBase.CreateContext(_connection);

        var permissions = new PermissionService(BusinessOperationsTestBase.CreatePermissionService(_context));
        _staff = new StaffManagementService(
            _context,
            BusinessOperationsTestBase.CreateScopeResolver(_context),
            permissions,
            TestHelpers.CreateLogger<StaffManagementService>(),
            audit: new StaffAuditService(
                _context, BusinessOperationsTestBase.CreateScopeResolver(_context)));

        _owner = BookingTestBase.CreateOwner("lc-owner@test.com");
        _business = BookingTestBase.CreateBusiness(_owner.Id, "Lifecycle Salon");
        _member = BookingTestBase.CreateStaff(_business.Id, "lc-member@test.com");
        _colleague = BookingTestBase.CreateStaff(_business.Id, "lc-colleague@test.com");
        _customer = BookingTestBase.CreateCustomer("lc-customer@test.com");

        _otherOwner = BookingTestBase.CreateOwner("lc-other-owner@test.com");
        _otherBusiness = BookingTestBase.CreateBusiness(_otherOwner.Id, "Other Lifecycle Salon");
        _otherMember = BookingTestBase.CreateStaff(_otherBusiness.Id, "lc-other-member@test.com");

        _service = BookingTestBase.CreateService(_business.Id, "Signature Cut", 60, 1500m);

        await BookingTestBase.SeedAsync(
            _context, _owner, _business, _member, _colleague, _customer, _service,
            _otherOwner, _otherBusiness, _otherMember);

        await BusinessOperationsTestBase.ProvisionAsync(_context, _business.Id);
        await BusinessOperationsTestBase.ProvisionAsync(_context, _otherBusiness.Id);

        // Both members deliver the service: eligibility is what the reassignment consults,
        // so a colleague who could not actually serve the customer must not be offered.
        await _context.StaffServiceAssignments.AddRangeAsync(
            BookingTestBase.CreateAssignment(_business.Id, _member.Id, _service.Id),
            BookingTestBase.CreateAssignment(_business.Id, _colleague.Id, _service.Id));
        await _context.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _context.Dispose();
        _connection.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>A future appointment for the member, with its booked price snapshot.</summary>
    private async Task<Appointment> AddAppointmentAsync(
        DateTime scheduledAt,
        string status = AppointmentStatuses.Booked,
        Guid? staffUserId = null,
        decimal price = 1500m)
    {
        var appointment = BookingTestBase.CreateAppointment(
            _business.Id, _customer.Id, staffUserId ?? _member.Id,
            scheduledAt, scheduledAt.AddHours(1), status);

        await _context.Appointments.AddAsync(appointment);
        await _context.AppointmentResources.AddAsync(
            BookingTestBase.CreateResource(appointment.Id, _service.Id, "Signature Cut", 60, price, 0));
        await _context.SaveChangesAsync();

        return appointment;
    }
}

/// <summary>The read-only impact report.</summary>
public partial class StaffLifecycleTests
{
    [Fact]
    public async Task Impact_counts_the_work_a_deactivation_would_strand()
    {
        var soon = DateTime.UtcNow.AddDays(2);
        await AddAppointmentAsync(soon);
        await AddAppointmentAsync(soon.AddHours(3));

        // A stale row whose time has passed but that nobody has resolved yet.
        await AddAppointmentAsync(DateTime.UtcNow.AddDays(-1));

        // A completed visit with an unpaid balance, and an M-PESA claim awaiting a decision.
        var completed = await AddAppointmentAsync(DateTime.UtcNow.AddDays(-3), AppointmentStatuses.Completed);
        await _context.Payments.AddAsync(new Payment
        {
            Id = Guid.NewGuid(),
            BusinessId = _business.Id,
            AppointmentId = completed.Id,
            CustomerId = _customer.Id,
            Amount = 1000m,
            Currency = "KES",
            Reference = "LC-COMPLETED",
            Method = PaymentMethod.Mpesa,
            Provider = PaymentProviderKind.ManualMpesa,
            Status = PaymentStatus.Verified,
            CreatedAt = DateTime.UtcNow
        });
        var pending = await AddAppointmentAsync(soon.AddDays(1));
        await _context.Payments.AddAsync(new Payment
        {
            Id = Guid.NewGuid(),
            BusinessId = _business.Id,
            AppointmentId = pending.Id,
            CustomerId = _customer.Id,
            Amount = 1500m,
            Currency = "KES",
            Reference = "LC-PENDING",
            Method = PaymentMethod.Mpesa,
            Provider = PaymentProviderKind.ManualMpesa,
            Status = PaymentStatus.Submitted,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var result = await _staff.GetDeactivationImpactAsync(_owner.Id, "Business", _member.Id);

        Assert.True(result.Success, result.Error?.Message);
        var impact = result.Data!;

        Assert.Equal(3, impact.FutureAppointmentCount); // two soon + the claim's appointment
        Assert.Equal(1, impact.UnresolvedPastAppointmentCount);
        Assert.Equal(1, impact.PendingVerificationCount);
        Assert.Equal(1500m, impact.PendingVerificationAmount);
        Assert.Equal(500m, impact.OutstandingAmount); // 1500 booked − 1000 collected
        Assert.Contains("future_appointments", impact.Warnings);
        Assert.Contains("pending_payment_verification", impact.Warnings);

        // The seats that could take the work are real: the colleague is qualified.
        var candidate = Assert.Single(impact.ReassignmentCandidates);
        Assert.Equal(_colleague.Id, candidate.UserId);
        Assert.Equal(3, candidate.EligibleAppointmentCount);
    }

    [Fact]
    public async Task Impact_is_not_available_for_another_business_member()
    {
        var result = await _staff.GetDeactivationImpactAsync(_owner.Id, "Business", _otherMember.Id);

        Assert.False(result.Success);
        Assert.Equal("NOT_FOUND", result.Error?.Code);
    }

    [Fact]
    public async Task Owner_membership_cannot_be_deactivated_from_the_staff_list()
    {
        var result = await _staff.GetDeactivationImpactAsync(_owner.Id, "Business", _owner.Id);

        Assert.True(result.Success, result.Error?.Message);
        Assert.False(result.Data!.CanDeactivate);
        Assert.Contains("owner_cannot_be_deactivated", result.Data.Warnings);
    }
}

/// <summary>Moving future work to a colleague.</summary>
public partial class StaffLifecycleTests
{
    [Fact]
    public async Task Reassignment_moves_only_future_open_appointments_and_keeps_history()
    {
        var future = await AddAppointmentAsync(DateTime.UtcNow.AddDays(2));
        var confirmed = await AddAppointmentAsync(
            DateTime.UtcNow.AddDays(3), AppointmentStatuses.Confirmed);
        var completed = await AddAppointmentAsync(
            DateTime.UtcNow.AddDays(-4), AppointmentStatuses.Completed);
        var cancelled = await AddAppointmentAsync(
            DateTime.UtcNow.AddDays(-2), AppointmentStatuses.Cancelled);

        var result = await _staff.ReassignAppointmentsAsync(
            _owner.Id, "Business", _member.Id,
            new ReassignStaffAppointmentsRequest { TargetStaffUserId = _colleague.Id });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.Data!.MovedCount);
        Assert.Equal(0, result.Data.SkippedCount);

        _context.ChangeTracker.Clear();

        // Future work follows the colleague; the status itself is untouched.
        var movedBooking = await _context.Appointments.AsNoTracking().FirstAsync(a => a.Id == future.Id);
        Assert.Equal(_colleague.Id, movedBooking.StaffUserId);
        Assert.Equal(AppointmentStatuses.Booked, movedBooking.Status);
        Assert.Equal(1, movedBooking.Version);

        var movedConfirmed = await _context.Appointments.AsNoTracking().FirstAsync(a => a.Id == confirmed.Id);
        Assert.Equal(_colleague.Id, movedConfirmed.StaffUserId);
        Assert.Equal(AppointmentStatuses.Confirmed, movedConfirmed.Status);

        // History is never re-attributed: a completed visit belongs to whoever did it.
        Assert.Equal(_member.Id,
            (await _context.Appointments.AsNoTracking().FirstAsync(a => a.Id == completed.Id)).StaffUserId);
        Assert.Equal(_member.Id,
            (await _context.Appointments.AsNoTracking().FirstAsync(a => a.Id == cancelled.Id)).StaffUserId);

        // And the move is on the record, with both parties named.
        var audit = await _context.StaffAuditEvents.AsNoTracking()
            .SingleAsync(e => e.BusinessId == _business.Id
                && e.EventType == StaffAuditEventTypes.AppointmentsReassigned);
        Assert.Equal(_owner.Id, audit.ActorUserId);
        Assert.Contains("2 appointment(s) moved", audit.Summary);
    }

    [Fact]
    public async Task Reassignment_refuses_a_suspended_colleague()
    {
        await AddAppointmentAsync(DateTime.UtcNow.AddDays(2));

        var membership = await _context.BusinessMemberships
            .FirstAsync(m => m.BusinessId == _business.Id && m.UserId == _colleague.Id);
        membership.Status = BusinessMembershipStatus.Suspended;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await _staff.ReassignAppointmentsAsync(
            _owner.Id, "Business", _member.Id,
            new ReassignStaffAppointmentsRequest { TargetStaffUserId = _colleague.Id });

        Assert.False(result.Success);
        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
    }

    [Fact]
    public async Task Reassignment_cannot_reach_another_business()
    {
        await AddAppointmentAsync(DateTime.UtcNow.AddDays(2));

        // The source is not this owner's staff member.
        var foreignSource = await _staff.ReassignAppointmentsAsync(
            _owner.Id, "Business", _otherMember.Id,
            new ReassignStaffAppointmentsRequest { TargetStaffUserId = _colleague.Id });

        Assert.False(foreignSource.Success);
        Assert.Equal("NOT_FOUND", foreignSource.Error?.Code);

        // …and a colleague from another business can never receive this business's work.
        var foreignTarget = await _staff.ReassignAppointmentsAsync(
            _owner.Id, "Business", _member.Id,
            new ReassignStaffAppointmentsRequest { TargetStaffUserId = _otherMember.Id });

        Assert.False(foreignTarget.Success);
        Assert.Equal("VALIDATION_ERROR", foreignTarget.Error?.Code);
    }

    [Fact]
    public async Task Reassignment_skips_a_colleague_whose_diary_already_overlaps()
    {
        var slot = DateTime.UtcNow.AddDays(2);
        await AddAppointmentAsync(slot);
        // The colleague is already busy at that exact time.
        await AddAppointmentAsync(slot, AppointmentStatuses.Confirmed, _colleague.Id);

        var result = await _staff.ReassignAppointmentsAsync(
            _owner.Id, "Business", _member.Id,
            new ReassignStaffAppointmentsRequest { TargetStaffUserId = _colleague.Id });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(0, result.Data!.MovedCount);
        Assert.Equal(1, result.Data.SkippedCount);
        Assert.Equal("TARGET_UNAVAILABLE", result.Data.Skipped[0].Reason);
    }
}


