using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Services;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Notifications;

namespace PunchedApi.Tests.Notifications;

/// <summary>
/// Tests for the notification management layer: recipient masking, failure
/// classification, template catalogue and variable resolution, business
/// preference enforcement, admin channel overrides, retry/cancel safety and
/// test-send isolation from production metrics.
/// </summary>
public class NotificationManagementTests
{
    // ══════════════════════════════════════════════════════════════════
    //  Masking + classification
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void MaskEmail_keeps_the_domain_and_hides_the_local_part()
    {
        Assert.Equal("am***@example.com", NotificationManagementShared.MaskEmail("amina.odhiambo@example.com"));
        Assert.Equal("***", NotificationManagementShared.MaskEmail("not-an-email"));
    }

    [Fact]
    public void MaskPhone_keeps_only_the_last_three_digits()
    {
        Assert.Equal("***678", NotificationManagementShared.MaskPhone("+254712345678"));
        Assert.Equal("***", NotificationManagementShared.MaskPhone("12"));
    }

    [Fact]
    public void MaskRecipient_falls_back_to_a_user_prefix_when_no_contact_exists()
    {
        var userId = Guid.NewGuid();
        var masked = NotificationManagementShared.MaskRecipient(null, null, userId);
        Assert.StartsWith("user:", masked);
        Assert.DoesNotContain(userId.ToString(), masked);
    }

    [Theory]
    [InlineData("failed", "permanent:no_channel_registered", "permanent_failure")]
    [InlineData("failed", "retry_exhausted:3", "retry_exhausted")]
    [InlineData("failed", "smtp timeout", "transient_failure")]
    [InlineData("pending", null, "pending")]
    [InlineData("sent", null, "delivered")]
    public void Classify_derives_the_failure_class_from_the_ledger_vocabulary(
        string status, string? error, string expected)
    {
        Assert.Equal(expected, NotificationManagementShared.Classify(status, error));
    }

    [Fact]
    public void Only_retry_exhausted_rows_are_retry_eligible()
    {
        Assert.True(NotificationManagementShared.IsRetryEligible("failed", "retry_exhausted:3"));
        Assert.False(NotificationManagementShared.IsRetryEligible("failed", "permanent:bad_recipient"));
        Assert.False(NotificationManagementShared.IsRetryEligible("sent", null));
    }

    [Fact]
    public void SanitizePayload_strips_secrets_before_a_payload_is_projected()
    {
        var sanitized = NotificationManagementShared.SanitizePayload(
            "{\"businessName\":\"Aurelia\",\"code\":\"123456\",\"password\":\"hunter2\",\"amount\":\"KES 500\"}");

        Assert.Contains("Aurelia", sanitized);
        Assert.Contains("KES 500", sanitized);
        Assert.DoesNotContain("123456", sanitized);
        Assert.DoesNotContain("hunter2", sanitized);
    }

    [Fact]
    public void SanitizePayload_never_throws_on_malformed_input()
    {
        Assert.Equal("{}", NotificationManagementShared.SanitizePayload("not json"));
        Assert.Equal("{}", NotificationManagementShared.SanitizePayload(null));
    }

    // ══════════════════════════════════════════════════════════════════
    //  Variables + SMS segments
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void AvailableTokens_are_derived_from_the_registered_payload_schema()
    {
        var definition = NotificationTypes.Get("appointment.booked");
        var tokens = NotificationManagementShared.AvailableTokens(definition);

        Assert.Contains("businessName", tokens);
        Assert.Contains("scheduledAt", tokens);
        Assert.Contains("BookLink", tokens);
    }

    [Fact]
    public void SamplePayload_satisfies_every_required_key()
    {
        var definition = NotificationTypes.Get("payment.failed");
        var payload = NotificationManagementShared.SamplePayload(definition);

        foreach (var required in definition.PayloadSchema.Required)
            Assert.True(payload.ContainsKey(required), $"sample payload is missing required key '{required}'");
    }

