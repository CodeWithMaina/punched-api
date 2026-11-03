using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  SUPPORT DTOs
//  The platform's support contact details, resolved from the
//  `Support` configuration section. Read-only; there is no
//  support-ticket API, so the client only ever renders links.
// ═══════════════════════════════════════════════════════════════

/// <summary>Response of GET /v1/support — the platform support channels.</summary>
public class SupportContactResponse
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = string.Empty;

    [JsonPropertyName("whatsApp")]
    public string WhatsApp { get; set; } = string.Empty;

    [JsonPropertyName("helpCenterUrl")]
    public string HelpCenterUrl { get; set; } = string.Empty;

    /// <summary>Expectation line shown under the section heading.</summary>
    [JsonPropertyName("responseNote")]
    public string ResponseNote { get; set; } = string.Empty;

    /// <summary>Time zone the <see cref="Hours"/> rows are expressed in.</summary>
    [JsonPropertyName("hoursTimeZone")]
    public string HoursTimeZone { get; set; } = string.Empty;

    [JsonPropertyName("hours")]
    public List<SupportHourResponse> Hours { get; set; } = new();
}

/// <summary>One support-hours row.</summary>
public class SupportHourResponse
{
    [JsonPropertyName("days")]
    public string Days { get; set; } = string.Empty;

    [JsonPropertyName("hours")]
    public string Hours { get; set; } = string.Empty;
}
