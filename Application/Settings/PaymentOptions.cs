namespace PunchedApi.Application.Settings;

/// <summary>
/// Payment module settings (appsettings: "Payments"). Never contains per-business
/// credentials — those are stored per business, encrypted, in BusinessPaymentConfig.
/// </summary>
public class PaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary>Base64 32-byte key used for AES-GCM encryption of business Daraja credentials at rest.</summary>
    public string? CredentialEncryptionKey { get; set; }

    /// <summary>Minutes an STK payment stays AwaitingCustomer before being expired (Safaricom prompts time out on their own; this is Punched's own window).</summary>
    public int StkExpiryMinutes { get; set; } = 5;

    /// <summary>Maximum payment attempts per payment (manual retry increments).</summary>
    public int MaxAttempts { get; set; } = 5;
}

/// <summary>Platform-level Daraja settings. Used (a) as fallback when a business has no own credentials and (b) to build callback URLs.</summary>
public class DarajaOptions
{
    public const string SectionName = "Daraja";

    /// <summary>true = sandbox (sandbox.safaricom.co.ke), false = production (api.safaricom.co.ke).</summary>
    public bool Sandbox { get; set; } = true;

    /// <summary>Platform consumer key (e.g. for C2B register-url or dev fallback). Optional.</summary>
    public string? ConsumerKey { get; set; }

    public string? ConsumerSecret { get; set; }

    /// <summary>Public base URL for callbacks, e.g. https://api.punched.co.ke (HTTPS 443, must not contain "mpesa"/"safaricom").</summary>
    public string CallbackBaseUrl { get; set; } = string.Empty;

    /// <summary>When true and a business has no credentials, the FakeDarajaClient is used (dev/test only — never fakes production success).</summary>
    public bool AllowMockClient { get; set; } = true;
}
