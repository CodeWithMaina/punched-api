using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Moq;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Notifications;

namespace PunchedApi.Tests.Notifications;

public class EmailChannelTests
{
    [Fact]
    public void Html_renderer_encodes_script_values()
    {
        var renderer = new TemplateRenderer(TestHelpers.CreateLogger<TemplateRenderer>());

        var result = renderer.RenderHtml("<p>{{businessName}}</p>",
            new Dictionary<string, object?> { ["businessName"] = "<script>alert(1)</script>" });

        Assert.Equal("<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>", result);
    }

    [Fact]
    public void Unresolved_token_is_empty_and_logs_warning()
    {
        var logger = new RecordingLogger<TemplateRenderer>();
        var renderer = new TemplateRenderer(logger);

        var result = renderer.RenderText("Hello {{businessName}}!", new Dictionary<string, object?>());

        Assert.Equal("Hello !", result);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("businessName"));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("recipient@example.com", false)]
    public async Task Missing_or_unverified_recipient_fails_permanently(string email, bool verified)
    {
        await using var context = CreateContext();
        var recipient = CreateUser(email, verified);
        context.Users.Add(recipient);
        await context.SaveChangesAsync();
        var channel = CreateChannel(context, new CapturingSmtpDelivery());

        await Assert.ThrowsAsync<PermanentChannelException>(() => channel.SendAsync(Message(recipient.Id)));
    }

    [Fact]
    public async Task Happy_path_builds_mime_with_configured_from_and_user_resolved_to()
    {
        await using var context = CreateContext();
        var recipient = CreateUser("actual-recipient@example.com", verified: true);
        context.Users.Add(recipient);
        await context.SaveChangesAsync();
        var smtp = new CapturingSmtpDelivery();
        var channel = CreateChannel(context, smtp);
        var message = Message(recipient.Id, new Dictionary<string, object?>
        {
            ["renderedSubject"] = "Appointment booked",
            ["renderedTitle"] = "Appointment booked",
            ["renderedBody"] = "Your appointment is booked.",
            ["renderedHtmlBody"] = "Your appointment is booked.",
            ["email"] = "attacker@example.com"
        });

        await channel.SendAsync(message);

        var mime = Assert.IsType<MimeMessage>(smtp.Message);
        Assert.Equal("sender@example.com", mime.From.Mailboxes.Single().Address);
        Assert.Equal("actual-recipient@example.com", mime.To.Mailboxes.Single().Address);
        Assert.Equal("Appointment booked", mime.Subject);
        Assert.Contains("Your appointment is booked.", mime.TextBody);
        Assert.Contains("Your appointment is booked.", mime.HtmlBody);
    }

    [Fact]
    public async Task Transient_transport_failure_is_retryable()
    {
        await using var context = CreateContext();
        var recipient = CreateUser("recipient@example.com", verified: true);
        context.Users.Add(recipient);
        await context.SaveChangesAsync();
        var channel = CreateChannel(context, new CapturingSmtpDelivery { Exception = new IOException("socket closed") });

        await Assert.ThrowsAsync<RetryableChannelException>(() => channel.SendAsync(Message(recipient.Id)));
    }

    [Fact]
    public async Task Provider_exception_details_are_not_logged_or_attached_to_retryable_failure()
    {
        await using var context = CreateContext();
        const string recipientAddress = "recipient@example.com";
        var recipient = CreateUser(recipientAddress, verified: true);
        context.Users.Add(recipient);
        await context.SaveChangesAsync();
        var logger = new RecordingLogger<EmailChannel>();
        var channel = CreateChannel(
            context,
            new CapturingSmtpDelivery { Exception = new InvalidOperationException($"Rejected {recipientAddress}") },
            logger);

        var failure = await Assert.ThrowsAsync<RetryableChannelException>(() => channel.SendAsync(Message(recipient.Id)));

        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(recipientAddress, string.Join(" ", logger.Entries.Select(entry => entry.Message)));
    }

    [Fact]
    public async Task Cancellation_is_honoured_before_lookup_or_delivery()
    {
        await using var context = CreateContext();
        using var source = new CancellationTokenSource();
        source.Cancel();
        var channel = CreateChannel(context, new CapturingSmtpDelivery());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.SendAsync(Message(Guid.NewGuid()), source.Token));
    }

    [Fact]
    public async Task Marketing_message_carries_one_click_unsubscribe_and_absolute_link()
    {
        await using var context = CreateContext();
        var recipient = CreateUser("marketing-recipient@example.com", verified: true);
        context.Users.Add(recipient);
        await context.SaveChangesAsync();
        var smtp = new CapturingSmtpDelivery();
        var channel = CreateChannel(context, smtp);

        await channel.SendAsync(Message(recipient.Id, category: NotificationCategory.Marketing));

        var mime = Assert.IsType<MimeMessage>(smtp.Message);
        Assert.Equal(
            "<http://localhost:5091/v1/notifications/unsubscribe?token=test-token>",
            mime.Headers["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", mime.Headers["List-Unsubscribe-Post"]);
        Assert.Contains(
            "http://localhost:5091/v1/notifications/unsubscribe?token=test-token",
            mime.HtmlBody);
        // The conditional layout must never leak block syntax into an email.
        Assert.DoesNotContain("{{#if", mime.HtmlBody);
        Assert.DoesNotContain("{{^if", mime.HtmlBody);
        Assert.DoesNotContain("{{/if}}", mime.HtmlBody);
    }

    [Fact]
    public async Task Transactional_message_has_no_unsubscribe_headers()
    {
        await using var context = CreateContext();
        var recipient = CreateUser("reminder-recipient@example.com", verified: true);
        context.Users.Add(recipient);
        await context.SaveChangesAsync();
        var smtp = new CapturingSmtpDelivery();
        var channel = CreateChannel(context, smtp);

        await channel.SendAsync(Message(recipient.Id));

        var mime = Assert.IsType<MimeMessage>(smtp.Message);
        Assert.False(mime.Headers.Contains("List-Unsubscribe"));
        Assert.False(mime.Headers.Contains("List-Unsubscribe-Post"));
    }

    [Fact]
    public async Task Business_profile_overrides_from_display_name_and_reply_to()
    {
        await using var context = CreateContext();
        var recipient = CreateUser("branded-recipient@example.com", verified: true);
        var businessId = Guid.NewGuid();
        context.Users.Add(recipient);
        context.BusinessMessagingProfiles.Add(new BusinessMessagingProfile
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            DisplayName = "Java House",
            ReplyTo = "hello@javahouse.example"
        });
        await context.SaveChangesAsync();
        var smtp = new CapturingSmtpDelivery();
        var channel = CreateChannel(context, smtp);

        await channel.SendAsync(Message(recipient.Id, businessId: businessId));

        var mime = Assert.IsType<MimeMessage>(smtp.Message);
        Assert.Equal("sender@example.com", mime.From.Mailboxes.Single().Address);
        Assert.Equal("Java House", mime.From.Mailboxes.Single().Name);
        Assert.Equal("hello@javahouse.example", mime.ReplyTo.Mailboxes.Single().Address);
        Assert.Contains("Java House", mime.HtmlBody);
        Assert.DoesNotContain("{{", mime.HtmlBody);
    }

    [Fact]
    public async Task Brand_text_renders_when_no_logo_is_configured()
    {
        await using var context = CreateContext();
        var recipient = CreateUser("no-logo@example.com", verified: true);
        var businessId = Guid.NewGuid();
        context.Users.Add(recipient);
        context.BusinessMessagingProfiles.Add(new BusinessMessagingProfile
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            DisplayName = "Style Studio"
        });
        await context.SaveChangesAsync();
        var smtp = new CapturingSmtpDelivery();
        var channel = CreateChannel(context, smtp);

        await channel.SendAsync(Message(recipient.Id, businessId: businessId));

        var mime = Assert.IsType<MimeMessage>(smtp.Message);
        Assert.Contains("Style Studio", mime.HtmlBody);
        Assert.DoesNotContain("<img", mime.HtmlBody);
        Assert.DoesNotContain("{{^if", mime.HtmlBody);
        Assert.DoesNotContain("{{/if}}", mime.HtmlBody);
    }

    private static ApplicationDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static User CreateUser(string email, bool verified) => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        FullName = "Test Recipient",
        Role = UserRole.Customer,
        Auth = new UserAuth
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = "hash",
            IsVerified = verified
        }
    };

    private static EmailChannel CreateChannel(
        ApplicationDbContext context,
        INotificationSmtpDelivery smtp,
        ILogger<EmailChannel>? logger = null)
    {
        var unsubscribeTokens = new Mock<IUnsubscribeTokenService>();
        unsubscribeTokens
            .Setup(t => t.CreateToken(It.IsAny<Guid>(), It.IsAny<Guid?>()))
            .Returns("test-token");

        return new EmailChannel(
            context,
            Options.Create(new EmailSettings
            {
                Enabled = true,
                FromAddress = "sender@example.com",
                FromName = "Punched"
            }),
            smtp,
            new EmailLayoutRenderer(
                new TemplateRenderer(TestHelpers.CreateLogger<TemplateRenderer>()),
                TestHelpers.CreateLogger<EmailLayoutRenderer>()),
            unsubscribeTokens.Object,
            Options.Create(new PublicAppSettings
            {
                BaseUrl = "http://localhost:3000",
                ApiBaseUrl = "http://localhost:5091"
            }),
            logger ?? TestHelpers.CreateLogger<EmailChannel>());
    }

    private static ChannelMessage Message(
        Guid recipientId,
        IReadOnlyDictionary<string, object?>? data = null,
        string category = NotificationCategory.Appointment,
        Guid? businessId = null) => new(
        Guid.NewGuid(),
        "appointment.booked",
        recipientId,
        businessId,
        category,
        data ?? new Dictionary<string, object?>
        {
            ["renderedSubject"] = "Subject",
            ["renderedTitle"] = "Title",
            ["renderedBody"] = "Body",
            ["renderedHtmlBody"] = "Body"
        });

    private sealed class CapturingSmtpDelivery : INotificationSmtpDelivery
    {
        public MimeMessage? Message { get; private set; }
        public Exception? Exception { get; init; }

        public Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Exception is not null) throw Exception;
            Message = message;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}