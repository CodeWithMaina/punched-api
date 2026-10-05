using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Settings;

namespace PunchedApi.Infrastructure.Services.Payments;

/// <summary>
/// Deterministic dev/test Daraja client. Used ONLY when a business (or the platform)
/// has no real Daraja credentials and the environment explicitly allows the mock
/// (Daraja:AllowMockClient, non-production). It simulates the STK accept + async
/// callback contract: the push "succeeds" synchronously and payment completion is
/// driven by delivering the simulated callback through the real callback pipeline
/// (see PaymentTests). It must NEVER be used in production — no fabricated money.
/// </summary>
public class FakeDarajaClient : IDarajaClient
{
    private readonly DarajaOptions _options;
    private readonly ILogger<FakeDarajaClient> _logger;

    public FakeDarajaClient(IOptions<DarajaOptions> options, ILogger<FakeDarajaClient> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<string?>("fake-access-token");

    public Task<StkPushResponse> StkPushAsync(StkPushRequest request, CancellationToken cancellationToken = default)
    {
        if (!_options.AllowMockClient || !string.IsNullOrEmpty(_options.ConsumerKey))
            return Task.FromResult(new StkPushResponse
            {
                Success = false,
                ErrorCode = "MOCK_DISABLED",
                ErrorMessage = "FakeDarajaClient is disabled (production credentials configured or mock not allowed)."
            });

        var checkoutId = "ws_CO_FAKE_" + Guid.NewGuid().ToString("N")[..16];
        _logger.LogInformation("FakeDarajaClient: simulated STK push accepted (checkout {CheckoutId} for {ShortCode}).", checkoutId, request.BusinessShortCode);

        return Task.FromResult(new StkPushResponse
        {
            Success = true,
            MerchantRequestId = "FAKE-" + Guid.NewGuid().ToString("N")[..12],
            CheckoutRequestId = checkoutId,
            ResponseCode = "0",
            ResponseDescription = "Success. Request accepted for processing (SIMULATED).",
            CustomerMessage = "Simulated M-PESA prompt sent."
        });
    }

    public Task<StkPushResponse> QueryStatusAsync(string checkoutRequestId, string shortCode, CancellationToken cancellationToken = default)
        => Task.FromResult(new StkPushResponse { Success = false, ErrorCode = "STATUS_NOT_SUPPORTED", ErrorMessage = "Transaction status requires Safaricom production approval." });

    public Task<bool> RegisterC2BUrlsAsync(string shortCode, string responseType, string confirmationUrl, string validationUrl, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("FakeDarajaClient: simulated C2B register-url for {ShortCode}.", shortCode);
        return Task.FromResult(true);
    }
}
