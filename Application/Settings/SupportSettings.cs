namespace PunchedApi.Application.Settings;

/// <summary>
/// Platform support contact details surfaced to the signed-in consoles
/// (Settings → Support). Configuration rather than a table: the values are
/// owned by operations, change rarely, and are identical for every tenant, so
/// they belong in <c>appsettings</c> next to the other platform-level settings.
/// </summary>
/// <remarks>
/// These are the *platform* support channels (the Zenbokk team), deliberately
/// distinct from a business's own contact details, which live on the
/// <c>Business</c> record. A link is only rendered when its value is configured,
/// so an unset channel disappears from the UI instead of becoming a dead
/// control.
/// </remarks>
public class SupportSettings
{
    public const string SectionName = "Support";

    /// <summary>Support mailbox. Rendered as a <c>mailto:</c> link.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Voice line. Rendered as a <c>tel:</c> link.</summary>
    public string Phone { get; set; } = string.Empty;

    /// <summary>WhatsApp number. Rendered as a <c>wa.me</c> link.</summary>
    public string WhatsApp { get; set; } = string.Empty;

    /// <summary>Documentation / help-centre home. Rendered as an external link.</summary>
    public string HelpCenterUrl { get; set; } = string.Empty;

    /// <summary>One-line expectation shown under the heading (e.g. response time).</summary>
    public string ResponseNote { get; set; } = string.Empty;

    /// <summary>Time zone the <see cref="Hours"/> rows are expressed in.</summary>
    public string HoursTimeZone { get; set; } = string.Empty;

    /// <summary>Support-hours rows, in display order.</summary>
    public List<SupportHourRange> Hours { get; set; } = new();
}

/// <summary>One row of the support-hours table ("Monday to Friday", "8:00 AM – 6:00 PM").</summary>
public class SupportHourRange
{
    public string Days { get; set; } = string.Empty;

    public string Hours { get; set; } = string.Empty;
}
