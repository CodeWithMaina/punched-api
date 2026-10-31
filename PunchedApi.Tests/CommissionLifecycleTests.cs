using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// The commission lifecycle end to end: accrual, settlement, payout, adjustment,
/// reversal and disputes - plus the guarantees that make it production-safe
/// (idempotency, cross-business isolation, mandatory reasons).
/// </summary>
public partial class CommissionLifecycleTests : IAsyncLifetime
{
    private Microsoft.Data.Sqlite.SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;
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

        _owner = BookingTestBase.CreateOwner("ledger-owner@test.com");
        _business = BookingTestBase.CreateBusiness(_owner.Id, "Ledger Salon");
        _staff = BookingTestBase.CreateStaff(_business.Id, "ledger-staff@test.com");
        _customer = BookingTestBase.CreateCustomer("ledger-customer@test.com");
        _service = BookingTestBase.CreateService(_business.Id, "Cut and Finish", 60, 1000m);

        await BookingTestBase.SeedAsync(_context, _owner, _business, _staff, _customer, _service,
            BookingTestBase.CreateAssignment(_business.Id, _staff.Id, _service.Id));

        await BusinessOperationsTestBase.ProvisionAsync(_context, _business.Id);
    }

    public Task DisposeAsync()
    {
        _context.Dispose();
        _connection.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>A terminal appointment with one service line, ready to accrue.</summary>
    private async Task<Appointment> CompletedAppointmentAsync(decimal price = 1000m, string status = "completed")
    {
        var start = DateTime.UtcNow.AddHours(-2);
        var appointment = BookingTestBase.CreateAppointment(
            _business.Id, _customer.Id, _staff.Id, start, start.AddHours(1), status);

        await BookingTestBase.SeedAsync(_context,
            appointment,
            BookingTestBase.CreateResource(appointment.Id, _service.Id, _service.Name, 60, price, 0));

        return appointment;
    }

    private async Task EnableCommissionAsync(
        decimal percentage = 10m, bool requireSettlement = true, bool requireApproval = false,
        bool earnOnNoShow = false, bool earnOnCancelled = false, bool requireEligibility = true)
    {
        var config = BusinessOperationsTestBase.EnabledPercentageConfig(
            _business.Id, percentage, requireSettlement, requireApproval, requireEligibility);
        config.EarnOnNoShow = earnOnNoShow;
        config.EarnOnCancelled = earnOnCancelled;
        await BookingTestBase.SeedAsync(_context, config);
    }

    private Task<List<CommissionEntry>> EntriesAsync() =>
        _context.CommissionEntries.AsNoTracking()
            .Where(e => e.BusinessId == _business.Id)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync();
}

public partial class CommissionLifecycleTests
{
    // ===========================================================
    //  ACCRUAL
    // ===========================================================

    [Fact]
    public async Task Completed_appointment_accrues_one_pending_entry_per_service_line()
    {
        await EnableCommissionAsync(percentage: 10m);
        var appointment = await CompletedAppointmentAsync(price: 1000m);

        var created = await _commissions.AccrueForAppointmentAsync(appointment.Id);

        Assert.Equal(1, created);
        var entry = Assert.Single(await EntriesAsync());
        Assert.Equal(100m, entry.Amount);
        Assert.Equal(1000m, entry.BaseAmount);
        Assert.Equal(10m, entry.RatePercent);
        Assert.Equal(CommissionEntryStatus.Pending, entry.Status);
        Assert.Equal(CommissionEntryType.Earning, entry.EntryType);
        Assert.Equal(_staff.Id, entry.StaffUserId);
        Assert.Equal(appointment.Id, entry.AppointmentId);
        Assert.Equal("KES", entry.Currency);
    }

    [Fact]
    public async Task Accrual_is_idempotent_for_a_repeated_event()
    {
        await EnableCommissionAsync();
        var appointment = await CompletedAppointmentAsync();

        var first = await _commissions.AccrueForAppointmentAsync(appointment.Id);
        var second = await _commissions.AccrueForAppointmentAsync(appointment.Id);
        var third = await _commissions.AccrueForAppointmentAsync(appointment.Id);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(0, third);
        Assert.Single(await EntriesAsync());
    }