    [Fact]
    public void Sms_segment_estimation_matches_the_gsm_and_ucs2_rules()
    {
        var short7 = NotificationManagementShared.EstimateSmsSegments(new string('a', 160));
        Assert.Equal(1, short7.Segments);
        Assert.Equal("GSM-7", short7.Encoding);

        var long7 = NotificationManagementShared.EstimateSmsSegments(new string('a', 161));
        Assert.Equal(2, long7.Segments);


        // A single non-GSM character drops the whole message to UCS-2.
        var unicode = NotificationManagementShared.EstimateSmsSegments(new string('a', 160) + "\uD83D\uDE00");
        Assert.Equal("UCS-2", unicode.Encoding);
        Assert.Equal(3, unicode.Segments);
    }
}


/// <summary>
/// Records audit writes so a test can assert that an administrative mutation was
/// audited without a live audit table.
/// </summary>
internal sealed class RecordingAuditService : ISubscriptionAuditService
{
    public List<(string Action, Guid? ActorUserId, Guid? BusinessId, string? PayloadJson, string? Reason)> Entries { get; } = [];

    public Task RecordAsync(
        string action,
        Guid? actorUserId,
        Guid? targetBusinessId,
        Guid? targetPlanId,
        string? payloadJson = null,
        string? reason = null)
    {
        Entries.Add((action, actorUserId, targetBusinessId, payloadJson, reason));
        return Task.CompletedTask;
    }
}

/// <summary>Availability stub: reports the channels a test declares available.</summary>
internal sealed class StubChannelAvailability : INotificationChannelAvailability
{
    private readonly HashSet<string> _available;

    public StubChannelAvailability(params string[] available)
    {
        _available = new HashSet<string>(available, StringComparer.Ordinal);
        _available.Add(NotificationChannel.InApp);
    }

    public Task<IReadOnlyList<string>> FilterAvailableAsync(
        Guid userId, IEnumerable<string> channels, CancellationToken ct = default)
    {
        IReadOnlyList<string> filtered = channels
            .Where(channel => _available.Contains(channel))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(filtered);
    }
}

/// <summary>SMTP stub that either succeeds or fails deterministically.</summary>
internal sealed class StubSmtpDelivery : INotificationSmtpDelivery
{
    private readonly bool _succeed;

    public StubSmtpDelivery(bool succeed = true) => _succeed = succeed;

    public int Sends { get; private set; }

    public Task SendAsync(MimeKit.MimeMessage message, CancellationToken cancellationToken)
    {
        Sends++;
        return _succeed
            ? Task.CompletedTask
            : Task.FromException(new InvalidOperationException("smtp refused"));
    }
}

/// <summary>
/// End-to-end behaviour of the admin and business notification management
/// services against a real (SQLite) context.
/// </summary>
public class NotificationManagementServiceTests
{
    // ══════════════════════════════════════════════════════════════════
    //  Admin: templates, logs, channel override, retry/cancel
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Global_templates_flag_protected_types_and_list_their_variables()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateAdminService(context, new RecordingAuditService());

        var templates = await service.GetGlobalTemplatesAsync();

        Assert.NotEmpty(templates);
        var security = templates.First(t => t.Type == "security.verification_code");
        Assert.True(security.Protected);

