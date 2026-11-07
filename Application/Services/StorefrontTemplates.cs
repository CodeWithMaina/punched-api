using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Services;

/// <summary>
/// The storefront template library.
///
/// <para><b>Classic</b> reproduces the storefront that shipped before the builder
/// existed and is the default, so a business that never chose a template keeps
/// exactly the page it had. The seven design templates that follow it are the
/// storefront kit's own catalogue — same ids, names, categories, palettes,
/// typography, corner radius and hero copy, and each seeds the whole starting
/// site (its pages, each page's hero copy and its section list).</para>
///
/// <para>Mirrored by <c>src/lib/storefront-builder/templates.ts</c> for the owner
/// console gallery.</para>
/// </summary>
public static partial class StorefrontTemplates
{
    public sealed record TemplateDefinition(
        string Id,
        string Name,
        string Description,
        string Category,
        IReadOnlyList<string> Tags,
        string HeaderVariant,
        string HeroVariant,
        string FooterVariant,
        StorefrontThemeDto Theme,
        IReadOnlyList<(string Key, string Variant)> Blueprint,
        /// <summary>
        /// The whole site this template defines: one entry per page, each with its
        /// own hero copy and section list. Empty for templates that describe only a
        /// home page.
        /// </summary>
        IReadOnlyList<TemplatePage>? Pages = null);

    /// <summary>One page of a template's starting site.</summary>
    public sealed record TemplatePage(
        string Id,
        string Title,
        string Slug,
        string Kind,
        string HeroHeading,
        string HeroDescription,
        IReadOnlyList<(string Key, string Variant)> Blueprint);

    public static readonly IReadOnlyList<TemplateDefinition> All = Build();

    /// <summary>
    /// Composed on demand rather than from sibling fields: a field initialiser
    /// would depend on the textual order of the partial files, which is exactly the
    /// kind of bug that only shows up in production.
    /// </summary>
    private static List<TemplateDefinition> Build() =>
        new[] { Classic() }.Concat(DesignCatalog()).ToList();

    /// <summary>
    /// The pre-builder storefront, preserved verbatim: clean typography, a
    /// full-bleed hero and a calm, familiar rhythm. Its stored look never changes,
    /// so businesses that predate the builder keep the page they already had.
    /// </summary>
    private static TemplateDefinition Classic() => new(
        "classic", "Classic",
        "The original Punched storefront — clean typography, a full-bleed hero and a calm, familiar rhythm.",
        "All businesses", ["classic", "default", "timeless"],
        HeaderVariant: "minimal", HeroVariant: "centered", FooterVariant: "minimal",
        Theme: new StorefrontThemeDto
        {
            Primary = "#111827", Secondary = "#374151", Accent = "#111827",
            Background = "#ffffff", Surface = "#f7f7f5", Text = "#141414", MutedText = "#6b7280",
            ButtonBackground = "#141414", ButtonText = "#ffffff", Border = "#e7e5e1",
            HeadingFont = "Figtree", BodyFont = "Figtree", HeadingWeight = 600,
            Radius = "sharp", CardStyle = "flat", Shadow = "none", ButtonStyle = "square",
            SectionSpacing = "generous",
        },
        Blueprint: [
            ("services", "list"), ("about", "centered"), ("gallery", "grid"),
            ("testimonials", "quote"), ("booking", "minimal"), ("contact", "minimal"),
        ]);

    /// <summary>
    /// Template ids that shipped in an earlier revision, mapped onto the current
    /// canonical ids. A business stored against one of these keeps its exact look
    /// (its stored theme and sections are untouched) while resolving to a live
    /// template, so nothing about an already-published storefront changes.
    /// </summary>
    private static readonly Dictionary<string, string> LegacyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["modern-minimal"] = "classic",
        ["modern-commerce"] = "ledger",
        ["luxury-premium"] = "luxe",
        ["bold-creative"] = "ink",
        ["professional-corporate"] = "ledger",
        ["editorial"] = "atelier",
        ["wellness-beauty"] = "bloom",
        ["fitness-lifestyle"] = "fade",
        // The production template family the design's catalogue replaced.
        ["minimal"] = "atelier",
        ["professional"] = "ledger",
        ["bold"] = "ink",
        ["wellness"] = "bloom",
        ["motion"] = "fade",
    };

    public static TemplateDefinition? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var direct = All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (direct != null) return direct;
        return LegacyAliases.TryGetValue(id, out var canonical)
            ? All.FirstOrDefault(t => t.Id.Equals(canonical, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>The canonical id a (possibly legacy) template id resolves to.</summary>
    public static string? CanonicalId(string? id) => Find(id)?.Id;

    public static StorefrontThemeDto ThemeFor(string? id) =>
        Find(id)?.Theme ?? new StorefrontThemeDto();
}
