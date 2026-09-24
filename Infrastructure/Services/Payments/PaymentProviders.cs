using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Services;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Infrastructure.Services.Payments;

/// <summary>
/// Daraja STK Push provider. Resolves the business's OWN credentials (tenant
/// isolation: business A can never push against business B's shortcode) and falls
/// back to the platform sandbox client in dev. Builds the callback URL from
/// Daraja:CallbackBaseUrl. Automated reversal is PROPOSED (requires Safaricom
/// production initiator credentials) — the provider reports Supported=false so the
/// domain records an audited manual refund instead of pretending.
/// </summary>
public class DarajaStkProvider : IPaymentProvider
{
    private readonly IDarajaClient _client;
    private readonly ApplicationDbContext _context;
    private readonly PaymentCredentialProtector _protector;
    private readonly DarajaOptions _options;
    private readonly ILogger<DarajaStkProvider> _logger;

    public DarajaStkProvider(
        IDarajaClient client,
        ApplicationDbContext context,
        PaymentCredentialProtector protector,
        IOptions<DarajaOptions> options,
        ILogger<DarajaStkProvider> logger)
    {
        _client = client;
        _context = context;
        _protector = protector;
        _options = options.Value;
        _logger = logger;
    }

    public PaymentProviderKind Kind => PaymentProviderKind.DarajaStk;
    public bool InitiatesPush => true;

    public async Task<ProviderInitiationResult> InitiateAsync(Payment payment, BusinessPaymentConfig config, CancellationToken cancellationToken = default)
    {
        var credentials = await ResolveCredentialsAsync(config);
        if (credentials is null)
        {
            return new ProviderInitiationResult
            {
                Success = false,
                ErrorCode = "NO_CREDENTIALS",
                ErrorMessage = "M-PESA is enabled for this business but Daraja credentials are not configured. Save them in payment settings.",
                Retryable = false
            };
        }

        if (string.IsNullOrWhiteSpace(_options.CallbackBaseUrl))
        {
            return new ProviderInitiationResult
            {
                Success = false,
                ErrorCode = "CALLBACK_URL_NOT_CONFIGURED",
                ErrorMessage = "Daraja:CallbackBaseUrl is not configured.",
                Retryable = false
            };
        }

        var callbackUrl = _options.CallbackBaseUrl.TrimEnd('/') + "/v1/payments/webhooks/daraja/stk";
        if (!(_options.AllowMockClient && _client is FakeDarajaClient))
            ValidateCallbackUrl(callbackUrl);

        var transactionType = config.MpesaAccountType == "till" ? "CustomerBuyGoodsOnline" : "CustomerPayBillOnline";

        var response = await _client.StkPushAsync(new StkPushRequest
        {
            BusinessShortCode = credentials.Value.ShortCode,
            Passkey = credentials.Value.Passkey,
            Amount = payment.Amount,
            PhoneNumber = payment.PhoneNumber ?? string.Empty,
            TransactionType = transactionType,
            AccountReference = payment.Reference,
            TransactionDesc = "Punched",
            CallbackUrl = callbackUrl
        }, cancellationToken);

        if (!response.Success)
        {
            _logger.LogWarning("STK push rejected by Daraja for payment {PaymentId}: {ErrorCode} {ErrorMessage}",
                payment.Id, response.ErrorCode, response.ErrorMessage);
            return new ProviderInitiationResult
            {
                Success = false,
                ErrorCode = response.ErrorCode,
                ErrorMessage = response.ErrorMessage,
                Retryable = response.Retryable
            };
        }

        return new ProviderInitiationResult
        {
            Success = true,
            CheckoutRequestId = response.CheckoutRequestId,
            MerchantRequestId = response.MerchantRequestId,
            CustomerMessage = response.CustomerMessage
        };
    }

    public Task<ProviderStatusResult> GetStatusAsync(string checkoutRequestId, BusinessPaymentConfig config, CancellationToken cancellationToken = default)
        => Task.FromResult(new ProviderStatusResult
        {
            Success = false,
            Status = "unknown",
            ErrorMessage = "Daraja transaction status requires Safaricom production approval (PROPOSED in Punched)."
        });

