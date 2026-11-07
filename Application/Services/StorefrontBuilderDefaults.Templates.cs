using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Services;

public static partial class StorefrontBuilderDefaults
{
    /// <summary>
    /// Applies a template's design to a configuration. Content (section titles,
    /// descriptions, settings, media ids, pages and SEO) is preserved when
    /// <paramref name="preserveContent"/> is true — so switching template changes
    /// the look without discarding what the owner wrote.
    /// </summary>
    public static StorefrontConfigDto ApplyTemplate(
        StorefrontConfigDto config, string templateId, bool preserveContent)
    {
        var template = StorefrontTemplates.Find(templateId) ?? StorefrontTemplates.Find(DefaultTemplateId)!;
        config.TemplateId = template.Id;

        // Design tokens — always taken from the template.
        config.Theme = CloneTheme(template.Theme);

        config.Header.Variant = template.HeaderVariant;
        config.Header.Transparent = template.HeaderVariant is "minimal" or "floating" or "centered";
        config.Header.Sticky = template.HeaderVariant is "sticky-compact" or "solid" or "split";

        config.Hero.Variant = template.HeroVariant;
        config.Hero.TextAlign = template.HeroVariant switch
        {
            "centered" or "premium" or "gradient" or "video" => "center",
            "editorial" => "left",
            _ => "left",
        };
        config.Hero.Height = template.HeroVariant switch
        {
            "premium" or "video" => "screen",
            "minimal" => "compact",
            "centered" or "gradient" => "standard",
            _ => "tall",
        };
        config.Hero.BackgroundColor = template.Theme.Primary;

        config.Footer.Variant = template.FooterVariant;
        config.Footer.BackgroundColor = template.Theme.Primary;
        config.Footer.TextColor = template.Theme.Background;

        // Sections: rebuild from the blueprint, keeping content of any existing
        // section of the same key (in order, so duplicate keys map 1:1).
        var previous = new Dictionary<string, Queue<StorefrontSectionDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in config.Sections)
        {
            if (!previous.TryGetValue(existing.Key, out var queue))
            {
                queue = new Queue<StorefrontSectionDto>();
                previous[existing.Key] = queue;
            }
            queue.Enqueue(existing);
        }

        var rebuilt = new List<StorefrontSectionDto>();
        var order = 0;
        foreach (var (key, variant) in template.Blueprint)
        {
            var def = StorefrontSectionCatalog.Find(key);
            if (def == null) continue;

            StorefrontSectionDto? carried = null;
            if (previous.TryGetValue(key, out var queue) && queue.Count > 0) carried = queue.Dequeue();

            rebuilt.Add(new StorefrontSectionDto
            {
                Id = carried?.Id ?? $"{key}-{Guid.NewGuid():n}",
                Key = key,
                Variant = StorefrontSectionCatalog.NormaliseVariant(key, variant),
                Enabled = carried?.Enabled ?? true,
                Order = order += 10,
                Title = carried?.Title ?? (preserveContent ? null : def.DefaultTitle),
                Description = carried?.Description ?? (preserveContent ? null : def.DefaultDescription),
                Settings = carried?.Settings ?? new Dictionary<string, string>(),
            });
        }
        config.Sections = rebuilt;

        // The design's templates describe a whole site: each page's own hero copy
        // and section list is seeded here. Page titles, slugs and SEO the owner has
        // edited are preserved, and the home page never carries its own copies —
        // the storefront-wide pair above is its single source of truth.
        if (template.Pages is { Count: > 0 })
            config.Pages = BuildTemplatePages(config.Pages, template);
        else if (config.Pages.Count == 0)
            config.Pages = DefaultPages();

