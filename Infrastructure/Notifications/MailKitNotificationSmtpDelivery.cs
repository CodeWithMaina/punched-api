using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using PunchedApi.Application.Settings;

namespace PunchedApi.Infrastructure.Notifications;

public sealed class MailKitNotificationSmtpDelivery : INotificationSmtpDelivery, IAsyncDisposable
{
    private readonly EmailSettings _settings;
    private SmtpClient? _client;

    public MailKitNotificationSmtpDelivery(IOptions<EmailSettings> settings) => _settings = settings.Value;

    public async Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var client = await GetConnectedClientAsync(cancellationToken);
            await client.SendAsync(message, cancellationToken);
        }
        catch
        {
            await ResetClientAsync();
            throw;
        }
    }

    private async Task<SmtpClient> GetConnectedClientAsync(CancellationToken cancellationToken)
    {
        _client ??= new SmtpClient { Timeout = checked(_settings.TimeoutSeconds * 1000) };
        if (!_client.IsConnected)
        {
            var secureOption = _settings.Port == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;
            await _client.ConnectAsync(_settings.Host, _settings.Port, secureOption, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(_settings.Username) && !_client.IsAuthenticated)
            await _client.AuthenticateAsync(_settings.Username, _settings.Password, cancellationToken);

        return _client;
    }

    private async Task ResetClientAsync()
    {
        if (_client is null) return;
        try
        {
            if (_client.IsConnected)
                await _client.DisconnectAsync(quit: false, CancellationToken.None);
        }
        catch
        {
            // The next send creates a fresh connection regardless of reset outcome.
        }

        _client.Dispose();
        _client = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null) return;
        if (_client.IsConnected)
            await _client.DisconnectAsync(quit: true, CancellationToken.None);
        _client.Dispose();
        _client = null;
    }
}