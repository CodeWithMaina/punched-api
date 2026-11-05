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
                .ToList();

        if (stored.Sections is { Count: > 0 })
        {
            var sections = new List<StorefrontSectionDto>();
            foreach (var section in stored.Sections.Where(s => s != null && StorefrontSectionCatalog.IsKnown(s.Key)))
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
                });
            }
            merged.Sections = sections.OrderBy(s => s.Order).ToList();
        }

        return merged;
    }
}