        return config;
    }

    private static List<StorefrontPageDto> BuildTemplatePages(
        List<StorefrontPageDto> current, StorefrontTemplates.TemplateDefinition template)
    {
        var existing = current
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var pages = new List<StorefrontPageDto>();
        var order = 0;

        foreach (var definition in template.Pages!)
        {
            existing.TryGetValue(definition.Id, out var carried);
            var isHome = definition.Kind.Equals("home", StringComparison.OrdinalIgnoreCase);

            var page = new StorefrontPageDto
            {
                Id = definition.Id,
                Title = carried?.Title ?? definition.Title,
                Slug = carried?.Slug ?? definition.Slug,
                Kind = definition.Kind,
                ShowInNav = carried?.ShowInNav ?? true,
                Order = order += 10,
                SeoTitle = carried?.SeoTitle,
                SeoDescription = carried?.SeoDescription,
            };

            if (!isHome)
            {
                page.Hero = new StorefrontHeroDto
                {
                    Variant = template.HeroVariant,
                    Heading = definition.HeroHeading,
                    Description = definition.HeroDescription,
                    BackgroundColor = template.Theme.Primary,
                    Height = "compact",
                };
                page.Sections = BuildSections(definition.Blueprint, carried?.Sections);
            }

            pages.Add(page);
        }

        return pages;
    }

    /// <summary>Builds a section list from a blueprint, carrying the content of the same key.</summary>
    private static List<StorefrontSectionDto> BuildSections(
        IReadOnlyList<(string Key, string Variant)> blueprint,
        List<StorefrontSectionDto>? carried)
    {
        var previous = new Dictionary<string, Queue<StorefrontSectionDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in carried ?? new List<StorefrontSectionDto>())
        {
            if (!previous.TryGetValue(section.Key, out var queue))
            {
                queue = new Queue<StorefrontSectionDto>();
                previous[section.Key] = queue;
            }
            queue.Enqueue(section);
        }

        var sections = new List<StorefrontSectionDto>();
        var order = 0;
        foreach (var (key, variant) in blueprint)
        {
            if (StorefrontSectionCatalog.Find(key) == null) continue;

            StorefrontSectionDto? existing = null;
            if (previous.TryGetValue(key, out var queue) && queue.Count > 0) existing = queue.Dequeue();

            sections.Add(new StorefrontSectionDto
            {
                Id = existing?.Id ?? $"{key}-{Guid.NewGuid():n}",
                Key = key,
                Variant = StorefrontSectionCatalog.NormaliseVariant(key, variant),
                Enabled = existing?.Enabled ?? true,
                Order = order += 10,
                Title = existing?.Title,
                Description = existing?.Description,
                Settings = existing?.Settings ?? new Dictionary<string, string>(),
            });
        }

        return sections;
    }

    private static StorefrontThemeDto CloneTheme(StorefrontThemeDto source) => new()
    {
        Primary = source.Primary,
        Secondary = source.Secondary,
        Accent = source.Accent,
        Background = source.Background,
        Surface = source.Surface,
        Text = source.Text,
        MutedText = source.MutedText,
        ButtonBackground = source.ButtonBackground,
        ButtonText = source.ButtonText,
        Border = source.Border,
        HeadingFont = source.HeadingFont,
        BodyFont = source.BodyFont,
        HeadingWeight = source.HeadingWeight,
        BodyWeight = source.BodyWeight,
        BaseFontSize = source.BaseFontSize,
        HeadingScale = source.HeadingScale,
        LineHeight = source.LineHeight,
        LetterSpacing = source.LetterSpacing,
        ContentWidth = source.ContentWidth,
        SectionSpacing = source.SectionSpacing,
        ContainerPadding = source.ContainerPadding,
        Radius = source.Radius,
        RadiusPx = source.RadiusPx,
        CardStyle = source.CardStyle,
        Shadow = source.Shadow,
        ImageRatio = source.ImageRatio,
        ButtonStyle = source.ButtonStyle,
        BackgroundStyle = source.BackgroundStyle,
        BackgroundImageMediaId = source.BackgroundImageMediaId,
        GradientFrom = source.GradientFrom,
        GradientTo = source.GradientTo,
        Mode = source.Mode,
        Animation = source.Animation,
        DurationMs = source.DurationMs,
        HoverEffect = source.HoverEffect,
        PageTransition = source.PageTransition,
    };
}
