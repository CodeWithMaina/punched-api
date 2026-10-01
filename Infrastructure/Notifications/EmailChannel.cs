using System.IO;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Settings;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Infrastructure.Notifications;

public sealed class EmailChannel : INotificationChannel
{
    private readonly ApplicationDbContext _context;
    private readonly EmailSettings _settings;
    private readonly INotificationSmtpDelivery _smtpDelivery;
    private readonly ILogger<EmailChannel> _logger;

    public EmailChannel(
        ApplicationDbContext context,
        IOptions<EmailSettings> settings,
        INotificationSmtpDelivery smtpDelivery,
        ILogger<EmailChannel> logger)
    {
        _context = context;
        _settings = settings.Value;
        _smtpDelivery = smtpDelivery;
        _logger = logger;
    }

    public string Name => NotificationChannel.Email;

    public async Task SendAsync(ChannelMessage message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_settings.Enabled)
            throw new PermanentChannelException("Email notifications are not enabled.");

        var recipient = await _context.Users
            .Include(user => user.Auth)
            .SingleOrDefaultAsync(user => user.Id == message.RecipientUserId && !user.IsDeleted, ct);
        if (recipient is null || string.IsNullOrWhiteSpace(recipient.Email) || recipient.Auth?.IsVerified != true)
            throw new PermanentChannelException("The notification recipient is not eligible for email delivery.");

        if (string.IsNullOrWhiteSpace(_settings.FromAddress))
            throw new PermanentChannelException("Email sender configuration is incomplete.");

        var subject = GetValue(message.Data, "renderedSubject");
        var title = GetValue(message.Data, "renderedTitle");
        var textBody = GetValue(message.Data, "renderedBody");
        var htmlBody = GetValue(message.Data, "renderedHtmlBody");
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_settings.FromName, _settings.FromAddress));
        mime.To.Add(new MailboxAddress(recipient.FullName, recipient.Email));
        mime.Subject = string.IsNullOrWhiteSpace(subject) ? title : subject;
        mime.Body = new BodyBuilder
        {
            TextBody = textBody,
            HtmlBody = $"<h1>{System.Net.WebUtility.HtmlEncode(title)}</h1><p>{htmlBody}</p>"
        }.ToMessageBody();

        try
        {
            await _smtpDelivery.SendAsync(mime, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SmtpCommandException ex) when ((int)ex.StatusCode is >= 400 and < 500)
        {
            throw new RetryableChannelException("SMTP temporarily rejected notification delivery.");
        }
        catch (SmtpCommandException ex)
        {
            throw new PermanentChannelException($"SMTP permanently rejected notification delivery ({(int)ex.StatusCode}).");
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or SmtpProtocolException or ServiceNotConnectedException)
        {
            throw new RetryableChannelException("SMTP notification delivery failed temporarily.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Email notification delivery failed for outbox {OutboxId} ({FailureType}).",
                message.OutboxId,
                ex.GetType().Name);
            throw new RetryableChannelException("Email notification delivery failed temporarily.");
        }
    }

    private static string GetValue(IReadOnlyDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null) return string.Empty;
        return value is System.Text.Json.JsonElement element
            ? element.ValueKind == System.Text.Json.JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString()
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}