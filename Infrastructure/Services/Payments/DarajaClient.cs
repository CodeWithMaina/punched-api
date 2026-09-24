using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Settings;

namespace PunchedApi.Infrastructure.Services.Payments;

/// <summary>
/// Real Safaricom Daraja HTTP client. Handles OAuth (with in-process token cache
/// refreshed ahead of expiry), STK Push and the transaction status query.
/// Base URLs: sandbox.safaricom.co.ke (sandbox) / api.safaricom.co.ke (production).
/// Callback URLs must be HTTPS 443 and must NOT contain the strings mpesa or
/// safaricom — enforced by configuration, validated in DarajaStkProvider.
/// </summary>
public class DarajaClient : IDarajaClient
{
    private readonly HttpClient _http;
    private readonly DarajaOptions _options;
    private readonly ILogger<DarajaClient> _logger;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTime _tokenExpiresAtUtc = DateTime.MinValue;

    public DarajaClient(HttpClient http, IOptions<DarajaOptions> options, ILogger<DarajaClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    private string BaseUrl => _options.Sandbox ? "https://sandbox.safaricom.co.ke" : "https://api.safaricom.co.ke";

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedToken != null && DateTime.UtcNow < _tokenExpiresAtUtc)
            return _cachedToken;

        if (string.IsNullOrEmpty(_options.ConsumerKey) || string.IsNullOrEmpty(_options.ConsumerSecret))
        {
            _logger.LogError("Daraja OAuth attempted without configured credentials.");
            return null;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedToken != null && DateTime.UtcNow < _tokenExpiresAtUtc)
                return _cachedToken;

            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes(_options.ConsumerKey + ":" + _options.ConsumerSecret));
            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/oauth/v1/generate?grant_type=client_credentials");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);

            var resp = await _http.SendAsync(req, cancellationToken);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
            var root = doc.RootElement;
            var token = root.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            // expires_in arrives as a STRING in Daraja — parse defensively.
            var expiresIn = root.TryGetProperty("expires_in", out var e)
                && int.TryParse(e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText(), out var secs)
                ? secs : 3599;

            _cachedToken = token;
            // Refresh 60s before expiry.
            _tokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task<StkPushResponse> StkPushAsync(StkPushRequest request, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        if (token == null)
            return new StkPushResponse { Success = false, ErrorCode = "NO_CREDENTIALS", ErrorMessage = "Daraja credentials are not configured." };

        var timestamp = NairobiTimestamp();
        var password = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.BusinessShortCode + request.Passkey + timestamp));

        var payload = new
        {
            BusinessShortCode = request.BusinessShortCode,
            Password = password,
            Timestamp = timestamp,
            TransactionType = request.TransactionType,
            Amount = (int)request.Amount, // Daraja STK accepts integers only
            PartyA = request.PhoneNumber,
            PartyB = request.BusinessShortCode,
            PhoneNumber = request.PhoneNumber,
            CallBackURL = request.CallbackUrl,
            AccountReference = Truncate(request.AccountReference, 12),
            TransactionDesc = Truncate(request.TransactionDesc, 13)
        };

        return await PostAsync(BaseUrl + "/mpesa/stkpush/v1/processrequest", payload, token, cancellationToken);
    }

    public Task<StkPushResponse> QueryStatusAsync(string checkoutRequestId, string shortCode, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "The Daraja Transaction Status API requires Safaricom-approved production credentials (initiator + security credential). " +
            "Punched stores this as PROPOSED until production access is granted.");

    public async Task<bool> RegisterC2BUrlsAsync(string shortCode, string responseType, string confirmationUrl, string validationUrl, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        if (token == null) return false;

        var payload = new { ShortCode = shortCode, ResponseType = responseType, ConfirmationURL = confirmationUrl, ValidationURL = validationUrl };
        var result = await PostAsync(BaseUrl + "/mpesa/c2b/v1/registerurl", payload, token, cancellationToken);
        return result.Success;
    }

    private async Task<StkPushResponse> PostAsync(string url, object payload, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resp = await _http.SendAsync(req, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var result = StkPushResponse.FromJson(doc.RootElement);

            if (!resp.IsSuccessStatusCode && result.ErrorCode == null)
            {
                result.ErrorCode = "HTTP_" + (int)resp.StatusCode;
                result.Retryable = (int)resp.StatusCode >= 500 || (int)resp.StatusCode == 429;
            }
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Daraja request failed (transient): {Url}", url);
            return new StkPushResponse { Success = false, ErrorCode = "NETWORK_ERROR", ErrorMessage = ex.Message, Retryable = true };
        }
    }

    private static string NairobiTimestamp()
    {
        var nairobi = TimeZoneInfo.FindSystemTimeZoneById("Africa/Nairobi");
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, nairobi);
        return now.ToString("yyyyMMddHHmmss");
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
