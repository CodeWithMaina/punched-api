using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Services;

/// <summary>
/// Platform defaults + template presets for the Storefront Builder.
///
/// <para>A business with no stored builder configuration resolves to
/// <see cref="Create"/> — a complete, good-looking storefront with zero
/// configuration. Switching template is <see cref="ApplyTemplate"/>, which
/// overlays a template's design (theme, header/hero/footer variants, section
/// order + variants) while preserving the owner's content (titles, descriptions,
/// section settings, pages, SEO).</para>
/// </summary>
public static partial class StorefrontBuilderDefaults
{
    public const string DefaultTemplateId = "classic";

    /// <summary>Creates the default configuration for a business that has none.</summary>
    public static StorefrontConfigDto Create()
    {
        var config = new StorefrontConfigDto
        {
            TemplateId = DefaultTemplateId,
            Theme = new StorefrontThemeDto(),
            Header = new StorefrontHeaderDto(),
            Hero = new StorefrontHeroDto(),
            Footer = new StorefrontFooterDto(),
            Seo = new StorefrontSeoDto(),
            Sections = new List<StorefrontSectionDto>(),
            Pages = DefaultPages(),
        };
        return ApplyTemplate(config, DefaultTemplateId, preserveContent: false);
    }

    /// <summary>
    /// The four default pages every storefront starts with. `kind` binds a page
    /// to a real, renderable route; `custom` pages are navigation-only today.
    /// </summary>
    public static List<StorefrontPageDto> DefaultPages() =>
    [
        new StorefrontPageDto { Id = "page-home", Title = "Home", Slug = "", Kind = "home", ShowInNav = true, Order = 0 },
        new StorefrontPageDto { Id = "page-services", Title = "Services", Slug = "services", Kind = "services", ShowInNav = true, Order = 10 },
        new StorefrontPageDto { Id = "page-about", Title = "About", Slug = "about", Kind = "about", ShowInNav = true, Order = 20 },
        new StorefrontPageDto { Id = "page-contact", Title = "Contact", Slug = "contact", Kind = "contact", ShowInNav = true, Order = 30 },
    ];

    /// <summary>
    /// Overlays defaults over a stored (possibly partial/legacy) configuration so
    /// a payload can never produce a half-empty storefront. Unknown section keys
    /// are dropped; unknown variants are normalised to the section default.
    /// </summary>
    public static StorefrontConfigDto Merge(StorefrontConfigDto? stored)
    {
        var merged = Create();
        if (stored == null) return merged;

        // A known (or legacy-aliased) template id is canonicalised so a business
        // saved against an earlier revision resolves to the live template that
        // now carries its design.
        var canonicalTemplateId = StorefrontTemplates.CanonicalId(stored.TemplateId);
        if (canonicalTemplateId != null) merged.TemplateId = canonicalTemplateId;

        MergeTheme(merged.Theme, stored.Theme);
        MergeHeader(merged.Header, stored.Header);
        MergeHero(merged.Hero, stored.Hero);
        MergeFooter(merged.Footer, stored.Footer);

        if (stored.Seo != null)
            merged.Seo = stored.Seo;

        if (stored.Pages is { Count: > 0 })
            merged.Pages = stored.Pages
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id))
                .OrderBy(p => p.Order)
                .Select(MergePage)
                .ToList();

        if (stored.Sections is { Count: > 0 })
            merged.Sections = MergeSectionList(stored.Sections);

        return merged;
    }

    /// <summary>
    /// Normalises a section list: unknown types are dropped, an unknown variant
    /// falls back to the section's default, and every design control is clamped to
    /// the range its renderer can honour.
    /// </summary>
    private static List<StorefrontSectionDto> MergeSectionList(List<StorefrontSectionDto>? stored)
    {
        if (stored is not { Count: > 0 }) return new List<StorefrontSectionDto>();

        var sections = new List<StorefrontSectionDto>();
        foreach (var section in stored.Where(s => s != null && StorefrontSectionCatalog.IsKnown(s.Key)))
        {
            sections.Add(new StorefrontSectionDto
            {
                Id = string.IsNullOrWhiteSpace(section.Id) ? Guid.NewGuid().ToString("n") : section.Id,
                Key = section.Key,
                Variant = StorefrontSectionCatalog.NormaliseVariant(section.Key, section.Variant),
                Enabled = section.Enabled,
                Order = section.Order,
                Title = section.Title,
                Description = section.Description,
                Settings = section.Settings ?? new Dictionary<string, string>(),
                Columns = section.Columns > 0 ? Math.Clamp(section.Columns, 1, 4) : 3,
                Background = StorefrontLayoutCatalog.FromList(
                    section.Background, StorefrontLayoutCatalog.SectionBackgrounds, "default"),
                Padding = StorefrontLayoutCatalog.FromList(
                    section.Padding, StorefrontLayoutCatalog.SectionPaddings, "normal"),
                Alignment = section.Alignment is "center" ? "center" : "left",
                Animation = StorefrontLayoutCatalog.FromList(
                    section.Animation, StorefrontLayoutCatalog.Animations, "inherit"),
            });
        }

        return sections.OrderBy(s => s.Order).ToList();
    }

    /// <summary>
    /// Normalises one managed page. The home page never carries its own hero or
    /// sections — the storefront-wide pair is the single source of truth for it —
    /// so a design import cannot leave a stale per-page copy behind after a
    /// template switch.
    /// </summary>
    private static StorefrontPageDto MergePage(StorefrontPageDto stored)
    {
        var page = new StorefrontPageDto
        {
            Id = stored.Id,
            Title = stored.Title,
            Slug = stored.Slug ?? string.Empty,
            Kind = string.IsNullOrWhiteSpace(stored.Kind) ? "custom" : stored.Kind,
            ShowInNav = stored.ShowInNav,
            Order = stored.Order,
            SeoTitle = stored.SeoTitle,
            SeoDescription = stored.SeoDescription,
        };

        if (page.Kind.Equals("home", StringComparison.OrdinalIgnoreCase)) return page;

        if (stored.Hero != null)
        {
            page.Hero = new StorefrontHeroDto();
            MergeHero(page.Hero, stored.Hero);
        }

        if (stored.Sections is { Count: > 0 })
            page.Sections = MergeSectionList(stored.Sections);

        return page;
    }
}
