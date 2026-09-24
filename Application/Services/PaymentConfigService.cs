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
        CredentialsUpdatedAt = config.CredentialsUpdatedAt
    };
}
