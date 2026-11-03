using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Settings;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Platform support surface.
/// Base route: /v1/support
/// </summary>
/// <remarks>
/// Authenticated on purpose: the Settings module is only reachable inside a
/// signed-in console, and keeping this endpoint out of the public allow-list
/// means the support channels are never scraped from an anonymous surface.
/// The payload is configuration, not tenant data — there is no business scoping
/// to enforce and no support-ticket API to call.
/// </remarks>
[ApiController]
[Route("v1/support")]
[Produces("application/json")]
[Authorize]
[EnableRateLimiting("general")]
public sealed class SupportController : ControllerBase
{
    private readonly SupportSettings _settings;

    public SupportController(IOptions<SupportSettings> settings)
    {
        _settings = settings.Value;
    }

    /// <summary>
    /// The configured platform support channels and hours.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<SupportContactResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult GetContact()
    {
        var response = new SupportContactResponse
        {
            Email = _settings.Email?.Trim() ?? string.Empty,
            Phone = _settings.Phone?.Trim() ?? string.Empty,
            WhatsApp = _settings.WhatsApp?.Trim() ?? string.Empty,
            HelpCenterUrl = _settings.HelpCenterUrl?.Trim() ?? string.Empty,
            ResponseNote = _settings.ResponseNote?.Trim() ?? string.Empty,
            HoursTimeZone = _settings.HoursTimeZone?.Trim() ?? string.Empty,
            Hours = (_settings.Hours ?? new List<SupportHourRange>())
                .Where(row => !string.IsNullOrWhiteSpace(row.Days) || !string.IsNullOrWhiteSpace(row.Hours))
                .Select(row => new SupportHourResponse
                {
                    Days = row.Days?.Trim() ?? string.Empty,
                    Hours = row.Hours?.Trim() ?? string.Empty
                })
                .ToList()
        };

        return Ok(ApiResponse<SupportContactResponse>.Ok(response));
    }
}
