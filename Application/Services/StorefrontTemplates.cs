using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Services;

/// <summary>
/// The storefront template library. Each template is a distinct design direction
/// — different typography, colour, spacing, header/hero/footer variant and
/// section arrangement — not a recolour of one layout.
///
/// <para><c>Classic</c> reproduces the storefront that shipped before the builder
/// existed (full-bleed image hero, minimal header, list services, quote
/// testimonial, minimal booking/contact). It is the
/// <see cref="StorefrontBuilderDefaults.DefaultTemplateId"/>, so every business —
/// including ones created before the builder — keeps exactly the page it had.</para>
///
/// <para>Mirrored (id + metadata + blueprint) by
/// <c>src/lib/storefront-builder/templates.ts</c> for the owner console gallery.</para>
/// </summary>
public static class StorefrontTemplates
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
        IReadOnlyList<(string Key, string Variant)> Blueprint);

    public static readonly IReadOnlyList<TemplateDefinition> All = new[]
    {
        // 1. Classic — the pre-builder storefront, preserved verbatim and set as the default.
        new TemplateDefinition(
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
            ]),

        // 2. Luxe — luxury & premium.
        new TemplateDefinition(
            "luxe", "Luxe",
            "Sophisticated serif typography, large editorial imagery and refined gold accents.",
            "Beauty & Wellness", ["luxury", "serif", "dark", "elegant"],
            HeaderVariant: "floating", HeroVariant: "premium", FooterVariant: "editorial",
            Theme: new StorefrontThemeDto
            {
                Primary = "#1a1712", Secondary = "#2b2419", Accent = "#cbb27a",
                Background = "#faf7f0", Surface = "#f1ebdd", Text = "#241f17", MutedText = "#8a7f68",
                ButtonBackground = "#1a1712", ButtonText = "#f3ead4", Border = "#e0d5bd",
                HeadingFont = "Newsreader", BodyFont = "Figtree", HeadingWeight = 400,
                Radius = "sharp", CardStyle = "elevated", Shadow = "soft", ButtonStyle = "square",
                SectionSpacing = "generous", HeadingScale = "editorial",
            },
            Blueprint: [
                ("about", "editorial"), ("services", "image-cards"), ("gallery", "editorial"),
                ("testimonials", "carousel"), ("booking", "split"), ("hours", "inline"),
                ("contact", "split"),
            ]),

        // 3. Minimal — modern & clean.
        new TemplateDefinition(
            "minimal", "Minimal",
            "Generous whitespace, restrained type and quiet, asymmetric layouts.",
            "Professional Services", ["minimal", "clean", "light", "airy"],
            HeaderVariant: "split", HeroVariant: "minimal", FooterVariant: "centered",
            Theme: new StorefrontThemeDto
            {
                Primary = "#111111", Secondary = "#525252", Accent = "#a3a3a3",
                Background = "#ffffff", Surface = "#fafafa", Text = "#171717", MutedText = "#737373",
                ButtonBackground = "#171717", ButtonText = "#ffffff", Border = "#ededed",
                HeadingFont = "Figtree", BodyFont = "Figtree", HeadingWeight = 500,
                Radius = "soft", CardStyle = "flat", Shadow = "none", ButtonStyle = "rounded",
                SectionSpacing = "generous", ContentWidth = 1080,
            },
            Blueprint: [
                ("services", "cards"), ("about", "centered"), ("gallery", "grid"),
                ("testimonials", "cards"), ("booking", "minimal"), ("contact", "minimal"),
            ]),

        // 4. Atelier — editorial & creative.
        new TemplateDefinition(
            "atelier", "Atelier",
            "Magazine-inspired typography, asymmetric image grids and story-first sections.",
            "Creative", ["editorial", "magazine", "story", "serif"],
            HeaderVariant: "centered", HeroVariant: "editorial", FooterVariant: "editorial",
            Theme: new StorefrontThemeDto
            {
                Primary = "#1c1917", Secondary = "#44403c", Accent = "#b45309",
                Background = "#fffdf8", Surface = "#f5f0e6", Text = "#1c1917", MutedText = "#78716c",
                ButtonBackground = "#1c1917", ButtonText = "#fffdf8", Border = "#e7e0d3",
                HeadingFont = "Newsreader", BodyFont = "Figtree", HeadingWeight = 500,
                Radius = "sharp", CardStyle = "flat", Shadow = "none", ButtonStyle = "outline",
                SectionSpacing = "generous", HeadingScale = "editorial", LetterSpacing = "tight",
            },
            Blueprint: [
                ("about", "editorial"), ("services", "image-cards"), ("gallery", "editorial"),
                ("richtext", "quote"), ("testimonials", "quote"), ("booking", "split"),
            ]),

        // 5. Bold — modern & vibrant.
        new TemplateDefinition(
            "bold", "Bold",
            "Expressive typography, vibrant colour and dynamic, asymmetric layouts.",
            "Creative", ["bold", "vibrant", "playful"],
            HeaderVariant: "solid", HeroVariant: "editorial", FooterVariant: "dark",
            Theme: new StorefrontThemeDto
            {
                Primary = "#4c1d95", Secondary = "#db2777", Accent = "#f59e0b",
                Background = "#fffbeb", Surface = "#fef3c7", Text = "#1c1917", MutedText = "#57534e",
                ButtonBackground = "#4c1d95", ButtonText = "#ffffff", Border = "#fcd34d",
                HeadingFont = "Figtree", BodyFont = "Figtree", HeadingWeight = 800,
                Radius = "round", CardStyle = "elevated", Shadow = "strong", ButtonStyle = "pill",
                SectionSpacing = "comfortable", HeadingScale = "display",
            },
            Blueprint: [
                ("services", "grid"), ("stats", "cards"), ("gallery", "masonry"),
                ("about", "image-right"), ("promotions", "cards"), ("testimonials", "wall"),
                ("booking", "banner"),
            ]),

        // 6. Professional — corporate & business.
        new TemplateDefinition(
            "professional", "Professional",
            "Structured, business-focused layouts with a confident, trustworthy tone.",
            "Professional Services", ["corporate", "structured", "trust"],
            HeaderVariant: "split", HeroVariant: "split", FooterVariant: "columns",
            Theme: new StorefrontThemeDto
            {
                Primary = "#0b2a4a", Secondary = "#1d4ed8", Accent = "#0ea5e9",
                Background = "#ffffff", Surface = "#f1f5f9", Text = "#0f172a", MutedText = "#475569",
                ButtonBackground = "#0b2a4a", ButtonText = "#ffffff", Border = "#dbe3ec",
                HeadingFont = "Figtree", BodyFont = "Figtree", HeadingWeight = 700,
                Radius = "soft", CardStyle = "soft", Shadow = "soft", ButtonStyle = "rounded",
                SectionSpacing = "comfortable",
            },
            Blueprint: [
                ("services", "featured"), ("about", "image-right"), ("stats", "row"),
                ("team", "cards"), ("testimonials", "cards"), ("faq", "two-column"),
                ("contact", "split"), ("booking", "panel"),
            ]),

        // 7. Wellness — beauty & lifestyle.
        new TemplateDefinition(
            "wellness", "Wellness",
            "Soft, elegant layouts with calm colour — made for salons, spas and studios.",
            "Beauty & Wellness", ["wellness", "soft", "calm"],
            HeaderVariant: "minimal", HeroVariant: "split", FooterVariant: "contact",
            Theme: new StorefrontThemeDto
            {
                Primary = "#7c5c47", Secondary = "#a98b74", Accent = "#c9a27a",
                Background = "#fdf8f4", Surface = "#f6ece4", Text = "#3b2f28", MutedText = "#8a7669",
                ButtonBackground = "#7c5c47", ButtonText = "#fffaf5", Border = "#ecddd0",
                HeadingFont = "Newsreader", BodyFont = "Figtree", HeadingWeight = 400,
                Radius = "round", CardStyle = "soft", Shadow = "soft", ButtonStyle = "pill",
                SectionSpacing = "generous",
            },
            Blueprint: [
                ("services", "image-cards"), ("about", "image-left"), ("gallery", "masonry"),
                ("team", "circles"), ("testimonials", "cards"), ("hours", "cards"),
                ("booking", "panel"), ("contact", "split"),
            ]),

        // 8. Motion — modern & immersive.
        new TemplateDefinition(
            "motion", "Motion",
            "Large visual heroes, subtle motion and a modern, immersive rhythm.",
            "Fitness", ["immersive", "dark", "modern", "bold"],
            HeaderVariant: "sticky-compact", HeroVariant: "video", FooterVariant: "dark",
            Theme: new StorefrontThemeDto
            {
                Primary = "#0f172a", Secondary = "#dc2626", Accent = "#facc15",
                Background = "#0b1120", Surface = "#111a2e", Text = "#f8fafc", MutedText = "#94a3b8",
                ButtonBackground = "#dc2626", ButtonText = "#ffffff", Border = "#1e293b",
                HeadingFont = "Figtree", BodyFont = "Figtree", HeadingWeight = 800,
                Radius = "soft", CardStyle = "elevated", Shadow = "strong", ButtonStyle = "rounded",
                SectionSpacing = "comfortable", HeadingScale = "display",
                BackgroundStyle = "gradient", GradientFrom = "#0b1120", GradientTo = "#1e293b",
            },
            Blueprint: [
                ("stats", "banner"), ("services", "grid"), ("about", "image-right"),
                ("team", "showcase"), ("gallery", "full-width"), ("testimonials", "wall"),
                ("promotions", "cards"), ("booking", "banner"),
            ]),
    };

    /// <summary>
    /// Template ids that shipped in an earlier revision, mapped onto the current
    /// canonical ids. A business that saved one of these keeps its exact look (the
    /// stored theme/sections are untouched) while resolving to a live template, so
    /// nothing about an already-published storefront changes.
    /// </summary>
    private static readonly Dictionary<string, string> LegacyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["modern-minimal"] = "classic",
        ["modern-commerce"] = "professional",
        ["luxury-premium"] = "luxe",
        ["bold-creative"] = "bold",
        ["professional-corporate"] = "professional",
        ["editorial"] = "atelier",
        ["wellness-beauty"] = "wellness",
        ["fitness-lifestyle"] = "motion",
    };

    public static TemplateDefinition? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var direct = All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (direct != null) return direct;
        return LegacyAliases.TryGetValue(id, out var canonical)
            ? All.FirstOrDefault(t => t.Id == canonical)
            : null;
    }

    /// <summary>The canonical id a (possibly legacy) template id resolves to.</summary>
    public static string? CanonicalId(string? id) => Find(id)?.Id;

    public static StorefrontThemeDto ThemeFor(string? id) =>
        Find(id)?.Theme ?? new StorefrontThemeDto();
}