    public Task<bool> CancelAsync(Payment payment, BusinessPaymentConfig config, CancellationToken cancellationToken = default)
        => Task.FromResult(false); // Daraja has no cancel; expiry is time-driven.

    public Task<ProviderReversalResult> ReverseAsync(Payment payment, string reason, BusinessPaymentConfig config, CancellationToken cancellationToken = default)
        => Task.FromResult(new ProviderReversalResult
        {
            Success = false,
            Supported = false, // REQUIRES SAFARICOM CONFIRMATION: reversal needs initiator + security credential
            ErrorCode = "REVERSAL_NOT_IMPLEMENTED",
            ErrorMessage = "Automated Daraja reversal is not implemented in the MVP; a manual refund record is created instead."
        });

    private async Task<(string ShortCode, string Passkey)?> ResolveCredentialsAsync(BusinessPaymentConfig config)
    {
        var key = _protector.Unprotect(config.ConsumerKeyEncrypted);
        var secret = _protector.Unprotect(config.ConsumerSecretEncrypted);
        var passkey = _protector.Unprotect(config.PasskeyEncrypted);

        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(secret) && !string.IsNullOrEmpty(passkey) && !string.IsNullOrEmpty(config.MpesaShortCode))
            return await Task.FromResult<(string ShortCode, string Passkey)?>((config.MpesaShortCode, passkey));

        // Dev/test fallback: use the fake Daraja client with the business shortcode
        // (or a sandbox placeholder). Never allowed with real production credentials.
        if (_options.AllowMockClient && _client is FakeDarajaClient)
            return await Task.FromResult<(string ShortCode, string Passkey)?>((config.MpesaShortCode ?? "174379", "fake-passkey"));

        return await Task.FromResult<(string ShortCode, string Passkey)?>(null);
    }

    /// <summary>Daraja rejects callback URLs containing mpesa/safaricom or non-HTTPS hosts.</summary>
    private void ValidateCallbackUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443)
            throw new InvalidOperationException("Daraja:CallbackBaseUrl must be an absolute HTTPS URL (port 443).");
        var host = (uri.Host + uri.AbsolutePath).ToLowerInvariant();
        if (host.Contains("mpesa") || host.Contains("m-pesa") || host.Contains("safaricom"))
            throw new InvalidOperationException("Daraja callback URLs must not contain the strings mpesa/m-pesa/safaricom (Safaricom registration filter drops them).");
    }
}

/// <summary>
/// Cash provider: no external system. Exists so the provider registry is uniform;
/// payment confirmation is a staff action on the domain (ConfirmCash), not a
/// provider call.
/// </summary>
public class CashPaymentProvider : IPaymentProvider
{
    private readonly ILogger<CashPaymentProvider> _logger;

    public CashPaymentProvider(ILogger<CashPaymentProvider> logger) => _logger = logger;

    public PaymentProviderKind Kind => PaymentProviderKind.Cash;
    public bool InitiatesPush => false;

    public Task<ProviderInitiationResult> InitiateAsync(Payment payment, BusinessPaymentConfig config, CancellationToken cancellationToken = default)
        => Task.FromResult(new ProviderInitiationResult { Success = false, ErrorCode = "NOT_SUPPORTED", ErrorMessage = "Cash payments require no provider initiation." });

    public Task<ProviderStatusResult> GetStatusAsync(string checkoutRequestId, BusinessPaymentConfig config, CancellationToken cancellationToken = default)
        => Task.FromResult(new ProviderStatusResult { Success = false, Status = "unknown", ErrorMessage = "Cash has no provider status." });

    public Task<bool> CancelAsync(Payment payment, BusinessPaymentConfig config, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<ProviderReversalResult> ReverseAsync(Payment payment, string reason, BusinessPaymentConfig config, CancellationToken cancellationToken = default)
        => Task.FromResult(new ProviderReversalResult { Success = false, Supported = false });
}
