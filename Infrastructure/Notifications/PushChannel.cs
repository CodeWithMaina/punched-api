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
    private readonly ILogger<PushChannel> _logger;

    public PushChannel(
        ApplicationDbContext context,
        IOptions<VapidSettings> settings,
        ILogger<PushChannel> logger)
    {
        _context = context;
        _settings = settings.Value;
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
            tag = "punched-notification",
            data = new { url = deepLink, type = message.Type }
        });

        var vapid = new VapidDetails(_settings.Subject, _settings.PublicKey, _settings.PrivateKey);
        var client = new WebPushClient();

        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await client.SendNotificationAsync(
                    new PushSubscription(device.Endpoint, device.P256dh, device.Auth),
                    payload,
                    vapid,
                    ct);
                device.LastSeenAt = DateTime.UtcNow;
            }
            catch (WebPushException ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.StatusCode == HttpStatusCode.Gone)
            {
                device.IsActive = false;
                device.LastSeenAt = DateTime.UtcNow;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (WebPushException ex)
            {
                _logger.LogWarning(ex, "Web push notification failed for device {DeviceId} on user {UserId}.", device.Id, message.RecipientUserId);
                throw new RetryableChannelException("Web push delivery failed temporarily.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Web push notification failed for device {DeviceId} on user {UserId}.", device.Id, message.RecipientUserId);
                throw new RetryableChannelException("Web push delivery failed temporarily.", ex);
            }
        }

        await _context.SaveChangesAsync(ct);
    }

    private static string BuildDeepLink(ChannelMessage message)
    {
        if (message.Data.TryGetValue("appointmentId", out var appointmentId) && appointmentId is not null)
            return $"/dashboard/appointments/{appointmentId}";

        return "/dashboard/notifications";
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