    [Fact]
    public async Task Ineligible_staff_accrues_nothing()
    {
        await EnableCommissionAsync();
        var otherStaff = BookingTestBase.CreateStaff(_business.Id, "ineligible@test.com");
        await BookingTestBase.SeedAsync(_context, otherStaff);

        var start = DateTime.UtcNow.AddHours(-2);
        var appointment = BookingTestBase.CreateAppointment(
            _business.Id, _customer.Id, otherStaff.Id, start, start.AddHours(1), "completed");
        await BookingTestBase.SeedAsync(_context,
            appointment,
            BookingTestBase.CreateResource(appointment.Id, _service.Id, _service.Name, 60, 1000m, 0));

        Assert.Equal(0, await _commissions.AccrueForAppointmentAsync(appointment.Id));
        Assert.Empty(await EntriesAsync());
    }

    [Fact]
    public async Task Unassigned_appointment_accrues_nothing()
    {
        await EnableCommissionAsync();
        var start = DateTime.UtcNow.AddHours(-2);
        var appointment = BookingTestBase.CreateAppointment(
            _business.Id, _customer.Id, null, start, start.AddHours(1), "completed");
        await BookingTestBase.SeedAsync(_context,
            appointment,
            BookingTestBase.CreateResource(appointment.Id, _service.Id, _service.Name, 60, 1000m, 0));

        Assert.Equal(0, await _commissions.AccrueForAppointmentAsync(appointment.Id));
        Assert.Empty(await EntriesAsync());
    }

    [Fact]
    public async Task No_show_earns_nothing_unless_the_business_opts_in()
    {
        await EnableCommissionAsync();
        var appointment = await CompletedAppointmentAsync(status: "no_show");

        Assert.Equal(0, await _commissions.AccrueForAppointmentAsync(appointment.Id));
        Assert.Empty(await EntriesAsync());
    }

    [Fact]
    public async Task No_show_earns_when_the_business_opts_in()
    {
        await EnableCommissionAsync(earnOnNoShow: true);
        var appointment = await CompletedAppointmentAsync(status: "no_show");

        Assert.Equal(1, await _commissions.AccrueForAppointmentAsync(appointment.Id));
    }

    [Fact]
    public async Task Cancelled_appointment_does_not_earn_by_default()
    {
        await EnableCommissionAsync();
        var appointment = await CompletedAppointmentAsync(status: "cancelled");

        Assert.Equal(0, await _commissions.AccrueForAppointmentAsync(appointment.Id));
        Assert.Empty(await EntriesAsync());
    }

    [Fact]
    public async Task Disabled_commissions_accrue_nothing()
    {
        var config = BusinessOperationsTestBase.EnabledPercentageConfig(_business.Id);
        config.IsEnabled = false;
        await BookingTestBase.SeedAsync(_context, config);

        var appointment = await CompletedAppointmentAsync();
        Assert.Equal(0, await _commissions.AccrueForAppointmentAsync(appointment.Id));
    }
}

public partial class CommissionLifecycleTests
{
    // ===========================================================
    //  SETTLEMENT
    // ===========================================================

    [Fact]
    public async Task Pending_earning_stays_pending_while_nothing_is_paid()
    {
        await EnableCommissionAsync();
        var appointment = await CompletedAppointmentAsync();
        await _commissions.AccrueForAppointmentAsync(appointment.Id);

        var released = await _commissions.SettleForAppointmentAsync(_business.Id, appointment.Id, null);

        Assert.Equal(0, released);
        Assert.Equal(CommissionEntryStatus.Pending, (await EntriesAsync()).Single().Status);
    }

    [Fact]
    public async Task Successful_payment_releases_the_earning_as_payable()
    {
        await EnableCommissionAsync();
        var appointment = await CompletedAppointmentAsync(price: 1000m);
        await _commissions.AccrueForAppointmentAsync(appointment.Id);

        var payment = BusinessOperationsTestBase.SuccessfulPayment(
            _business.Id, appointment.Id, _customer.Id, 1000m);
        await BookingTestBase.SeedAsync(_context, payment);

        var released = await _commissions.SettleForAppointmentAsync(_business.Id, appointment.Id, payment.Id);

        Assert.Equal(1, released);
        var entry = (await EntriesAsync()).Single();
        Assert.Equal(CommissionEntryStatus.Payable, entry.Status);
        Assert.Equal(payment.Id, entry.PaymentId);
        Assert.NotNull(entry.PayableAt);
    }

