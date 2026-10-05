using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Settings;

namespace PunchedApi.Infrastructure.Notifications;

public sealed class AfricaTalkingClient
{
    private readonly HttpClient _http;
    private readonly SmsSettings _settings;

    public AfricaTalkingClient(HttpClient http, IOptions<SmsSettings> settings)
    {
        _http = http;
        _settings = settings.Value;
        _http.BaseAddress = new Uri("https://api.africastalking.com");
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task SendAsync(string phoneNumber, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.Username) || string.IsNullOrWhiteSpace(_settings.ApiKey))
            throw new InvalidOperationException("Africa's Talking credentials are not configured.");

        var normalized = NormalizePhoneNumber(phoneNumber);
        var payload = new
        {
            username = _settings.Username,
            to = new[] { normalized },
            message,
            from = string.IsNullOrWhiteSpace(_settings.SenderId) ? null : _settings.SenderId
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/version1/messaging")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("apiKey", _settings.ApiKey);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Africa's Talking SMS request failed: {response.StatusCode} {content}");
        }
    }

    private static string NormalizePhoneNumber(string phoneNumber)
    {
        var cleaned = new string(phoneNumber.Where(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(cleaned))
            throw new InvalidOperationException("The phone number is invalid.");

        if (cleaned.Length > 9 && cleaned.StartsWith("254", StringComparison.Ordinal))
            return "+" + cleaned;

        if (!cleaned.StartsWith("+", StringComparison.Ordinal) && cleaned.Length >= 9)
            return "+" + cleaned;

        return cleaned;
    }
}
