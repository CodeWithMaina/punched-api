using System;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.Services;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Daraja webhook endpoints for STK Push callbacks and C2B confirmations/validations.
/// These endpoints are NOT authenticated with JWT — Daraja calls them directly.
/// They are protected by the rate limiter and process callbacks idempotently.
///
/// Route: /v1/payments/webhooks/daraja
/// </summary>
[ApiController]
[Route("v1/payments/webhooks/daraja")]
[Produces("application/json")]
[AllowAnonymous] // Daraja calls these endpoints directly — no Punched JWT.
[EnableRateLimiting("general")]
public class DarajaWebhookController : ControllerBase
{
    private readonly IPaymentCallbackService _callbacks;
    private readonly ILogger<DarajaWebhookController> _logger;

    public DarajaWebhookController(IPaymentCallbackService callbacks, ILogger<DarajaWebhookController> logger)
    {
        _callbacks = callbacks;
        _logger = logger;
    }

    /// <summary>
    /// STK Push callback. Daraja POSTs the CheckoutRequestID, ResultCode, and
    /// CallbackMetadata (including MpesaReceiptNumber) here.
    /// </summary>
    [HttpPost("stk")]
    public async Task<IActionResult> StkCallback([FromBody] JsonElement payload)
    {
        var rawPayload = payload.GetRawText();
        var (outcome, paymentId) = await _callbacks.ProcessStkCallbackAsync(rawPayload);
        _logger.LogInformation("STK callback processed: outcome={Outcome}, paymentId={PaymentId}", outcome, paymentId);
        // Daraja expects an HTTP 200 with any body. Return 200 regardless of
        // our internal outcome — the callback itself was received and persisted.
        return Ok(new { message = "Callback received", outcome, paymentId });
    }

    /// <summary>
    /// C2B confirmation callback. Daraja POSTs transaction details here after
    /// a customer pays via PayBill or Till.
    /// </summary>
    [HttpPost("c2b/confirmation")]
    public async Task<IActionResult> C2BConfirmation([FromBody] JsonElement payload)
    {
        var rawPayload = payload.GetRawText();
        var (outcome, paymentId) = await _callbacks.ProcessC2BConfirmationAsync(rawPayload);
        _logger.LogInformation("C2B confirmation processed: outcome={Outcome}, paymentId={PaymentId}", outcome, paymentId);
        return Ok(new { message = "Confirmation received", outcome, paymentId });
    }

    /// <summary>
    /// C2B validation callback (only if Safaricom enables validation for the
    /// shortcode). We accept known references and accept-with-warning for
    /// unknown ones (to avoid losing customer money).
    /// </summary>
    [HttpPost("c2b/validation")]
    public async Task<IActionResult> C2BValidation([FromBody] JsonElement payload)
    {
        var rawPayload = payload.GetRawText();
        var (accepted, description) = await _callbacks.ProcessC2BValidationAsync(rawPayload);
        return Ok(new { status = accepted ? "SUCCESS" : "FAILED", data = description });
    }
}