    [Fact]
    public async Task Requiring_approval_releases_to_approved_not_payable()
    {
        await EnableCommissionAsync(requireApproval: true);
        var appointment = await CompletedAppointmentAsync(price: 1000m);
        await _commissions.AccrueForAppointmentAsync(appointment.Id);
        await BookingTestBase.SeedAsync(_context,
            BusinessOperationsTestBase.SuccessfulPayment(_business.Id, appointment.Id, _customer.Id, 1000m));

        await _commissions.SettleForAppointmentAsync(_business.Id, appointment.Id, null);

        Assert.Equal(CommissionEntryStatus.Approved, (await EntriesAsync()).Single().Status);
    }

    [Fact]
    public async Task Settlement_is_idempotent()
    {
        await EnableCommissionAsync();
        var appointment = await CompletedAppointmentAsync(price: 1000m);
        await _commissions.AccrueForAppointmentAsync(appointment.Id);
        await BookingTestBase.SeedAsync(_context,
            BusinessOperationsTestBase.SuccessfulPayment(_business.Id, appointment.Id, _customer.Id, 1000m));

        Assert.Equal(1, await _commissions.SettleForAppointmentAsync(_business.Id, appointment.Id, null));
        Assert.Equal(0, await _commissions.SettleForAppointmentAsync(_business.Id, appointment.Id, null));
        Assert.Single(await EntriesAsync());
    }

    [Fact]
    public async Task Without_requiring_settlement_the_earning_is_payable_immediately()
    {
        await EnableCommissionAsync(requireSettlement: false);
        var appointment = await CompletedAppointmentAsync();

        await _commissions.AccrueForAppointmentAsync(appointment.Id);

        Assert.Equal(CommissionEntryStatus.Payable, (await EntriesAsync()).Single().Status);
    }

    [Fact]
    public async Task Partial_settlement_below_the_required_ratio_does_not_release()
    {
        var config = BusinessOperationsTestBase.EnabledPercentageConfig(_business.Id);
        config.AllowPartialSettlement = false;
        await BookingTestBase.SeedAsync(_context, config);

        var appointment = await CompletedAppointmentAsync(price: 1000m);
        await _commissions.AccrueForAppointmentAsync(appointment.Id);
        await BookingTestBase.SeedAsync(_context,
            BusinessOperationsTestBase.SuccessfulPayment(_business.Id, appointment.Id, _customer.Id, 400m));

        Assert.Equal(0, await _commissions.SettleForAppointmentAsync(_business.Id, appointment.Id, null));
        Assert.Equal(CommissionEntryStatus.Pending, (await EntriesAsync()).Single().Status);
    }
}

public partial class CommissionLifecycleTests
{
    // ===========================================================
    //  PAYOUT, ADJUSTMENT, REVERSAL, DISPUTE
    // ===========================================================

