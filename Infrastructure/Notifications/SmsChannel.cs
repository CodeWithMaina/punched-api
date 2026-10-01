using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Settings;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Infrastructure.Notifications;

public sealed class SmsChannel : INotificationChannel
{
    private readonly ApplicationDbContext _context;
    private readonly SmsSettings _settings;
    private readonly AfricaTalkingClient _africaTalkingClient;
    private readonly ILogger<SmsChannel> _logger;

    public SmsChannel(
        ApplicationDbContext context,
        IOptions<SmsSettings> settings,
        AfricaTalkingClient africaTalkingClient,
        ILogger<SmsChannel> logger)
    {
        _context = context;
        _settings = settings.Value;
        _africaTalkingClient = africaTalkingClient;
        _logger = logger;
    }

    public string Name => NotificationChannel.Sms;

    public async Task SendAsync(ChannelMessage message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_settings.Enabled)
            throw new PermanentChannelException("SMS delivery is disabled.");

        var user = await _context.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == message.RecipientUserId && !u.IsDeleted, ct);

        if (user is null || string.IsNullOrWhiteSpace(user.PhoneNumber))
            throw new PermanentChannelException("The notification recipient has no mobile number configured.");

        var body = GetValue(message.Data, "renderedBody");
        if (string.IsNullOrWhiteSpace(body))
            throw new PermanentChannelException("The SMS body is empty.");

        try
        {
            await _africaTalkingClient.SendAsync(user.PhoneNumber, body, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            throw new PermanentChannelException(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            throw new RetryableChannelException("SMS delivery failed temporarily.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SMS delivery failed for outbox {OutboxId}.", message.OutboxId);
            throw new RetryableChannelException("SMS delivery failed temporarily.", ex);
        }
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