        var booking = templates.First(t => t.Type == "appointment.booked" && t.Channel == NotificationChannel.Email);
        Assert.False(booking.Protected);
        Assert.Contains("businessName", booking.AvailableVariables);
        Assert.Equal(0, booking.OverrideCount);
    }

    [Fact]
    public async Task Logs_mask_the_recipient_and_never_return_a_raw_address()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateAdminService(context, new RecordingAuditService());

        var owner = BookingTestBase.CreateOwner("amina.odhiambo@example.com");
        context.Users.Add(owner);
        var business = BookingTestBase.CreateBusiness(owner.Id, "Masked Salon");
        context.Businesses.Add(business);
        context.NotificationLogs.Add(new NotificationLog
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            BusinessId = business.Id,
            Channel = NotificationChannel.Email,
            TemplateType = "appointment.booked",
            Status = "sent",
            PayloadJson = "{\"businessName\":\"Masked Salon\",\"code\":\"999999\"}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var page = await service.GetLogsAsync(null, null, null, null, null, null, null, null, 1, 25);

        var item = Assert.Single(page.Items);
        Assert.Equal("am***@example.com", item.RecipientMasked);
        Assert.Equal("Masked Salon", item.BusinessName);
        Assert.Equal("delivered", item.Classification);

        var detail = await service.GetLogDetailAsync(item.Id);
        Assert.NotNull(detail);
        Assert.DoesNotContain("999999", detail!.SanitizedPayloadJson);
    }
    [Fact]
    public async Task Admin_channel_override_suppresses_every_category_and_is_audited()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var audit = new RecordingAuditService();
        var service = CreateAdminService(context, audit);

        var owner = BookingTestBase.CreateOwner("owner@test.com");
        context.Users.Add(owner);
        var business = BookingTestBase.CreateBusiness(owner.Id, "Override Salon");
        context.Businesses.Add(business);
        await context.SaveChangesAsync();

        var detail = await service.SetBusinessChannelAsync(
            business.Id, NotificationChannel.Sms, enabled: false, actorUserId: owner.Id, reason: "cost review");

        Assert.NotNull(detail);
        Assert.True(detail!.DeliveryDisabled);
        Assert.Contains(detail.ChannelOverrides, entry => entry.Channel == NotificationChannel.Sms && !entry.Enabled);

        var rows = await context.NotificationPreferences
            .Where(row => row.BusinessId == business.Id && row.UserId == null && row.Channel == NotificationChannel.Sms)
            .ToListAsync();
        Assert.Equal(NotificationDefaults.AllCategories.Count, rows.Count);
        Assert.All(rows, row => Assert.False(row.Enabled));

        Assert.Contains(
            audit.Entries,
            entry => entry.Action == AdminNotificationManagementService.AuditActions.ChannelOverride);
    }

    [Fact]
    public async Task Admin_channel_override_enabling_clears_the_kill_switch()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateAdminService(context, new RecordingAuditService());

        var owner = BookingTestBase.CreateOwner("owner2@test.com");
        context.Users.Add(owner);
        var business = BookingTestBase.CreateBusiness(owner.Id, "Reenable Salon");
        context.Businesses.Add(business);
        await context.SaveChangesAsync();

        await service.SetBusinessChannelAsync(business.Id, NotificationChannel.Sms, false, owner.Id, null);
        var reenabled = await service.SetBusinessChannelAsync(business.Id, NotificationChannel.Sms, true, owner.Id, null);

        Assert.NotNull(reenabled);
        Assert.DoesNotContain(reenabled!.ChannelOverrides, entry => entry.Channel == NotificationChannel.Sms);
        Assert.Empty(await context.NotificationPreferences
            .Where(row => row.BusinessId == business.Id && row.Channel == NotificationChannel.Sms)
            .ToListAsync());
    }

    [Fact]
    public async Task Retry_requeues_a_retry_exhausted_row_and_refuses_a_second_attempt()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var audit = new RecordingAuditService();
        var service = CreateAdminService(context, audit);

        var owner = BookingTestBase.CreateOwner("retry@test.com");
        var row = new NotificationLog
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            Channel = NotificationChannel.Email,
            TemplateType = "appointment.booked",
            Status = "failed",
            Error = "retry_exhausted:3",
            Attempts = 3,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Users.Add(owner);
        context.NotificationLogs.Add(row);
        await context.SaveChangesAsync();

        var first = await service.RetryAsync(row.Id, owner.Id);
        Assert.True(first.Accepted);

        var reloaded = await context.NotificationLogs.AsNoTracking().SingleAsync(item => item.Id == row.Id);
        Assert.Equal("pending", reloaded.Status);
        Assert.Equal(0, reloaded.Attempts);
        Assert.Null(reloaded.Error);

        // A second retry is refused because the row is no longer retry-exhausted,
        // which is what prevents duplicate delivery from a double click.
        var second = await service.RetryAsync(row.Id, owner.Id);
        Assert.False(second.Accepted);
        Assert.Equal("NOT_RETRYABLE", second.Code);

        Assert.Contains(audit.Entries, entry => entry.Action == AdminNotificationManagementService.AuditActions.Retry);
    }

    [Fact]
    public async Task Retry_refuses_a_permanent_failure()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateAdminService(context, new RecordingAuditService());

        var owner = BookingTestBase.CreateOwner("perm@test.com");
        var row = new NotificationLog
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            Channel = NotificationChannel.Sms,
            TemplateType = "appointment.booked",
            Status = "failed",
            Error = "permanent:no_mobile_number",
            Attempts = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Users.Add(owner);
        context.NotificationLogs.Add(row);
        await context.SaveChangesAsync();

        var result = await service.RetryAsync(row.Id, owner.Id);

        Assert.False(result.Accepted);
        Assert.Equal("NOT_RETRYABLE", result.Code);
        var reloaded = await context.NotificationLogs.AsNoTracking().SingleAsync(item => item.Id == row.Id);
        Assert.Equal("failed", reloaded.Status);
    }

    [Fact]
    public async Task Cancel_marks_a_pending_row_cancelled_and_refuses_a_sent_row()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateAdminService(context, new RecordingAuditService());

        var owner = BookingTestBase.CreateOwner("cancel@test.com");
        var pending = new NotificationLog
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            Channel = NotificationChannel.Email,
            TemplateType = "appointment.reminder",
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var sent = new NotificationLog
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            Channel = NotificationChannel.Email,
            TemplateType = "appointment.reminder",
            Status = "sent",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Users.Add(owner);
        context.NotificationLogs.AddRange(pending, sent);
        await context.SaveChangesAsync();

        Assert.True((await service.CancelAsync(pending.Id, owner.Id, "operator request")).Accepted);
        Assert.False((await service.CancelAsync(sent.Id, owner.Id, null)).Accepted);

        var reloaded = await context.NotificationLogs.AsNoTracking().SingleAsync(item => item.Id == pending.Id);
        Assert.Equal("cancelled", reloaded.Status);
    }

    [Fact]
    public async Task Delivery_monitoring_reports_worker_state_from_ledger_evidence()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateAdminService(context, new RecordingAuditService());

        var owner = BookingTestBase.CreateOwner("worker@test.com");
        context.Users.Add(owner);
        context.NotificationLogs.Add(new NotificationLog
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            Channel = NotificationChannel.Email,
            TemplateType = "appointment.booked",
            Status = "pending",
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-30),
        });
        await context.SaveChangesAsync();

        var monitoring = await service.GetDeliveryMonitoringAsync();

        Assert.Equal(1, monitoring.Pending);
        Assert.Equal("stalled", monitoring.WorkerState);
        Assert.NotNull(monitoring.OldestPendingAgeSeconds);
        Assert.True(monitoring.OldestPendingAgeSeconds > 60);
        Assert.Equal(5, monitoring.Backlog.Count);
        Assert.NotEmpty(monitoring.Channels);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Business: preferences, templates, preview, test-send isolation
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Business_preferences_are_sparse_and_only_store_deviations()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var audit = new RecordingAuditService();
        var service = CreateBusinessService(context, audit, new StubChannelAvailability(
            NotificationChannel.Email, NotificationChannel.Sms, NotificationChannel.Push));

        var owner = BookingTestBase.CreateOwner("prefs@test.com");
        context.Users.Add(owner);
        var business = BookingTestBase.CreateBusiness(owner.Id, "Prefs Salon");
        context.Businesses.Add(business);
        await context.SaveChangesAsync();

        // MARKETING defaults to OFF for every channel; turning email OFF is a
        // no-op (absence is already the default), turning it ON must persist.
        await service.UpdatePreferencesAsync(business.Id, new UpdateBusinessNotificationPreferencesRequest
        {
            Preferences =
            [
                new NotificationPreferenceItem { Category = NotificationCategory.Marketing, Channel = NotificationChannel.Email, Enabled = false },
                new NotificationPreferenceItem { Category = NotificationCategory.Marketing, Channel = NotificationChannel.Email, Enabled = true },
            ],
        }, owner.Id);

        var rows = await context.NotificationPreferences
            .Where(row => row.BusinessId == business.Id && row.UserId == null)
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(NotificationCategory.Marketing, row.Category);
        Assert.Equal(NotificationChannel.Email, row.Channel);
        Assert.True(row.Enabled);

        var matrix = await service.GetPreferencesAsync(business.Id);
        Assert.Equal(
            NotificationDefaults.AllCategories.Count * NotificationDefaults.AllChannels.Count,
            matrix.Count);
        Assert.Contains(
            matrix,
            entry => entry.Category == NotificationCategory.Marketing
                && entry.Channel == NotificationChannel.Email
                && entry.Enabled
                && entry.Source == PreferenceSource.Business);
    }

    [Fact]
    public async Task Business_template_catalogue_marks_overrides_and_protected_types()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateBusinessService(context, new RecordingAuditService(), new StubChannelAvailability());

        var owner = BookingTestBase.CreateOwner("templates@test.com");
        context.Users.Add(owner);
        var business = BookingTestBase.CreateBusiness(owner.Id, "Templates Salon");
        context.Businesses.Add(business);
        context.NotificationTemplateOverrides.Add(new NotificationTemplateOverride
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Type = "appointment.booked",
            Channel = NotificationChannel.Email,
            SubjectTemplate = "Your booking",
            BodyTemplateHtml = "<p>Hi {{businessName}}</p>",
            BodyTemplateText = "Hi {{businessName}}",
            Version = 3,
            UpdatedBy = "owner",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var templates = await service.GetTemplatesAsync(business.Id, null, null, null);

        var overridden = templates.First(t =>
            t.Type == "appointment.booked" && t.Channel == NotificationChannel.Email);
        Assert.Equal("override", overridden.Source);
        Assert.Equal(3, overridden.Version);

        var untouched = templates.First(t =>
            t.Type == "appointment.cancelled" && t.Channel == NotificationChannel.Email);
        Assert.Equal("default", untouched.Source);

        var security = templates.First(t => t.Type == "security.verification_code");
        Assert.True(security.Protected);

        var filtered = await service.GetTemplatesAsync(business.Id, null, null, NotificationChannel.Sms);
        Assert.All(filtered, template => Assert.Equal(NotificationChannel.Sms, template.Channel));
    }

    [Fact]
    public async Task Preview_flags_unknown_tokens_and_renders_known_ones()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateBusinessService(context, new RecordingAuditService(), new StubChannelAvailability());

        var owner = BookingTestBase.CreateOwner("preview@test.com");
        context.Users.Add(owner);
        var business = BookingTestBase.CreateBusiness(owner.Id, "Preview Salon");
        context.Businesses.Add(business);
        await context.SaveChangesAsync();

        var response = await service.PreviewAsync(business.Id, new BusinessNotificationPreviewRequest
        {
            Type = "appointment.booked",
            Channel = NotificationChannel.Sms,
            BodyTemplateText = "Hi {{businessName}} — see you at {{scheduledAt}}. {{MadeUpToken}}",
        });

        Assert.Contains("Preview Salon", response.Body);
        Assert.Contains("MadeUpToken", response.UnknownTokens);
        Assert.True(response.HasUnknownTokens);
    }

    [Fact]
    public async Task Test_send_rejects_an_unavailable_channel_and_records_nothing()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var smtp = new StubSmtpDelivery();
        var service = CreateBusinessService(
            context, new RecordingAuditService(), new StubChannelAvailability(NotificationChannel.Email), smtp);

        var owner = BookingTestBase.CreateOwner("testsend@test.com");
        context.Users.Add(owner);
        var business = BookingTestBase.CreateBusiness(owner.Id, "Test Send Salon");
        context.Businesses.Add(business);
        await context.SaveChangesAsync();

        var rejected = await service.TestSendAsync(business.Id, new BusinessNotificationTestSendRequest
        {
            Channel = NotificationChannel.Sms,
            Type = "appointment.booked",
            Recipient = "+254712345678",
        }, owner.Id);

        Assert.False(rejected.Accepted);
        Assert.Equal(0, smtp.Sends);
        Assert.Empty(await context.NotificationLogs.ToListAsync());
    }

    [Fact]
    public async Task Test_send_delivers_and_is_recorded_on_a_separate_ledger_key()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var smtp = new StubSmtpDelivery();
        var service = CreateBusinessService(
            context,
            new RecordingAuditService(),
            new StubChannelAvailability(NotificationChannel.Email),
            smtp,
            new EmailSettings { Enabled = true, FromAddress = "no-reply@punched.app", FromName = "Punched" });

        var owner = BookingTestBase.CreateOwner("testsend2@test.com");
        context.Users.Add(owner);
        var business = BookingTestBase.CreateBusiness(owner.Id, "Test Send Salon 2");
        context.Businesses.Add(business);
        await context.SaveChangesAsync();

        var result = await service.TestSendAsync(business.Id, new BusinessNotificationTestSendRequest
        {
            Channel = NotificationChannel.Email,
            Type = "appointment.booked",
            Recipient = "operator@example.com",
        }, owner.Id);

        Assert.True(result.Accepted);
        Assert.Equal(1, smtp.Sends);

        var row = Assert.Single(await context.NotificationLogs.ToListAsync());
        Assert.StartsWith(BusinessNotificationSettingsService.TestTemplateKeyPrefix, row.TemplateKey);
        Assert.Contains("__test", row.PayloadJson);
    }

    [Fact]
    public async Task Business_overview_excludes_test_rows_from_production_metrics()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateBusinessService(context, new RecordingAuditService(), new StubChannelAvailability());

        var owner = BookingTestBase.CreateOwner("overview@test.com");
        context.Users.Add(owner);
        var business = BookingTestBase.CreateBusiness(owner.Id, "Overview Salon");
        context.Businesses.Add(business);
        context.NotificationLogs.AddRange(
            new NotificationLog
            {
                Id = Guid.NewGuid(),
                UserId = owner.Id,
                BusinessId = business.Id,
                Channel = NotificationChannel.Email,
                TemplateType = "appointment.booked",
                Status = "sent",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            new NotificationLog
            {
                Id = Guid.NewGuid(),
                UserId = owner.Id,
                BusinessId = business.Id,
                Channel = NotificationChannel.Email,
                TemplateType = "appointment.booked",
                TemplateKey = BusinessNotificationSettingsService.TestTemplateKeyPrefix + NotificationChannel.Email,
                Status = "sent",
                PayloadJson = "{\"__test\":true}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        await context.SaveChangesAsync();

        var overview = await service.GetOverviewAsync(business.Id, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        Assert.Equal(1, overview.TotalNotifications);
        Assert.Equal(1, overview.Delivered);
        Assert.Equal("healthy", overview.SystemStatus);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Factories
    // ══════════════════════════════════════════════════════════════════

    private static AdminNotificationManagementService CreateAdminService(
        ApplicationDbContext context,
        RecordingAuditService audit,
        params INotificationChannel[] channels) =>
        new(
            context,
            audit,
            new TemplateOverrideResolver(context, TestHelpers.CreateLogger<TemplateOverrideResolver>()),
            channels,
            Options.Create(new EmailSettings
            {
                Enabled = true,
                Host = "smtp.test",
                Port = 587,
                FromAddress = "no-reply@punched.app",
                FromName = "Punched",
                Username = "smtp-user",
                Password = "smtp-secret-value",
            }),
            Options.Create(new SmsSettings()),
            Options.Create(new VapidSettings()),
            TestHelpers.CreateLogger<AdminNotificationManagementService>());

    private static BusinessNotificationSettingsService CreateBusinessService(
        ApplicationDbContext context,
        RecordingAuditService audit,
        StubChannelAvailability availability,
        StubSmtpDelivery? smtp = null,
        EmailSettings? email = null,
        SmsSettings? sms = null)
    {
        var renderer = new TemplateRenderer(TestHelpers.CreateLogger<TemplateRenderer>());
        return new BusinessNotificationSettingsService(
            context,
            availability,
            new TemplateOverrideResolver(context, TestHelpers.CreateLogger<TemplateOverrideResolver>()),
            renderer,
            new EmailLayoutRenderer(renderer, TestHelpers.CreateLogger<EmailLayoutRenderer>()),
            audit,
            smtp ?? new StubSmtpDelivery(),
            new AfricaTalkingClient(new HttpClient(), Options.Create(sms ?? new SmsSettings())),
            Options.Create(email ?? new EmailSettings()),
            TestHelpers.CreateLogger<BusinessNotificationSettingsService>());
    }
}
