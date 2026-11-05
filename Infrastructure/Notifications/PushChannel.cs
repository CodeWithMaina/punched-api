using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebPush;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Settings;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Infrastructure.Notifications;

public sealed class PushChannel : INotificationChannel
{
    private readonly ApplicationDbContext _context;
    private readonly VapidSettings _settings;
    private readonly IPushNotificationSender _sender;
    private readonly ILogger<PushChannel> _logger;

    public PushChannel(
        ApplicationDbContext context,
        IOptions<VapidSettings> settings,
        IPushNotificationSender sender,
        ILogger<PushChannel> logger)
    {
        _context = context;
        _settings = settings.Value;
        _sender = sender;
        _logger = logger;
    }

    public string Name => NotificationChannel.Push;

    public async Task SendAsync(ChannelMessage message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_settings.Enabled || string.IsNullOrWhiteSpace(_settings.PublicKey) || string.IsNullOrWhiteSpace(_settings.PrivateKey))
            throw new PermanentChannelException("Web push is not configured for this deployment.");

        var devices = await _context.PushDevices
            .Where(device => device.UserId == message.RecipientUserId && device.IsActive)
            .ToListAsync(ct);

        if (devices.Count == 0)
            return;

        var title = GetValue(message.Data, "renderedTitle");
        var body = GetValue(message.Data, "renderedBody");
        var deepLink = BuildDeepLink(message);
        var payload = JsonSerializer.Serialize(new
        {
            title,
            body,
            icon = "/icons/icon-192.svg",
            badge = "/icons/icon-192.svg",
            // Coalesce per appointment (a reminder ladder replaces its own earlier
            // banner) instead of a single constant tag that made every notification
            // overwrite the last one.
            tag = BuildTag(message),
            data = new { url = deepLink, type = message.Type }
        });

        var vapid = new VapidDetails(_settings.Subject, _settings.PublicKey, _settings.PrivateKey);

        var delivered = 0;
        var failed = 0;
        Exception? lastFailure = null;

        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _sender.SendAsync(
                    new PushSubscription(device.Endpoint, device.P256dh, device.Auth),
                    payload,
                    vapid,
                    ct);
                device.LastSeenAt = DateTime.UtcNow;
                delivered++;
            }
            catch (WebPushException ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.StatusCode == HttpStatusCode.Gone)
            {
                // 404/410 — the browser discarded the subscription. Prune the device
                // so we never retry a dead endpoint, but do not fail the whole row:
                // other devices for the same user may still be reachable.
                device.IsActive = false;
                device.LastSeenAt = DateTime.UtcNow;
                _logger.LogInformation(
                    "Deactivated web push device {DeviceId} for user {UserId} after HTTP {StatusCode}.",
                    device.Id, message.RecipientUserId, (int)ex.StatusCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One bad endpoint must never stop the others, and a partial success
                // must not be retried (that would re-notify devices already reached).
                failed++;
                lastFailure = ex;
                _logger.LogWarning(
                    ex,
                    "Web push notification failed for device {DeviceId} on user {UserId}.",
                    device.Id, message.RecipientUserId);
            }
        }

        // Persist pruning and LastSeen even when some devices failed, so a retry
        // never re-sends to a device that already received the message.
        await _context.SaveChangesAsync(ct);

        if (delivered == 0 && failed > 0)
            throw new RetryableChannelException("Web push delivery failed for every device.", lastFailure);
    }

    private static string BuildDeepLink(ChannelMessage message)
    {
        if (message.Data.TryGetValue("appointmentId", out var appointmentId) && appointmentId is not null)
            return $"/dashboard/appointments/{GetValue(message.Data, "appointmentId")}";

        return "/dashboard/notifications";
    }

    /// <summary>
    /// Notification `tag`. A per-appointment tag lets a reminder ladder replace its
    /// own earlier banner while leaving unrelated notifications intact; without an
    /// appointment the type is the grouping key.
    /// </summary>
    private static string BuildTag(ChannelMessage message)
    {
        if (message.Data.TryGetValue("appointmentId", out var appointmentId) && appointmentId is not null)
        {
            var value = GetValue(message.Data, "appointmentId");
            if (!string.IsNullOrWhiteSpace(value)) return $"appointment-{value}";
        }

        return $"punched-{message.Type}";
    }

    private static string GetValue(IReadOnlyDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null) return string.Empty;
        return value switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonElement element => element.ToString(),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
        };
    }
}
