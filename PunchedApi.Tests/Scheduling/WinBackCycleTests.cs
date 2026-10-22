using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Scheduling;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests.Scheduling;

/// <summary>
/// Covers repeatable win-back nudges: cycle policy math, the lapsed-customer
/// scan and the send cooldown read from the notification outbox.
/// </summary>
/// <remarks>
/// SQLite in-memory is used (same harness as <see cref="BookingTestBase"/>)
/// because the scan uses <c>GroupBy</c>, <c>StartsWith</c> and per-row
/// <c>SaveChanges</c> inside transactions — none of which the EF in-memory
/// provider supports faithfully.
/// </remarks>
public class WinBackCycleTests
{
    private static readonly Guid BusinessId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ══════════════════════════════════════════════════════════════════
    //  Cycle policy
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Dedupe_key_is_stable_inside_one_cycle_and_changes_per_cycle()
    {
        var customerId = Guid.NewGuid();
        var anchor = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

        var a = WinBackCyclePolicy.BuildDedupeKey(customerId, BusinessId, anchor, 0);
        var b = WinBackCyclePolicy.BuildDedupeKey(customerId, BusinessId, anchor, 0);
        var next = WinBackCyclePolicy.BuildDedupeKey(customerId, BusinessId, anchor, 1);

        Assert.Equal(a, b);
        Assert.NotEqual(a, next);
        Assert.StartsWith(WinBackCyclePolicy.DedupeKeyPrefix(customerId, BusinessId), a);
        Assert.EndsWith("c0", a);
        Assert.EndsWith("c1", next);
    }