    [Fact]
    public async Task Recording_a_payout_marks_the_entries_paid()
    {
        await EnableCommissionAsync();
        var appointment = await CompletedAppointmentAsync(price: 1000m);
        await _commissions.AccrueForAppointmentAsync(appointment.Id);
        await BookingTestBase.SeedAsync(_context,
            BusinessOperationsTestBase.SuccessfulPayment(_business.Id, appointment.Id, _customer.Id, 1000m));
        await _commissions.SettleForAppointmentAsync(_business.Id, appointment.Id, null);

        var result = await _commissions.RecordPayoutAsync(_owner.Id, "Business", new RecordCommissionPayoutRequest
        {
            StaffUserId = _staff.Id,
            Reference = "QGH7ABC123"
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(100m, result.Data!.TotalAmount);
        Assert.Equal(1, result.Data.EntryCount);

        var entry = (await EntriesAsync()).Single();
        Assert.Equal(CommissionEntryStatus.Paid, entry.Status);
        Assert.Equal(result.Data.Id, entry.PayoutId);
    }

    [Fact]
    public async Task A_payout_reference_can_only_be_used_once()
    {
        await EnableCommissionAsync();
        var appointment = await CompletedAppointmentAsync(price: 1000m);
        await _commissions.AccrueForAppointmentAsync(appointment.Id);
        await BookingTestBase.SeedAsync(_context,
            BusinessOperationsTestBase.SuccessfulPayment(_business.Id, appointment.Id, _customer.Id, 1000m));
        await _commissions.SettleForAppointmentAsync(_business.Id, appointment.Id, null);

        var first = await _commissions.RecordPayoutAsync(_owner.Id, "Business", new RecordCommissionPayoutRequest
        {
            StaffUserId = _staff.Id,
            Reference = "DUP-REF-1"
        });
        Assert.True(first.Success);

        var second = await _commissions.RecordPayoutAsync(_owner.Id, "Business", new RecordCommissionPayoutRequest
        {
            StaffUserId = _staff.Id,
            Reference = "DUP-REF-1"
        });

        Assert.False(second.Success);
        Assert.Equal("PAYOUT_REFERENCE_EXISTS", second.Error?.Code);
    }

    [Fact]
    public async Task Paying_out_with_no_payable_entries_is_refused()
    {
        await EnableCommissionAsync();

        var result = await _commissions.RecordPayoutAsync(_owner.Id, "Business", new RecordCommissionPayoutRequest
        {
            StaffUserId = _staff.Id,
            Reference = "EMPTY-1"
        });

        Assert.False(result.Success);
        Assert.Equal("NOT_FOUND", result.Error?.Code);
    }

    [Fact]
    public async Task An_adjustment_requires_a_reason_and_is_idempotent_by_content()
    {
        await EnableCommissionAsync();

        var withoutReason = await _commissions.AdjustAsync(_owner.Id, "Business", new CommissionAdjustmentRequest
        {
            StaffUserId = _staff.Id,
            Amount = -50m,
            Reason = ""
        });
        Assert.False(withoutReason.Success);
        Assert.Equal("VALIDATION_ERROR", withoutReason.Error?.Code);

        var request = new CommissionAdjustmentRequest
        {
            StaffUserId = _staff.Id,
            Amount = -50m,
            Reason = "Goodwill discount given at the counter",
            ReleaseImmediately = true
        };

        var first = await _commissions.AdjustAsync(_owner.Id, "Business", request);
        var replay = await _commissions.AdjustAsync(_owner.Id, "Business", request);

        Assert.True(first.Success, first.Error?.Message);
        Assert.True(replay.Success);
        Assert.Equal(first.Data!.Id, replay.Data!.Id);
        Assert.Single(await EntriesAsync());
        Assert.Equal(-50m, first.Data.Amount);
        Assert.Equal("payable", first.Data.Status);
    }
}

public partial class CommissionLifecycleTests
{
    // ===========================================================
    //  REVERSAL + DISPUTE
    // ===========================================================

    [Fact]
    public async Task Reversing_an_unpaid_earning_marks_it_reversed_and_appends_a_negative_entry()
    {
        await EnableCommissionAsync();
        var appointment = await CompletedAppointmentAsync(price: 1000m);
        await _commissions.AccrueForAppointmentAsync(appointment.Id);

        var reversed = await _commissions.ReverseForPaymentAsync(
            _business.Id, appointment.Id, null, "Customer refunded");

        Assert.Equal(1, reversed);
        var entries = await EntriesAsync();
        Assert.Equal(2, entries.Count);

        var earning = entries.Single(e => e.EntryType == CommissionEntryType.Earning);
        var reversal = entries.Single(e => e.EntryType == CommissionEntryType.Reversal);

        Assert.Equal(CommissionEntryStatus.Reversed, earning.Status);
        Assert.Equal(reversal.Id, earning.ReversedByEntryId);
        Assert.Equal(-100m, reversal.Amount);
        Assert.Equal(earning.Id, reversal.ReversesEntryId);
        Assert.Equal("Customer refunded", reversal.Reason);
    }

    [Fact]
    public async Task Reversal_is_idempotent()
    {
        await EnableCommissionAsync();
        var appointment = await CompletedAppointmentAsync();
        await _commissions.AccrueForAppointmentAsync(appointment.Id);

        Assert.Equal(1, await _commissions.ReverseForPaymentAsync(_business.Id, appointment.Id, null, "Refund"));
        Assert.Equal(0, await _commissions.ReverseForPaymentAsync(_business.Id, appointment.Id, null, "Refund"));
        Assert.Equal(2, (await EntriesAsync()).Count);
    }

    [Fact]
    public async Task Reversing_a_paid_earning_keeps_it_paid_and_records_a_recoverable_clawback()
    {
        await EnableCommissionAsync();
        var appointment = await CompletedAppointmentAsync(price: 1000m);
        await _commissions.AccrueForAppointmentAsync(appointment.Id);
        await BookingTestBase.SeedAsync(_context,
            BusinessOperationsTestBase.SuccessfulPayment(_business.Id, appointment.Id, _customer.Id, 1000m));
        await _commissions.SettleForAppointmentAsync(_business.Id, appointment.Id, null);
        await _commissions.RecordPayoutAsync(_owner.Id, "Business", new RecordCommissionPayoutRequest
        {
            StaffUserId = _staff.Id,
            Reference = "PAID-THEN-REVERSED"
        });

        await _commissions.ReverseForPaymentAsync(_business.Id, appointment.Id, null, "Refund after payout");

        var entries = await EntriesAsync();
        var earning = entries.Single(e => e.EntryType == CommissionEntryType.Earning);
        var clawback = entries.Single(e => e.EntryType == CommissionEntryType.Reversal);

        // The money moved, so the original stays PAID and the correction is a
        // payable clawback that nets against the next payout.
        Assert.Equal(CommissionEntryStatus.Paid, earning.Status);
        Assert.Equal(CommissionEntryStatus.Payable, clawback.Status);
        Assert.Equal(-100m, clawback.Amount);
    }

    [Fact]
    public async Task A_disputed_entry_is_held_out_of_the_payable_balance_and_can_be_released()
    {
        await EnableCommissionAsync();
        var appointment = await CompletedAppointmentAsync(price: 1000m);
        await _commissions.AccrueForAppointmentAsync(appointment.Id);
        await BookingTestBase.SeedAsync(_context,
            BusinessOperationsTestBase.SuccessfulPayment(_business.Id, appointment.Id, _customer.Id, 1000m));
        await _commissions.SettleForAppointmentAsync(_business.Id, appointment.Id, null);

        var entry = (await EntriesAsync()).Single();

        var disputed = await _commissions.DisputeAsync(_owner.Id, "Business", entry.Id,
            new CommissionDisputeRequest { Reason = "Staff member disputes the service price" });
        Assert.True(disputed.Success, disputed.Error?.Message);
        Assert.Equal("disputed", disputed.Data!.Status);

        var balance = await _commissions.GetStaffBalanceAsync(_owner.Id, "Business", _staff.Id);
        Assert.Equal(0m, balance.Data!.PayableAmount);
        Assert.Equal(100m, balance.Data.DisputedAmount);

        var resolved = await _commissions.ResolveDisputeAsync(_owner.Id, "Business", entry.Id,
            new ResolveCommissionDisputeRequest { Approve = true, Note = "Verified with the customer" });
        Assert.True(resolved.Success, resolved.Error?.Message);
        Assert.Equal("payable", resolved.Data!.Status);
    }

    [Fact]
    public async Task A_dispute_can_be_resolved_against_the_commission()
    {
        await EnableCommissionAsync();
        var appointment = await CompletedAppointmentAsync();
        await _commissions.AccrueForAppointmentAsync(appointment.Id);
        var entry = (await EntriesAsync()).Single();

        await _commissions.DisputeAsync(_owner.Id, "Business", entry.Id,
            new CommissionDisputeRequest { Reason = "Duplicate service line" });

        var resolved = await _commissions.ResolveDisputeAsync(_owner.Id, "Business", entry.Id,
            new ResolveCommissionDisputeRequest { Approve = false, Note = "Duplicate confirmed" });

        Assert.True(resolved.Success, resolved.Error?.Message);
        Assert.Equal("void", resolved.Data!.Status);
    }
}

public partial class CommissionLifecycleTests
{
    // ===========================================================
    //  ISOLATION + AUTHORIZATION
    // ===========================================================

    [Fact]
    public async Task A_staff_member_only_sees_their_own_ledger()
    {
        await EnableCommissionAsync();
        var appointment = await CompletedAppointmentAsync(price: 1000m);
        await _commissions.AccrueForAppointmentAsync(appointment.Id);

        var otherStaff = BookingTestBase.CreateStaff(_business.Id, "other-staff@test.com");
        await BookingTestBase.SeedAsync(_context, otherStaff);

        // Asking for somebody else entries is silently narrowed to the caller own.
        var result = await _commissions.GetEntriesAsync(_staff.Id, "Staff", new CommissionEntriesQueryRequest
        {
            StaffUserId = otherStaff.Id
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.All(result.Data!.Items, item => Assert.Equal(_staff.Id, item.StaffUserId));
    }

    [Fact]
    public async Task A_staff_member_cannot_read_another_staff_balance()
    {
        await EnableCommissionAsync();
        var otherStaff = BookingTestBase.CreateStaff(_business.Id, "balance-thief@test.com");
        await BookingTestBase.SeedAsync(_context, otherStaff);

        var result = await _commissions.GetStaffBalanceAsync(_staff.Id, "Staff", otherStaff.Id);

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.Error?.Code);
    }

    [Fact]
    public async Task Commission_data_from_another_business_is_never_visible()
    {
        await EnableCommissionAsync();
        var appointment = await CompletedAppointmentAsync(price: 1000m);
        await _commissions.AccrueForAppointmentAsync(appointment.Id);

        // A completely separate tenant, with its own owner and staff.
        var otherOwner = BookingTestBase.CreateOwner("other-owner@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other Salon");
        var otherStaff = BookingTestBase.CreateStaff(otherBusiness.Id, "other-tenant-staff@test.com");
        await BookingTestBase.SeedAsync(_context, otherOwner, otherBusiness, otherStaff);
        await BusinessOperationsTestBase.ProvisionAsync(_context, otherBusiness.Id);

        var result = await _commissions.GetEntriesAsync(otherOwner.Id, "Business", new CommissionEntriesQueryRequest());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Empty(result.Data!.Items);
    }

    [Fact]
    public async Task An_outsider_cannot_pay_out_another_business_commission()
    {
        await EnableCommissionAsync();
        var appointment = await CompletedAppointmentAsync(price: 1000m);
        await _commissions.AccrueForAppointmentAsync(appointment.Id);
        await BookingTestBase.SeedAsync(_context,
            BusinessOperationsTestBase.SuccessfulPayment(_business.Id, appointment.Id, _customer.Id, 1000m));
        await _commissions.SettleForAppointmentAsync(_business.Id, appointment.Id, null);

        var otherOwner = BookingTestBase.CreateOwner("payout-outsider@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Outsider Salon");
        await BookingTestBase.SeedAsync(_context, otherOwner, otherBusiness);
        await BusinessOperationsTestBase.ProvisionAsync(_context, otherBusiness.Id);

        var result = await _commissions.RecordPayoutAsync(otherOwner.Id, "Business", new RecordCommissionPayoutRequest
        {
            StaffUserId = _staff.Id,
            Reference = "CROSS-TENANT-1"
        });

        Assert.False(result.Success);
        Assert.Equal("STAFF_NOT_FOUND", result.Error?.Code);
    }

    [Fact]
    public async Task A_staff_member_cannot_record_a_payout()
    {
        await EnableCommissionAsync();
        var appointment = await CompletedAppointmentAsync(price: 1000m);
        await _commissions.AccrueForAppointmentAsync(appointment.Id);
        await BookingTestBase.SeedAsync(_context,
            BusinessOperationsTestBase.SuccessfulPayment(_business.Id, appointment.Id, _customer.Id, 1000m));
        await _commissions.SettleForAppointmentAsync(_business.Id, appointment.Id, null);

        var result = await _commissions.RecordPayoutAsync(_staff.Id, "Staff", new RecordCommissionPayoutRequest
        {
            StaffUserId = _staff.Id,
            Reference = "STAFF-SELF-PAY"
        });

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.Error?.Code);
    }

    [Fact]
    public async Task A_staff_member_cannot_adjust_commission()
    {
        await EnableCommissionAsync();

        var result = await _commissions.AdjustAsync(_staff.Id, "Staff", new CommissionAdjustmentRequest
        {
            StaffUserId = _staff.Id,
            Amount = 500m,
            Reason = "Self-awarded bonus"
        });

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.Error?.Code);
    }
}
