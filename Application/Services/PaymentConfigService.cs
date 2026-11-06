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
            if (request.MpesaAccountType is not ("paybill" or "till"))
                return ApiResponse<PaymentConfigResponse>.Fail("VALIDATION_ERROR", "mpesaAccountType must be 'paybill' or 'till'.");
            if (string.IsNullOrWhiteSpace(request.MpesaShortCode))
                return ApiResponse<PaymentConfigResponse>.Fail("VALIDATION_ERROR", "mpesaShortCode is required when M-PESA is enabled.");
            if (!request.MpesaShortCode!.All(char.IsDigit) || request.MpesaShortCode.Length is < 5 or > 12)
                return ApiResponse<PaymentConfigResponse>.Fail("VALIDATION_ERROR", "mpesaShortCode must be a numeric PayBill/Till number.");
        }

        // Manual M-PESA: a half-configured collection account is worse than none, so
        // the same "must be payable or switched off" rule the Daraja path uses applies.
        if (request.ManualMpesaEnabled)
        {
            var paybill = request.MpesaPaybillNumber?.Trim();
            var till = request.MpesaTillNumber?.Trim();
            var hasPaybill = !string.IsNullOrWhiteSpace(paybill);
            var hasTill = !string.IsNullOrWhiteSpace(till);

            if (!hasPaybill && !hasTill)
                return ApiResponse<PaymentConfigResponse>.Fail(
                    "VALIDATION_ERROR",
                    "Add a PayBill or Till number, or turn manual M-PESA off.");

            if (hasPaybill && paybill is not null)
            {
                if (!paybill.All(char.IsDigit) || paybill.Length is < 5 or > 12)
                    return ApiResponse<PaymentConfigResponse>.Fail(
                        "VALIDATION_ERROR", "mpesaPaybillNumber must be a numeric PayBill number.");
                if (string.IsNullOrWhiteSpace(request.MpesaPaybillAccount))
                    return ApiResponse<PaymentConfigResponse>.Fail(
                        "VALIDATION_ERROR", "mpesaPaybillAccount is required when a PayBill number is set.");
            }

            if (hasTill && till is not null && (!till.All(char.IsDigit) || till.Length is < 5 or > 12))
                return ApiResponse<PaymentConfigResponse>.Fail(
                    "VALIDATION_ERROR", "mpesaTillNumber must be a numeric Till number.");
        }

        if (request.BookingFeePercentage is decimal percent && (percent < 0 || percent > 100))
            return ApiResponse<PaymentConfigResponse>.Fail(
                "VALIDATION_ERROR", "bookingFeePercentage must be between 0 and 100.");

        var config = await _context.BusinessPaymentConfigs.FirstOrDefaultAsync(c => c.BusinessId == businessId);
        if (config == null)
        {
            config = new BusinessPaymentConfig { Id = Guid.NewGuid(), BusinessId = businessId, CreatedAt = DateTime.UtcNow };
            await _context.BusinessPaymentConfigs.AddAsync(config);
        }

        config.CashEnabled = request.CashEnabled;
        config.MpesaEnabled = request.MpesaEnabled;
        config.MpesaAccountType = request.MpesaAccountType;
        config.MpesaShortCode = string.IsNullOrWhiteSpace(request.MpesaShortCode) ? null : request.MpesaShortCode.Trim();

        config.ManualMpesaEnabled = request.ManualMpesaEnabled;
        config.MpesaPaybillNumber = Normalise(request.MpesaPaybillNumber, 20);
        config.MpesaPaybillAccount = Normalise(request.MpesaPaybillAccount, 40);
        config.MpesaTillNumber = Normalise(request.MpesaTillNumber, 20);
        config.MpesaPayeeName = Normalise(request.MpesaPayeeName, 100);
        config.MpesaInstructions = Normalise(request.MpesaInstructions, 500);

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
        MpesaPayeeName = config.MpesaPayeeName,
        MpesaInstructions = config.MpesaInstructions,
        BookingFeePercentage = config.BookingFeePercentage,
        HasManualMpesaDetails = config.HasManualMpesaDetails
    };

    /// <summary>Trim a value to a column's length, treating blank as "not set".</summary>
    private static string? Normalise(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
