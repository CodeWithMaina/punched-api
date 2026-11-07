using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Services;

/// <summary>
/// The storefront kit's own template catalogue.
///
/// <para>Every template ships the same starting <b>site</b> — a home page plus
/// Services, About, Gallery and Contact, each with its own hero copy and section
/// list — because that is what the design does: the identity comes from the
/// palette, the typography, the corner radius and the headline, not from a
/// different page structure. They are kept apart from <c>classic</c> (the
/// pre-builder storefront) so that default never changes.</para>
/// </summary>
public static partial class StorefrontTemplates
{
    private static IReadOnlyList<TemplateDefinition> DesignCatalog() => new[]
    {
        Design(
            id: "atelier", name: "Atelier", category: "Beauty and Salon",
            description: "Warm paper tones, an editorial serif and square corners — a small studio with a steady hand.",
            primary: "#1c1917", accent: "#b45309", background: "#fffdf8", surface: "#f5f0e6",
            headingFont: "Newsreader", bodyFont: "Figtree", radiusPx: 0,
            headline: "The art of a good haircut.",
            blurb: "Colour, cut and care from a small team in Nyeri. Appointments, not queues."),

        Design(
            id: "luxe", name: "Luxe", category: "Beauty and Salon",
            description: "Deep lacquer, antique gold and a high-contrast serif for a private studio.",
            primary: "#1a1712", accent: "#cbb27a", background: "#faf7f0", surface: "#f1ebdd",
            headingFont: "Playfair Display", bodyFont: "Figtree", radiusPx: 2,
            headline: "Quiet luxury, beautifully finished.",
            blurb: "Signature colour and styling in a private studio."),

        Design(
            id: "fade", name: "Fade & Co", category: "Barbershop",
            description: "Ink navy with a brass accent and a geometric sans — sharp, direct, no fuss.",
            primary: "#14213d", accent: "#e0a526", background: "#ffffff", surface: "#f1f3f7",
            headingFont: "DM Sans", bodyFont: "DM Sans", radiusPx: 6,
            headline: "Sharp cuts. Straight talk.",
            blurb: "Fades, beard trims and hot towel shaves since 2016."),

        Design(
            id: "bloom", name: "Bloom", category: "Fitness and Wellness",
            description: "Sage and clay, an editorial serif and generous rounding for a slower pace.",
            primary: "#2f5d50", accent: "#d98b6a", background: "#f8faf7", surface: "#e9efe9",
            headingFont: "Newsreader", bodyFont: "Figtree", radiusPx: 14,
            headline: "Slow down. Feel like yourself.",
            blurb: "Massage, facials and recovery sessions, booked in a minute."),

        Design(
            id: "ink", name: "Inkwell", category: "Photography",
            description: "Near-black and a single hot accent, square corners, nothing in the way of the work.",
            primary: "#111111", accent: "#ff5a36", background: "#f4f4f2", surface: "#e8e8e4",
            headingFont: "DM Sans", bodyFont: "DM Sans", radiusPx: 0,
            headline: "Portraits with a point of view.",
            blurb: "Studio and on-location sessions for people and brands."),

        Design(
            id: "table", name: "Saffron Table", category: "Restaurant and Hospitality",
            description: "Claret and old gold with a classical serif, for a menu worth planning around.",
            primary: "#5b1e1e", accent: "#c9a14a", background: "#fdf8f2", surface: "#f3e9dc",
            headingFont: "Playfair Display", bodyFont: "Figtree", radiusPx: 4,
            headline: "Dinner worth planning around.",
            blurb: "Seasonal tasting menus and a short, honest wine list."),

        Design(
            id: "ledger", name: "Ledger", category: "Professional Services",
            description: "Trustworthy navy and a clear blue accent, set in a plain-spoken sans.",
            primary: "#1e3a5f", accent: "#3b82f6", background: "#ffffff", surface: "#f2f5f9",
            headingFont: "DM Sans", bodyFont: "DM Sans", radiusPx: 8,
            headline: "Clear advice for growing businesses.",
            blurb: "Tax, bookkeeping and planning for owner-run companies."),
    };

    /// <summary>The home page's section list, shared by every design template.</summary>
    private static IReadOnlyList<(string Key, string Variant)> HomeBlueprint() =>
    [
        ("about", "image-left"),
        ("services", "cards"),
        ("gallery", "grid"),
        ("testimonials", "cards"),
        ("booking", "panel"),
    ];

    private static TemplateDefinition Design(
        string id, string name, string category, string description,
        string primary, string accent, string background, string surface,
        string headingFont, string bodyFont, int radiusPx,
        string headline, string blurb)
        => new(
            Id: id,
            Name: name,
            Description: description,
            Category: category,
            Tags: [id, "design"],
            HeaderVariant: "classic",
            HeroVariant: "overlay",
            FooterVariant: "columns",
            Theme: DesignTheme(primary, accent, background, surface, headingFont, bodyFont, radiusPx),
            Blueprint: HomeBlueprint(),
            Pages: StartingSite(headline, blurb));

    /// <summary>
    /// The design's five pages, each with its own hero copy and section list.
    /// Sample copy only — the business's name, address, hours and services are
    /// rendered from real data and are never seeded here.
    /// </summary>
    private static IReadOnlyList<TemplatePage> StartingSite(string headline, string blurb) =>
    [
        new("home", "Home", "", "home", headline, blurb, HomeBlueprint()),
        new("services", "Services", "services", "services",
            "Services and pricing", "Everything we offer, with honest prices.",
            [("services", "list"), ("booking", "panel")]),
        new("about", "About", "about", "about",
            "The people behind the studio", "A small team that cares about the details.",
            [("about", "image-right"), ("richtext", "quote"), ("testimonials", "cards")]),
        new("gallery", "Gallery", "gallery", "gallery",
            "Our work", "Recent cuts, colour and finishes.",
            [("gallery", "feature-first"), ("booking", "panel")]),
        new("contact", "Contact", "contact", "contact",
            "Come and say hello", "We are easy to find and easy to book.",
            [("contact", "split"), ("hours", "list")]),
    ];

    /// <summary>
    /// A design template's palette, translated onto the production token set: the
    /// design's keys map one-to-one where they exist, and production-only tokens
    /// are derived from them.
    /// </summary>
    private static StorefrontThemeDto DesignTheme(
        string primary, string accent, string background, string surface,
        string headingFont, string bodyFont, int radiusPx) => new()
    {
        Primary = primary,
        Secondary = primary,
        Accent = accent,
        Background = background,
        Surface = surface,
        Text = primary,
        ButtonBackground = primary,
        ButtonText = "#ffffff",
        Border = surface,
        HeadingFont = headingFont,
        BodyFont = bodyFont,
        HeadingWeight = 500,
        RadiusPx = radiusPx,
        Radius = RadiusToken(radiusPx),
        SectionSpacing = "normal",
        CardStyle = "soft",
        Shadow = "soft",
        ButtonStyle = "rounded",
        ImageRatio = "4:5",
        Mode = "light",
        Animation = "up",
        DurationMs = 600,
        HoverEffect = "lift",
        PageTransition = "fade",
    };

    private static string RadiusToken(int px) => px switch
    {
        <= 4 => "sharp",
        <= 12 => "soft",
        _ => "round",
    };
}
