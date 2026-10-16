using System.IO;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Infrastructure.Notifications;

public sealed class EmailChannel : INotificationChannel
{
    private readonly ApplicationDbContext _context;
    private readonly EmailSettings _settings;
    private readonly INotificationSmtpDelivery _smtpDelivery;
    private readonly EmailLayoutRenderer _layout;
    private readonly IUnsubscribeTokenService _unsubscribeTokens;
    private readonly PublicAppSettings _publicApp;
    private readonly ILogger<EmailChannel> _logger;

    public EmailChannel(
        ApplicationDbContext context,
        IOptions<EmailSettings> settings,
        INotificationSmtpDelivery smtpDelivery,
        EmailLayoutRenderer layout,
        IUnsubscribeTokenService unsubscribeTokens,
        IOptions<PublicAppSettings> publicApp,
        ILogger<EmailChannel> logger)
    {
        _context = context;
        _settings = settings.Value;
        _smtpDelivery = smtpDelivery;
        _layout = layout;
        _unsubscribeTokens = unsubscribeTokens;
        _publicApp = publicApp.Value;
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

        // Phase 4 — per-tenant branding and sender identity; platform defaults
        // apply whenever the business has no messaging profile.
        var profile = await ResolveProfileAsync(message.BusinessId, ct);
        var isMarketing = message.Category == NotificationCategory.Marketing;

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(
            SanitizeDisplayName(string.IsNullOrWhiteSpace(profile?.DisplayName) ? _settings.FromName : profile!.DisplayName),
            _settings.FromAddress));
        mime.To.Add(new MailboxAddress(recipient.FullName, recipient.Email));
        if (MailboxAddress.TryParse(profile?.ReplyTo, out var replyTo))
            mime.ReplyTo.Add(replyTo);
        mime.Subject = string.IsNullOrWhiteSpace(subject) ? title : subject;

        var apiBase = ApiBase();
        var extraTokens = new Dictionary<string, object?>();
        if (profile?.LogoMediaId != null)
            extraTokens["logoUrl"] = $"{apiBase}/v1/media/public/{profile.LogoMediaId:D}";

        string? unsubscribeUrl = null;
        if (isMarketing)
        {
            var token = _unsubscribeTokens.CreateToken(message.RecipientUserId, message.BusinessId);
            unsubscribeUrl = $"{apiBase}/v1/notifications/unsubscribe?token={Uri.EscapeDataString(token)}";
            extraTokens["unsubscribeLink"] = unsubscribeUrl;
        }

        var contentHtml = string.IsNullOrWhiteSpace(title)
            ? htmlBody
            : $"<h1>{System.Net.WebUtility.HtmlEncode(title)}</h1>{htmlBody}";

        mime.Body = new BodyBuilder
        {
            TextBody = textBody,
            HtmlBody = _layout.Render(profile, mime.Subject ?? string.Empty, contentHtml, isMarketing, extraTokens)
        }.ToMessageBody();

        if (unsubscribeUrl != null)
        {
            // RFC 8058 — lets mail providers offer one-click unsubscribe from the
            // message header itself. Marketing mail only; transactional never.
            mime.Headers.Add("List-Unsubscribe", $"<{unsubscribeUrl}>");
            mime.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
        }

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

    private async Task<BusinessMessagingProfile?> ResolveProfileAsync(Guid? businessId, CancellationToken ct)
    {
        if (businessId == null) return null;
        return await _context.BusinessMessagingProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.BusinessId == businessId.Value, ct);
    }

    /// <summary>Public API origin for absolute URLs embedded in outbound email.</summary>
    private string ApiBase() =>
        (!string.IsNullOrWhiteSpace(_publicApp.ApiBaseUrl) ? _publicApp.ApiBaseUrl : _publicApp.BaseUrl).TrimEnd('/');

    /// <summary>
    /// Strips control characters (CR/LF above all) — a display name must never be
    /// able to split or inject a header — and bounds it to the profile's 200 chars.
    /// </summary>
    private static string SanitizeDisplayName(string name)
    {
        var cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0) return "Punched";
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    private static string GetValue(IReadOnlyDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null) return string.Empty;
        return value is System.Text.Json.JsonElement element
            ? element.ValueKind == System.Text.Json.JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString()
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}