using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Anonymous, rate-limited marketing-unsubscribe endpoints (Phase 4). Both
/// handlers are driven by the encrypted, expiring token minted by
/// <see cref="IUnsubscribeTokenService"/> at email-render time, so no session is
/// required: an email recipient who is not signed in can still stop marketing
/// mail. The GET renders a browser confirmation page; the POST serves the
/// RFC 8058 one-click flow referenced by the <c>List-Unsubscribe-Post</c> header.
/// </summary>
/// <remarks>
/// Unsubscribing only re-applies the system default (marketing/email off); it
/// never affects transactional categories (appointment reminders, security,
/// receipts). The action is idempotent, so a double-click or a re-scanned link
/// is harmless. The endpoint is on the "general" per-IP rate-limit policy.
/// </remarks>
[ApiController]
[Route("v1/notifications/unsubscribe")]
[EnableRateLimiting("general")]
public sealed class UnsubscribeController : ControllerBase
{
    private readonly IUnsubscribeTokenService _tokens;
    private readonly IPreferenceResolver _preferences;
    private readonly ILogger<UnsubscribeController> _logger;

    public UnsubscribeController(
        IUnsubscribeTokenService tokens,
        IPreferenceResolver preferences,
        ILogger<UnsubscribeController> logger)
    {
        _tokens = tokens;
        _preferences = preferences;
        _logger = logger;
    }

    /// <summary>Unsubscribe link a recipient clicks in an email footer.</summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Get([FromQuery] string? token, CancellationToken ct = default)
    {
        var grant = _tokens.ReadGrant(token ?? string.Empty);
        if (grant == null)
        {
            return new ContentResult
            {
                Content = Html(
                    "Link invalid or expired",
                    "This unsubscribe link is invalid or has expired. Sign in and open Settings &rarr; Notifications to manage your preferences."),
                ContentType = "text/html",
                StatusCode = StatusCodes.Status400BadRequest
            };
        }

        await ApplyAsync(grant, ct);
        return Content(
            Html(
                "You are unsubscribed",
                "Marketing emails from this business are now turned off for your address. Appointment reminders, receipts, and security messages are unaffected."),
            "text/html",
            Encoding.UTF8);
    }

    /// <summary>RFC 8058 one-click unsubscribe (invoked by mail providers, not browsers).</summary>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Post([FromQuery] string? token, CancellationToken ct = default)
    {
        var grant = _tokens.ReadGrant(token ?? string.Empty);
        if (grant == null)
            return BadRequest(ApiResponse<string>.Fail("INVALID_TOKEN", "Invalid or expired unsubscribe token."));

        await ApplyAsync(grant, ct);
        return Ok();
    }

    private async Task ApplyAsync(UnsubscribeGrant grant, CancellationToken ct)
    {
        var marketingOff = new List<NotificationPreferenceItem>
        {
            new()
            {
                Category = NotificationCategory.Marketing,
                Channel = NotificationChannel.Email,
                Enabled = false
            }
        };

        try
        {
            await _preferences.UpsertAsync(grant.UserId, grant.BusinessId, marketingOff, ct);
        }
        catch (ArgumentException) when (grant.BusinessId != null)
        {
            // The recipient is no longer associated with that business (stale list
            // or removed enrollment) — the tenant-scoped row is rejected. Fall back
            // to a global marketing-off row so this address still stops receiving
            // marketing mail from every business.
            _logger.LogInformation(
                "Unsubscribe for user {User} targeted business {Business} they are no longer associated with; applying a global marketing opt-out.",
                grant.UserId, grant.BusinessId);
            await _preferences.UpsertAsync(grant.UserId, null, marketingOff, ct);
        }
    }

    // Headline/detail are compile-time literals from this file only — no
    // request data is ever interpolated into the page.
    private static string Html(string headline, string detail) =>
        "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">" +
        $"<title>{headline}</title></head>" +
        "<body style=\"margin:0;padding:32px 16px;font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;background:#f3f4f6;color:#1f2937;\">" +
        "<div style=\"max-width:560px;margin:0 auto;background:#ffffff;border-radius:8px;padding:32px;\">" +
        $"<h1 style=\"font-size:20px;margin:0 0 12px;\">{headline}</h1>" +
        $"<p style=\"line-height:1.6;margin:0 0 8px;\">{detail}</p>" +
        "<p style=\"font-size:14px;color:#6b7280;line-height:1.6;margin:0;\">Transactional messages such as appointment reminders, receipts, and security alerts are unaffected by this change.</p>" +
        "</div></body></html>";
}
