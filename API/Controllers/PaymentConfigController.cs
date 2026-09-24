using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Filters;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Business payment configuration (cash + M-PESA/Daraja). Business-owner only.
/// GET never returns credentials; PUT only accepts new secret values and stores
/// them encrypted. Each business owns exactly one configuration.
/// </summary>
[ApiController]
[Route("v1/payment-config")]
[Produces("application/json")]
[Authorize(Roles = "Business")]
[RequireModule("payments")]
[EnableRateLimiting("general")]
public class PaymentConfigController : ControllerBase
{
    private readonly IPaymentConfigService _config;
    private readonly IBusinessContext _businessContext;

    public PaymentConfigController(IPaymentConfigService config, IBusinessContext businessContext)
    {
        _config = config;
        _businessContext = businessContext;
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
    public async Task<IActionResult> Save([FromBody] SavePaymentConfigRequest request)
    {
        var businessId = await _businessContext.GetBusinessIdAsync();
        if (businessId == null) return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<PaymentConfigResponse>.Fail("FORBIDDEN", "No business scope."));
        var result = await _config.SaveAsync(businessId.Value, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }
}
