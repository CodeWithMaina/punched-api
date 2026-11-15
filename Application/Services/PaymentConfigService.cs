using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Business payment configuration. Credentials are encrypted at rest and never
/// returned in responses (only a boolean hasCredentials flag). Each business owns
/// exactly one configuration row (unique index) — Business A can never use
/// Business B's payment account.
/// </summary>
public class PaymentConfigService : IPaymentConfigService
{
    private readonly ApplicationDbContext _context;
    private readonly IUnitOfWork _uow;
    private readonly PaymentCredentialProtector _protector;

    public PaymentConfigService(ApplicationDbContext context, IUnitOfWork uow, PaymentCredentialProtector protector)
    {
        _context = context;
        _uow = uow;
        _protector = protector;
    }

    public async Task<ApiResponse<PaymentConfigResponse>> GetAsync(Guid businessId)
    {
        var config = await _context.BusinessPaymentConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.BusinessId == businessId);

        if (config == null)
            return ApiResponse<PaymentConfigResponse>.Fail("NOT_FOUND", "Payment configuration not found for this business.");

        return ApiResponse<PaymentConfigResponse>.Ok(ToResponse(config));
    }

    public async Task<ApiResponse<PaymentConfigResponse>> SaveAsync(Guid businessId, SavePaymentConfigRequest request)
    {
        if (request.MpesaEnabled)
        {
            // The Daraja STK path can only push to a PayBill or Till — never a phone.
            if (ManualMpesaMethods.Normalise(request.MpesaAccountType) == ManualMpesaMethods.SendMoney)
                return ApiResponse<PaymentConfigResponse>.Fail(
                    "VALIDATION_ERROR", "M-PESA prompts need a PayBill or Till shortcode, not a phone number.");
            if (string.IsNullOrWhiteSpace(request.MpesaShortCode))
                return ApiResponse<PaymentConfigResponse>.Fail("VALIDATION_ERROR", "mpesaShortCode is required when M-PESA is enabled.");
            if (!request.MpesaShortCode!.All(char.IsDigit) || request.MpesaShortCode.Length is < 5 or > 12)
                return ApiResponse<PaymentConfigResponse>.Fail("VALIDATION_ERROR", "mpesaShortCode must be a numeric PayBill/Till number.");
        }

        // Manual M-PESA: ONE shared definition of a valid configuration, so the form and
        // the server cannot disagree. A failure carries the offending field(s) so the
        // owner UI can place each message under its input. Enabling with an incomplete
        // method is refused rather than silently leaving the business unpayable.
        var fieldErrors = MpesaDetailsValidation.Validate(request);
        if (fieldErrors.Count > 0)
            return ApiResponse<PaymentConfigResponse>.Fail(
                "VALIDATION_ERROR",
                fieldErrors.Values.First(),
                fieldErrors);

        if (request.BookingFeePercentage is decimal percent && (percent < 0 || percent > 100))
            return ApiResponse<PaymentConfigResponse>.Fail(
                "VALIDATION_ERROR", "bookingFeePercentage must be between 0 and 100.");

        // ── Storefront payment options ──────────────────────────
        // Omitted means "keep what is stored" (a client that does not know about these
        // switches must not be able to turn a business's storefront policy off by staying
        // silent); a business with no row yet falls back to the defaults — Payment
        // Included on, Pay After Service off. The pair is validated as a whole BEFORE
        // anything is written, so a save can never leave the business unbookable.
        var existing = await _context.BusinessPaymentConfigs.FirstOrDefaultAsync(c => c.BusinessId == businessId);
        var payUpfront = request.PayUpfrontEnabled ?? existing?.PayUpfrontEnabled ?? true;
        var payAfterService = request.PayAfterServiceEnabled ?? existing?.PayAfterServiceEnabled ?? false;
        if (!payUpfront && !payAfterService)
            return ApiResponse<PaymentConfigResponse>.Fail(
                "VALIDATION_ERROR",
                "Keep at least one payment option on: customers can pay during booking, after the service, or both.",
                new Dictionary<string, string>
                {
                    ["payUpfrontEnabled"] = "Turn on Payment Included or Pay After Service.",
                    ["payAfterServiceEnabled"] = "Turn on Pay After Service or Payment Included."
                });

        var config = existing;
        if (config == null)
        {
            config = new BusinessPaymentConfig { Id = Guid.NewGuid(), BusinessId = businessId, CreatedAt = DateTime.UtcNow };
            await _context.BusinessPaymentConfigs.AddAsync(config);
        }

        config.PayUpfrontEnabled = payUpfront;
        config.PayAfterServiceEnabled = payAfterService;

        config.CashEnabled = request.CashEnabled;
        config.MpesaEnabled = request.MpesaEnabled;
        // Canonicalise so an old client that never sent a type (or sent an alias) still
        // lands on a known value the DB constraint accepts.
        config.MpesaAccountType = ManualMpesaMethods.Normalise(request.MpesaAccountType);
        config.MpesaShortCode = string.IsNullOrWhiteSpace(request.MpesaShortCode) ? null : request.MpesaShortCode.Trim();

        config.ManualMpesaEnabled = request.ManualMpesaEnabled;
        config.MpesaPaybillNumber = Normalise(request.MpesaPaybillNumber, 20);
        config.MpesaPaybillAccount = Normalise(request.MpesaPaybillAccount, 40);
        config.MpesaTillNumber = Normalise(request.MpesaTillNumber, 20);
        // Stored normalised (254…) so the public payload and the DB CHECK constraint agree.
        config.MpesaPhoneNumber = MpesaDetailsValidation.NormalisePhone(request.MpesaPhoneNumber);
        config.MpesaPayeeName = Normalise(request.MpesaPayeeName, 60);
        config.MpesaInstructions = Normalise(request.MpesaInstructions, 300);

        // Omitted means "leave it alone": a client that does not know about this
        // setting must never be able to reset a business's pricing to a default.
        if (request.BookingFeePercentage is decimal fee)
            config.BookingFeePercentage = decimal.Round(fee, 2, MidpointRounding.AwayFromZero);

        // Sensitive credentials: only overwrite when the client sends a new value;
        // omitted fields keep the existing stored value. Never echo them back.
        var updatedCredentials = false;
        if (!string.IsNullOrWhiteSpace(request.ConsumerKey))
        {
            config.ConsumerKeyEncrypted = _protector.Protect(request.ConsumerKey!.Trim());
            updatedCredentials = true;
        }
        if (!string.IsNullOrWhiteSpace(request.ConsumerSecret))
        {
            config.ConsumerSecretEncrypted = _protector.Protect(request.ConsumerSecret!.Trim());
            updatedCredentials = true;
        }
        if (!string.IsNullOrWhiteSpace(request.Passkey))
        {
            config.PasskeyEncrypted = _protector.Protect(request.Passkey!.Trim());
            updatedCredentials = true;
        }

        if (updatedCredentials)
            config.CredentialsUpdatedAt = DateTime.UtcNow;

        await _uow.SaveChangesAsync();
        return ApiResponse<PaymentConfigResponse>.Ok(ToResponse(config));
    }

    private static PaymentConfigResponse ToResponse(BusinessPaymentConfig config) => new()
    {
        BusinessId = config.BusinessId,
        CashEnabled = config.CashEnabled,
        MpesaEnabled = config.MpesaEnabled,
        MpesaAccountType = config.MpesaAccountType,
        MpesaShortCode = config.MpesaShortCode,
        HasCredentials = config.HasCredentials,
        CredentialsUpdatedAt = config.CredentialsUpdatedAt,
        ManualMpesaEnabled = config.ManualMpesaEnabled,
        MpesaPaybillNumber = config.MpesaPaybillNumber,
        MpesaPaybillAccount = config.MpesaPaybillAccount,
        MpesaTillNumber = config.MpesaTillNumber,
        MpesaPhoneNumber = config.MpesaPhoneNumber,
        MpesaPayeeName = config.MpesaPayeeName,
        MpesaInstructions = config.MpesaInstructions,
        BookingFeePercentage = config.BookingFeePercentage,
        HasManualMpesaDetails = config.HasManualMpesaDetails,
        PayUpfrontEnabled = config.PayUpfrontEnabled,
        PayAfterServiceEnabled = config.PayAfterServiceEnabled
    };

    /// <summary>Trim a value to a column's length, treating blank as "not set".</summary>
    private static string? Normalise(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
