using PunchedApi.Application.Modules;

namespace PunchedApi.Application.Services;

/// <summary>
/// The storefront section vocabulary: every independently-configurable section
/// type, its design variants, and the customer capability that gates it.
///
/// <para>Mirrored by <c>src/lib/storefront-builder/sections.ts</c> in the owner
/// console so the editor and the server agree on keys and variants. Keeping the
/// vocabulary here (rather than inline in the renderer) is what lets the owner's
/// configuration drive the page.</para>
/// </summary>
public static class StorefrontSectionCatalog
{
    public sealed record SectionDefinition(
        string Key,
        string Label,
        string Description,
        IReadOnlyList<string> Variants,
        string DefaultVariant,
        /// <summary>Customer capability that must be entitled, or null if always allowed.</summary>
        string? Capability,
        string? DefaultTitle,
        string? DefaultDescription);

    public static readonly IReadOnlyList<SectionDefinition> Sections = new[]
    {
        new SectionDefinition("announcement", "Announcement", "A slim promotional bar.",
            ["bar", "pill"], "bar", null, null, null),
        new SectionDefinition("services", "Services", "Your bookable catalogue.",
            ["cards", "image-cards", "list", "grid", "scroller", "featured", "categories"],
            "cards", CustomerCapabilityCatalog.Services, "Our services", "Choose from our most-loved treatments."),
        new SectionDefinition("about", "About", "Your story and mission.",
            ["image-left", "image-right", "centered", "editorial", "gallery"], "image-left",
            null, "About us", null),
        new SectionDefinition("team", "Team", "The people behind the business.",
            ["cards", "circles", "showcase", "list"], "cards", null, "Meet the team", null),
        new SectionDefinition("gallery", "Gallery", "A curated image gallery.",
            ["masonry", "grid", "carousel", "full-width", "editorial", "feature-first"], "masonry",
            CustomerCapabilityCatalog.Gallery, "Our work", null),
        new SectionDefinition("testimonials", "Testimonials", "Words from your clients.",
            ["cards", "quote", "carousel", "wall"], "cards",
            CustomerCapabilityCatalog.GalleryReviews, "What clients say", null),
        new SectionDefinition("booking", "Booking", "A booking-focused call to action.",
            ["panel", "split", "banner", "minimal"], "panel",
            CustomerCapabilityCatalog.Appointments, "Book your visit", "Pick a time that suits you — it takes under a minute."),
        new SectionDefinition("promotions", "Promotions", "Special offers and packages.",
            ["cards", "banner", "list"], "cards", null, "Current offers", null),
        new SectionDefinition("loyalty", "Loyalty", "Your rewards programme.",
            ["panel", "card", "minimal"], "panel", CustomerCapabilityCatalog.Loyalty, "Rewards & loyalty", null),
        new SectionDefinition("stats", "Statistics", "Achievements at a glance.",
            ["row", "cards", "banner"], "row", null, null, null),
        new SectionDefinition("hours", "Opening hours", "When you are open.",
            ["list", "cards", "inline"], "list", null, "Opening hours", null),
        new SectionDefinition("contact", "Contact", "How to reach you.",
            ["split", "cards", "map", "minimal"], "split", null, "Get in touch", null),
        new SectionDefinition("faq", "FAQ", "Answers to common questions.",
            ["accordion", "two-column"], "accordion", null, "Frequently asked questions", null),
        new SectionDefinition("social", "Social", "Follow along.",
            ["row", "cards"], "row", null, "Follow us", null),
        new SectionDefinition("newsletter", "Newsletter", "Email signup.",
            ["inline", "panel"], "inline", null, "Stay in the loop", null),
        new SectionDefinition("richtext", "Text & image", "Free-form content.",
            ["text", "text-image", "image-text", "quote"], "text", null, null, null),
        new SectionDefinition("embed", "Embed", "A custom HTML embed.",
            ["frame"], "frame", null, null, null),
    };

    private static readonly Dictionary<string, SectionDefinition> ByKey =
        Sections.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

    public static SectionDefinition? Find(string key) =>
        ByKey.TryGetValue(key ?? string.Empty, out var def) ? def : null;

    public static bool IsKnown(string key) => ByKey.ContainsKey(key ?? string.Empty);

    /// <summary>Normalises an unknown/blank variant to the section's default.</summary>
    public static string NormaliseVariant(string key, string? variant)
    {
        var def = Find(key);
        if (def == null) return "default";
        if (string.IsNullOrWhiteSpace(variant)) return def.DefaultVariant;
        return def.Variants.Any(v => v.Equals(variant, StringComparison.OrdinalIgnoreCase))
            ? variant!
            : def.DefaultVariant;
    }

    /// <summary>
    /// True when the section may be shown to a visitor given the business's
    /// customer capabilities. Sections with no capability are always allowed.
    /// </summary>
    public static bool CapabilityAllows(string key, IReadOnlyDictionary<string, bool> capabilities)
    {
        var def = Find(key);
        if (def?.Capability == null) return true;
        return capabilities.TryGetValue(def.Capability, out var allowed) && allowed;
    }
}