    [Fact]
    public void Dedupe_key_does_not_reuse_the_previous_cycle_after_reactivation()
    {
        var customerId = Guid.NewGuid();
        var firstAnchor = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        // Booking after the first cycle moves the anchor; the new key must differ
        // even though the cycle index restarts at 0.
        var secondAnchor = firstAnchor.AddDays(60);

        var first = WinBackCyclePolicy.BuildDedupeKey(customerId, BusinessId, firstAnchor, 1);
        var second = WinBackCyclePolicy.BuildDedupeKey(customerId, BusinessId, secondAnchor, 0);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Dedupe_key_normalises_postgres_unspecified_kind_without_shifting_ticks()
    {
        var customerId = Guid.NewGuid();
        var utc = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var unspecified = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

        Assert.Equal(
            WinBackCyclePolicy.BuildDedupeKey(customerId, BusinessId, utc, 2),
            WinBackCyclePolicy.BuildDedupeKey(customerId, BusinessId, unspecified, 2));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(29, 0)]
    [InlineData(30, 1)]
    [InlineData(59, 1)]
    [InlineData(60, 2)]
    [InlineData(91, 3)]
    public void Cycle_index_counts_whole_cooldown_windows(int daysSinceAnchor, int expectedCycle)
    {
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var anchor = now.AddDays(-daysSinceAnchor);

        Assert.Equal(expectedCycle, WinBackCyclePolicy.ComputeCycleIndex(anchor, now, 30));
    }

    [Fact]
    public void Cycle_index_is_zero_for_a_future_anchor_and_rejects_a_zero_cooldown()
    {
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(0, WinBackCyclePolicy.ComputeCycleIndex(now.AddDays(5), now, 30));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WinBackCyclePolicy.ComputeCycleIndex(now.AddDays(-90), now, 0));
    }

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(31, true)]
    public void Lapse_threshold_is_inclusive_of_the_inactivity_window(int daysInactive, bool expected)
    {
        Assert.Equal(expected, WinBackCyclePolicy.IsLapsed(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-daysInactive),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            30));
    }

    [Theory]
    [InlineData("cancelled", false)]
    [InlineData("Cancelled", false)]
    [InlineData("no_show", false)]
    [InlineData("completed", true)]
    [InlineData("booked", true)]
    [InlineData(null, true)]
    public void Cancelled_and_no_show_appointments_are_not_customer_activity(
        string? status, bool expected)
    {
        Assert.Equal(expected, WinBackCyclePolicy.CountsAsActivity(status));
    }

    [Fact]
    public void Every_win_back_template_type_is_registered()
    {
        Assert.All(WinBackCyclePolicy.WinBackTemplateTypes, type =>
            Assert.True(NotificationTypes.TryGet(type, out _), $"'{type}' must be registered to render."));
    }

    [Fact]
    public void Lock_key_is_scoped_per_business_and_customer()
    {
        var customerId = Guid.NewGuid();
        var otherCustomer = Guid.NewGuid();

        Assert.Equal(
            WinBackCyclePolicy.LockKey(customerId, BusinessId),
            WinBackCyclePolicy.LockKey(customerId, BusinessId));
        Assert.NotEqual(
            WinBackCyclePolicy.LockKey(customerId, BusinessId),
            WinBackCyclePolicy.LockKey(otherCustomer, BusinessId));
    }

    // ══════════════════════════════════════════════════════════════════
    //  Cooldown service
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Cooldown_service_returns_null_when_the_customer_was_never_nudged()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (customer, business) = await SeedEnrollmentAsync(context, DateTime.UtcNow.AddDays(-60));

        var cooldown = new WinBackCooldownService(context);

        Assert.Null(await cooldown.GetLastSentAtAsync(customer.Id, business.Id));
        Assert.Null(await cooldown.GetCooldownEndsAtAsync(customer.Id, business.Id, 30));
    }

    [Fact]
    public async Task Cooldown_service_only_counts_sent_win_back_notifications()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var now = DateTime.UtcNow;
        var (customer, business) = await SeedEnrollmentAsync(context, now.AddDays(-90));

        AddNotification(context, customer.Id, business.Id, "loyalty.win_back_nudge", "sent", now.AddDays(-10));
        AddNotification(context, customer.Id, business.Id, "customer.lapsed", "failed", now.AddDays(-1));
        AddNotification(context, customer.Id, business.Id, "appointment.reminder", "sent", now.AddHours(-2));
        await context.SaveChangesAsync();

        var cooldown = new WinBackCooldownService(context);
        var lastSentAt = await cooldown.GetLastSentAtAsync(customer.Id, business.Id);

        Assert.NotNull(lastSentAt);
        Assert.Equal(now.AddDays(-10).Date, lastSentAt!.Value.Date);
        var endsAt = await cooldown.GetCooldownEndsAtAsync(customer.Id, business.Id, 30);
        Assert.Equal(now.AddDays(-10).AddDays(30), endsAt!.Value);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Lapsed-customer scan
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Scan_creates_one_job_per_lapsed_pair_with_a_cycle_key_and_payload()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var now = DateTime.UtcNow;
        var enrolledAt = now.AddDays(-45);
        var (customer, business) = await SeedEnrollmentAsync(context, enrolledAt);

        var result = await CreateScanner(context)
            .ScanAsync(now, lapseInactivityDays: 30, cooldownDays: 30);

        Assert.Equal(1, result.Evaluated);
        Assert.Equal(1, result.Lapsed);
        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.SkippedCooldown);
        Assert.Equal(0, result.SkippedDuplicate);

        var job = await context.ScheduledJobs.SingleAsync();
        Assert.Equal(WinBackCyclePolicy.JobType, job.JobType);
        Assert.Equal(business.Id, job.BusinessId);
        Assert.Equal("pending", job.Status);
        Assert.Equal(
            WinBackCyclePolicy.BuildDedupeKey(customer.Id, business.Id, enrolledAt, 1),
            job.DedupeKey);
        Assert.Contains(customer.Id.ToString("D"), job.PayloadJson);
        Assert.Contains("\"daysInactive\":45", job.PayloadJson);
    }

    [Fact]
    public async Task Scan_is_idempotent_within_a_cycle()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var now = DateTime.UtcNow;
        await SeedEnrollmentAsync(context, now.AddDays(-45));

        var scanner = CreateScanner(context);
        await scanner.ScanAsync(now, 30, 30);
        var second = await scanner.ScanAsync(now, 30, 30);

        Assert.Equal(0, second.Created);
        Assert.Equal(1, second.SkippedDuplicate);
        Assert.Equal(1, await context.ScheduledJobs.CountAsync());
    }
    [Fact]
    public async Task Scan_ignores_customers_with_a_recent_qualifying_appointment()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var now = DateTime.UtcNow;
        var (customer, business) = await SeedEnrollmentAsync(context, now.AddDays(-120));
        context.Appointments.Add(BookingTestBase.CreateAppointment(
            business.Id, customer.Id, null, now.AddDays(-3), now.AddDays(-3).AddMinutes(30), "completed"));
        await context.SaveChangesAsync();

        var result = await CreateScanner(context).ScanAsync(now, 30, 30);

        Assert.Equal(1, result.Evaluated);
        Assert.Equal(0, result.Lapsed);
        Assert.Equal(0, result.Created);
        Assert.Empty(await context.ScheduledJobs.ToListAsync());
    }

    [Fact]
    public async Task Scan_falls_back_to_enrolment_when_the_only_appointment_was_cancelled()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var now = DateTime.UtcNow;
        var enrolledAt = now.AddDays(-90);
        var (customer, business) = await SeedEnrollmentAsync(context, enrolledAt);
        context.Appointments.Add(BookingTestBase.CreateAppointment(
            business.Id, customer.Id, null, now.AddDays(-60), now.AddDays(-60).AddMinutes(30), "cancelled"));
        await context.SaveChangesAsync();

        var result = await CreateScanner(context).ScanAsync(now, 30, 30);

        Assert.Equal(1, result.Created);
        var job = await context.ScheduledJobs.SingleAsync();
        Assert.Equal(
            WinBackCyclePolicy.BuildDedupeKey(customer.Id, business.Id, enrolledAt, 3),
            job.DedupeKey);
        Assert.Contains("\"daysInactive\":90", job.PayloadJson);
    }

    [Fact]
    public async Task Scan_suppresses_the_job_while_the_send_cooldown_is_active()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var now = DateTime.UtcNow;
        var (customer, business) = await SeedEnrollmentAsync(context, now.AddDays(-45));
        AddNotification(context, customer.Id, business.Id, "customer.lapsed", "sent", now.AddDays(-5));
        await context.SaveChangesAsync();

        var result = await CreateScanner(context).ScanAsync(now, 30, 30);

        Assert.Equal(1, result.Lapsed);
        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.SkippedCooldown);
        Assert.Empty(await context.ScheduledJobs.ToListAsync());
    }

    [Fact]
    public async Task Scan_ignores_transactional_notifications_when_checking_the_cooldown()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var now = DateTime.UtcNow;
        var (customer, business) = await SeedEnrollmentAsync(context, now.AddDays(-45));
        AddNotification(context, customer.Id, business.Id, "appointment.reminder", "sent", now.AddDays(-1));
        await context.SaveChangesAsync();

        var result = await CreateScanner(context).ScanAsync(now, 30, 30);

        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.SkippedCooldown);
    }

    [Fact]
    public async Task Scan_creates_a_new_cycle_after_the_cooldown_elapses()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var now = DateTime.UtcNow;
        var enrolledAt = now.AddDays(-45);
        var (customer, business) = await SeedEnrollmentAsync(context, enrolledAt);
        // Last nudge 31 days ago: the cooldown is over, so cycle 1 may send again.
        AddNotification(context, customer.Id, business.Id, "loyalty.win_back_nudge", "sent", now.AddDays(-31));
        await context.SaveChangesAsync();

        var result = await CreateScanner(context).ScanAsync(now, 30, 30);

        Assert.Equal(1, result.Created);
        var job = await context.ScheduledJobs.SingleAsync();
        Assert.Equal(
            WinBackCyclePolicy.BuildDedupeKey(customer.Id, business.Id, enrolledAt, 1),
            job.DedupeKey);
    }

    [Fact]
    public async Task Scan_skips_a_pair_that_already_has_an_outstanding_job()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var now = DateTime.UtcNow;
        var (customer, business) = await SeedEnrollmentAsync(context, now.AddDays(-45));
        context.ScheduledJobs.Add(new ScheduledJob
        {
            Id = Guid.NewGuid(),
            JobType = WinBackCyclePolicy.JobType,
            DedupeKey = WinBackCyclePolicy.BuildDedupeKey(customer.Id, business.Id, now.AddDays(-90), 0),
            BusinessId = business.Id,
            PayloadJson = "{}",
            ScheduledForUtc = now.AddDays(-1),
            Status = "pending",
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddDays(-1),
        });
        await context.SaveChangesAsync();

        var result = await CreateScanner(context).ScanAsync(now, 30, 30);

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.SkippedDuplicate);
    }

    [Fact]
    public async Task Scan_ignores_left_enrolments()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var now = DateTime.UtcNow;
        await SeedEnrollmentAsync(context, now.AddDays(-120));
        var enrollment = await context.CustomerBusinessEnrollments.SingleAsync();
        enrollment.Status = CustomerBusinessEnrollmentStatus.Left;
        enrollment.LeftAt = now.AddDays(-10);
        await context.SaveChangesAsync();

        var result = await CreateScanner(context).ScanAsync(now, 30, 30);

        Assert.Equal(0, result.Evaluated);
        Assert.Equal(0, result.Created);
    }


    // ══════════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════════

    private static LapsedCustomerScanner CreateScanner(ApplicationDbContext context)
        => new(context,
            new WinBackCooldownService(context),
            TestHelpers.CreateLogger<LapsedCustomerScanner>());

    private static async Task<(User Customer, Business Business)> SeedEnrollmentAsync(
        ApplicationDbContext context, DateTime enrolledAt)
    {
        var owner = BookingTestBase.CreateOwner($"owner-{Guid.NewGuid():N}@test.com");
        context.Users.Add(owner);

        var business = BookingTestBase.CreateBusiness(owner.Id, "Win Back Salon");
        context.Businesses.Add(business);

        var customer = BookingTestBase.CreateCustomer($"customer-{Guid.NewGuid():N}@test.com");
        context.Users.Add(customer);

        context.CustomerBusinessEnrollments.Add(new CustomerBusinessEnrollment
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            BusinessId = business.Id,
            Status = CustomerBusinessEnrollmentStatus.Active,
            Source = "booking",
            EnrolledAt = enrolledAt,
            UpdatedAt = enrolledAt,
            CreatedAt = enrolledAt,
        });

        await context.SaveChangesAsync();
        return (customer, business);
    }

    private static void AddNotification(
        ApplicationDbContext context,
        Guid customerId,
        Guid businessId,
        string templateType,
        string status,
        DateTime sentAt)
    {
        context.NotificationLogs.Add(new NotificationLog
        {
            Id = Guid.NewGuid(),
            UserId = customerId,
            BusinessId = businessId,
            Channel = "email",
            TemplateType = templateType,
            Status = status,
            SentAt = sentAt,
            NextAttemptAt = sentAt,
            CreatedAt = sentAt,
        });
    }


}
