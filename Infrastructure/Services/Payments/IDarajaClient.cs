using System.Text.Json;

namespace PunchedApi.Infrastructure.Services.Payments;

/// <summary>
/// Thin seam over the Safaricom Daraja HTTP APIs used by this module. The real
/// implementation (DarajaClient) speaks HTTPS/OAuth; the fake (FakeDarajaClient)
/// is used in dev/test when no credentials are configured. NEVER fake production
/// success: the fake only runs when explicitly allowed outside production.
///
/// Verified against Safaricom Daraja (2026): OAuth token cache; STK Push
/// /mpesa/stkpush/v1/processrequest with TransactionType CustomerPayBillOnline /
/// CustomerBuyGoodsOnline; callbacks at CallBackURL; C2B v2 registerurl.
/// </summary>
public interface IDarajaClient
{
    /// <summary>STK Push (M-PESA Express). Returns CheckoutRequestID for callback correlation.</summary>
    Task<StkPushResponse> StkPushAsync(StkPushRequest request, CancellationToken cancellationToken = default);

    /// <summary>Transaction status query. REQUIRES Safaricom-approved production access; sandbox support varies.</summary>
    Task<StkPushResponse> QueryStatusAsync(string checkoutRequestId, string shortCode, CancellationToken cancellationToken = default);

    /// <summary>C2B v2 register URL (confirmation/validation endpoints for PayBill/Till).</summary>
    Task<bool> RegisterC2BUrlsAsync(string shortCode, string responseType, string confirmationUrl, string validationUrl, CancellationToken cancellationToken = default);

    /// <summary>Obtain (cached) OAuth access token.</summary>
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

public class StkPushRequest
{
    public string BusinessShortCode { get; set; } = string.Empty;
    public string Passkey { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    /// <summary>CustomerPayBillOnline (paybill) or CustomerBuyGoodsOnline (till).</summary>
    public string TransactionType { get; set; } = "CustomerPayBillOnline";
    /// <summary>AccountReference (max 12 chars) — Punched payment reference.</summary>
    public string AccountReference { get; set; } = string.Empty;
    /// <summary>TransactionDesc (max 13 chars).</summary>
    public string TransactionDesc { get; set; } = string.Empty;
    public string CallbackUrl { get; set; } = string.Empty;
}

public class StkPushResponse
{
    public bool Success { get; set; }
    public string? MerchantRequestId { get; set; }
    public string? CheckoutRequestId { get; set; }
    public string? ResponseCode { get; set; }
    public string? ResponseDescription { get; set; }
    public string? CustomerMessage { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public bool Retryable { get; set; }

    public static StkPushResponse FromJson(JsonElement json) => new()
    {
        Success = json.TryGetProperty("ResponseCode", out var rc) && rc.GetString() == "0",
        MerchantRequestId = json.TryGetProperty("MerchantRequestID", out var mr) ? mr.GetString() : null,
        CheckoutRequestId = json.TryGetProperty("CheckoutRequestID", out var cr) ? cr.GetString() : null,
        ResponseCode = json.TryGetProperty("ResponseCode", out var rc2) ? rc2.GetString() : null,
        ResponseDescription = json.TryGetProperty("ResponseDescription", out var rd) ? rd.GetString() : null,
        CustomerMessage = json.TryGetProperty("CustomerMessage", out var cm) ? cm.GetString() : null,
        ErrorCode = json.TryGetProperty("errorCode", out var ec) ? ec.GetString() : null,
        ErrorMessage = json.TryGetProperty("errorMessage", out var em) ? em.GetString() : json.TryGetProperty("ResponseDescription", out var rd2) ? rd2.GetString() : null
    };
}
