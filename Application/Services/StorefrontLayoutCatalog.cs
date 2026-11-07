namespace PunchedApi.Application.Services;

/// <summary>
/// The header / hero / footer layout vocabulary, and the section variant
/// vocabulary, exactly as the storefront design names them.
///
/// <para>This is the server's source of truth for those three layout slots (the
/// section <i>types</i> live in <see cref="StorefrontSectionCatalog"/>). Names
/// that shipped in an earlier revision stay valid through
/// <see cref="Normalise*"/> so a stored configuration never renders as an unknown
/// variant — it resolves to the closest current design instead.</para>
/// </summary>
public static class StorefrontLayoutCatalog
{
    // ── Header ──────────────────────────────────────────────────────────────────
    public static readonly IReadOnlyList<string> HeaderLayouts =
    [
        "classic", "centered", "split", "minimal", "pill", "glass", "utility", "bar",
        // Earlier names, still stored on live configurations.
        "solid", "floating", "sticky-compact", "mobile-first",
    ];

    private static readonly Dictionary<string, string> HeaderAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["solid"] = "classic",
        ["floating"] = "pill",
        ["sticky-compact"] = "classic",
        ["mobile-first"] = "minimal",
    };

    // ── Hero ────────────────────────────────────────────────────────────────────
    public static readonly IReadOnlyList<string> HeroLayouts =
    [
        "overlay", "centered", "split", "split-reverse", "minimal", "gradient",
        "facts", "collage", "booking",
        // Earlier names, still stored on live configurations.
        "video", "carousel", "editorial", "premium", "full-image", "splitr",
    ];

    private static readonly Dictionary<string, string> HeroAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["full-image"] = "overlay",
        ["splitr"] = "split-reverse",
    };

    // ── Footer ──────────────────────────────────────────────────────────────────
    public static readonly IReadOnlyList<string> FooterLayouts =
    [
        "columns", "newsletter", "centered", "minimal", "wordmark", "split",
        // Earlier names, still stored on live configurations.
        "editorial", "dark", "contact",
    ];

    // ── Header drawer ───────────────────────────────────────────────────────────
    public static readonly IReadOnlyList<string> DrawerStyles = ["left", "right", "top", "full"];

    /// <summary>Section entrance animations. `inherit` defers to the theme.</summary>
    public static readonly IReadOnlyList<string> Animations =
        ["inherit", "none", "fade", "up", "left", "zoom", "blur"];

    public static readonly IReadOnlyList<string> HoverEffects = ["none", "lift", "glow", "zoom"];

    public static readonly IReadOnlyList<string> PageTransitions = ["none", "fade", "slide"];

    public static readonly IReadOnlyList<string> Modes = ["light", "dark"];

    public static readonly IReadOnlyList<string> SectionBackgrounds =
        ["default", "surface", "primary", "accent"];

    public static readonly IReadOnlyList<string> SectionPaddings = ["compact", "normal", "generous"];

    public static string NormaliseHeaderLayout(string? value) =>
        Normalise(value, HeaderLayouts, HeaderAliases, "classic");

    public static string NormaliseHeroLayout(string? value) =>
        Normalise(value, HeroLayouts, HeroAliases, "overlay");

    public static string NormaliseFooterLayout(string? value) =>
        Normalise(value, FooterLayouts, EmptyAliases, "columns");

    private static readonly Dictionary<string, string> EmptyAliases = new();

    private static string Normalise(
        string? value,
        IReadOnlyList<string> allowed,
        Dictionary<string, string> aliases,
        string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var candidate = value.Trim();
        if (aliases.TryGetValue(candidate, out var canonical) &&
            allowed.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            return canonical;
        return allowed.Contains(candidate, StringComparer.OrdinalIgnoreCase)
            ? allowed.First(v => v.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            : fallback;
    }

    /// <summary>Clamps a value into a small set of enum-like options.</summary>
    public static string FromList(string? value, IReadOnlyList<string> allowed, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var candidate = value.Trim();
        return allowed.Contains(candidate, StringComparer.OrdinalIgnoreCase)
            ? allowed.First(v => v.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            : fallback;
    }
}
