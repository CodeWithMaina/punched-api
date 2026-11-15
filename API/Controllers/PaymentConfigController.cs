using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Filters;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Business payment configuration (cash + M-PESA/Daraja). Business-owner only.
/// GET never returns credentials; PUT only accepts new secret values and stores
/// them encrypted. Each business owns exactly one configuration.
/// </summary>
[ApiController]
[Route("v1/payment-config")]
[Produces("application/json")]
[Authorize(Policy = MembershipPolicies.Owner)]
[RequireModule("payments")]
[EnableRateLimiting("general")]
public class PaymentConfigController : ControllerBase
{
    private readonly IPaymentConfigService _config;
    private readonly IBusinessContext _businessContext;
    private readonly ILogger<PaymentConfigController> _logger;

    public PaymentConfigController(
        IPaymentConfigService config,
        IBusinessContext businessContext,
        ILogger<PaymentConfigController> logger)
    {
        _config = config;
        _businessContext = businessContext;
        _logger = logger;
    }

    /// <summary>Get this business's payment configuration (secrets redacted — only a hasCredentials flag).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PaymentConfigResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get()
    {
        var businessId = await _businessContext.GetBusinessIdAsync();
        if (businessId == null) return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<PaymentConfigResponse>.Fail("FORBIDDEN", "No business scope."));
        var result = await _config.GetAsync(businessId.Value);
        return result.Success ? Ok(result) : NotFound(result);
    }

    /// <summary>Save payment configuration. Omitted secret fields keep existing stored values.</summary>
    [HttpPut]
    [ProducesResponseType(typeof(ApiResponse<PaymentConfigResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<PaymentConfigResponse>), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Save([FromBody] SavePaymentConfigRequest request)
    {
        var businessId = await _businessContext.GetBusinessIdAsync();
        if (businessId == null) return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<PaymentConfigResponse>.Fail("FORBIDDEN", "No business scope."));

        var result = await _config.SaveAsync(businessId.Value, request);

        if (result.Success)
        {
            // Changing where money is sent is sensitive, so record who changed what and
            // when. Numbers are masked: the value is not needed to audit the change, and
            // a full PayBill/Till/phone is not worth leaking into logs.
            _logger.LogInformation(
                "Payment configuration changed. BusinessId={BusinessId} Actor={ActorId} Method={Method} Enabled={Enabled} Paybill={Paybill} Till={Till} Phone={Phone}",
                businessId.Value,
                CurrentUserId(),
                ManualMpesaMethods.Normalise(request.MpesaAccountType),
                request.ManualMpesaEnabled,
                Mask(request.MpesaPaybillNumber),
                Mask(request.MpesaTillNumber),
                Mask(request.MpesaPhoneNumber));
            return Ok(result);
        }

        // An invalid configuration is the client's to fix, so it is a 422 rather than a
        // generic 400; the per-field messages ride on `error.fields`.
        if (result.Error?.Code == "VALIDATION_ERROR")
            return StatusCode(StatusCodes.Status422UnprocessableEntity, result);

        return BadRequest(result);
    }

    private Guid? CurrentUserId() =>
        Guid.TryParse(User.FindFirst("userId")?.Value, out var id) ? id : null;

    /// <summary>Keeps the last two characters so an audit trail can still tell entries apart.</summary>
    private static string? Mask(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= 2 ? new string('*', trimmed.Length) : new string('*', trimmed.Length - 2) + trimmed[^2..];
    }
}

