namespace PunchedApi.Application.Settings;

/// <summary>
/// Public-facing application settings used to build absolute callback/acceptance URLs
/// (e.g. staff invitation links). Configure via the "PublicApp" section / env vars.
/// </summary>
public class PublicAppSettings
{
    public const string SectionName = "PublicApp";

    /// <summary>
    /// Base URL of the web frontend (e.g. https://punched.app or http://localhost:3000).
    /// Used to build invitation acceptance links.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:3000";

    /// <summary>
    /// Registrable root domain that hosts business subdomains
    /// (e.g. <c>punched.app</c>; local dev falls back to the <see cref="BaseUrl"/>
    /// host, typically <c>localhost</c>). Empty → derive from <see cref="BaseUrl"/>.
    /// Consumed by <c>TenantUrlBuilder</c> / <c>TenantHostResolver</c> to build and
    /// parse <c>{slug}.punched.app</c> addresses without hardcoding the domain.
    /// </summary>
    public string RootDomain { get; set; } = string.Empty;

    /// <summary>
    /// How many days a newly created staff invitation remains valid.
    /// </summary>
    public int InvitationExpiryDays { get; set; } = 7;
}