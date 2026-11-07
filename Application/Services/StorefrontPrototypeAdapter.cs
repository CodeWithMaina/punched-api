using System.Text.Json;
using System.Text.Json.Serialization;
using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Services;

/// <summary>
/// Translates between the storefront design's configuration object and the
/// production <see cref="StorefrontConfigDto"/>.
///
/// <para>Why an adapter rather than one shared model: the design carries eleven
/// theme keys, single-letter field names and a per-page hero/section list, while
/// production carries ~30 theme tokens, a page list that drives navigation, and
/// dictionary-backed section settings. Both are legitimate; the adapter is the one
/// place that knows how they correspond, so a design payload can seed a real
/// storefront and a real storefront can be read and inspected in the design's own
/// vocabulary.</para>
///
/// <para><b>Contract.</b> Every key the design defines survives a round trip. Keys
/// the design does not have (the business name, production-only tokens, resolved
/// media) are derived on import and deliberately not invented on export.</para>
/// </summary>
public static class StorefrontPrototypeAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Parses the design's configuration object, or null when it is not valid.</summary>
    public static PrototypeConfig? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<PrototypeConfig>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── The design's own shape (its exact key names) ────────────────────────────

    public sealed class PrototypeConfig
    {
        [JsonPropertyName("tpl")] public string Tpl { get; set; } = "classic";
        /// <summary>The design's sample business name. Never exported: the real
        /// business name is the business's own.</summary>
        [JsonPropertyName("biz")] public string? Biz { get; set; }
        [JsonPropertyName("theme")] public PrototypeTheme Theme { get; set; } = new();
        [JsonPropertyName("header")] public PrototypeHeader Header { get; set; } = new();
        [JsonPropertyName("foot")] public PrototypeFooter Foot { get; set; } = new();
        [JsonPropertyName("pages")] public List<PrototypePage> Pages { get; set; } = new();
    }

    public sealed class PrototypeTheme
    {
        [JsonPropertyName("p")] public string P { get; set; } = "#1c1917";
        [JsonPropertyName("a")] public string A { get; set; } = "#b45309";
        [JsonPropertyName("bg")] public string Bg { get; set; } = "#fffdf8";
        [JsonPropertyName("sf")] public string Sf { get; set; } = "#f5f0e6";
        [JsonPropertyName("hf")] public string Hf { get; set; } = "Newsreader";
        [JsonPropertyName("bf")] public string Bf { get; set; } = "Figtree";
        [JsonPropertyName("r")] public int R { get; set; }
        [JsonPropertyName("btn")] public string Btn { get; set; } = "#1c1917";
        [JsonPropertyName("bt")] public string Bt { get; set; } = "#ffffff";
        [JsonPropertyName("mode")] public string Mode { get; set; } = "light";
        [JsonPropertyName("anim")] public string Anim { get; set; } = "up";
        [JsonPropertyName("hover")] public string Hover { get; set; } = "lift";
        [JsonPropertyName("sp")] public int Sp { get; set; } = 600;
        [JsonPropertyName("pt")] public string Pt { get; set; } = "fade";
    }

    public sealed class PrototypeHeader
    {
        [JsonPropertyName("layout")] public string Layout { get; set; } = "classic";
        [JsonPropertyName("sticky")] public bool Sticky { get; set; }
        [JsonPropertyName("tr")] public bool Tr { get; set; }
        [JsonPropertyName("name")] public bool Name { get; set; } = true;
        [JsonPropertyName("hbg")] public string Hbg { get; set; } = "#ffffff";
        [JsonPropertyName("htx")] public string Htx { get; set; } = "#1c1b19";
        [JsonPropertyName("cta")] public string Cta { get; set; } = "Book now";
        [JsonPropertyName("pad")] public int Pad { get; set; } = 18;
        [JsonPropertyName("shadow")] public bool Shadow { get; set; }
        [JsonPropertyName("border")] public bool Border { get; set; }
        [JsonPropertyName("drawer")] public string Drawer { get; set; } = "left";
        [JsonPropertyName("dcta")] public bool Dcta { get; set; } = true;
        [JsonPropertyName("fx")] public string Fx { get; set; } = "underline";
        [JsonPropertyName("ctas")] public string Ctas { get; set; } = "solid";
        [JsonPropertyName("caps")] public bool Caps { get; set; }
        [JsonPropertyName("nav")] public List<PrototypeNavItem> Nav { get; set; } = new();
    }

    public sealed class PrototypeNavItem
    {
        [JsonPropertyName("l")] public string L { get; set; } = string.Empty;
        [JsonPropertyName("on")] public bool On { get; set; } = true;
        [JsonPropertyName("to")] public string To { get; set; } = "home";
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("kids")] public List<PrototypeNavItem> Kids { get; set; } = new();
    }

    public sealed class PrototypeFooter
    {
        [JsonPropertyName("layout")] public string Layout { get; set; } = "columns";
        [JsonPropertyName("bg")] public string Bg { get; set; } = "#1c1917";
        [JsonPropertyName("tx")] public string Tx { get; set; } = "#fffdf8";
        [JsonPropertyName("desc")] public string Desc { get; set; } = string.Empty;
        [JsonPropertyName("cp")] public string Cp { get; set; } = string.Empty;
        [JsonPropertyName("soc")] public bool Soc { get; set; } = true;
        [JsonPropertyName("hrs")] public bool Hrs { get; set; } = true;
        [JsonPropertyName("legal")] public bool Legal { get; set; } = true;
        [JsonPropertyName("pad")] public int Pad { get; set; } = 72;
        [JsonPropertyName("news")] public string? News { get; set; }
    }

    public sealed class PrototypePage
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("t")] public string T { get; set; } = string.Empty;
        [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
        [JsonPropertyName("seo")] public string Seo { get; set; } = string.Empty;
        [JsonPropertyName("sd")] public string Sd { get; set; } = string.Empty;
        [JsonPropertyName("hero")] public PrototypeHero? Hero { get; set; }
        [JsonPropertyName("secs")] public List<PrototypeSection> Secs { get; set; } = new();
    }

    public sealed class PrototypeHero
    {
        [JsonPropertyName("on")] public bool On { get; set; } = true;
        [JsonPropertyName("l")] public string L { get; set; } = "overlay";
        [JsonPropertyName("h")] public string H { get; set; } = string.Empty;
        [JsonPropertyName("d")] public string D { get; set; } = string.Empty;
        [JsonPropertyName("c1")] public string C1 { get; set; } = string.Empty;
        [JsonPropertyName("c2")] public string C2 { get; set; } = string.Empty;
        [JsonPropertyName("img")] public string Img { get; set; } = string.Empty;
        [JsonPropertyName("col")] public string Col { get; set; } = "#000000";
        [JsonPropertyName("ov")] public int Ov { get; set; } = 55;
        [JsonPropertyName("al")] public string Al { get; set; } = "left";
        [JsonPropertyName("ht")] public string Ht { get; set; } = "normal";
        [JsonPropertyName("an")] public string An { get; set; } = "inherit";
        [JsonPropertyName("eb")] public string Eb { get; set; } = string.Empty;
    }

    public sealed class PrototypeSection
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("k")] public string K { get; set; } = string.Empty;
        [JsonPropertyName("on")] public bool On { get; set; } = true;
        [JsonPropertyName("v")] public string V { get; set; } = "a";
        [JsonPropertyName("bg")] public string Bg { get; set; } = "default";
        [JsonPropertyName("pad")] public string Pad { get; set; } = "normal";
        [JsonPropertyName("al")] public string Al { get; set; } = "left";
        [JsonPropertyName("anim")] public string Anim { get; set; } = "inherit";
        [JsonPropertyName("cols")] public int Cols { get; set; } = 3;
        [JsonPropertyName("d")] public PrototypeSectionContent D { get; set; } = new();
    }

    public sealed class PrototypeSectionContent
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("lead")] public string? Lead { get; set; }
        [JsonPropertyName("btn")] public string? Btn { get; set; }
        [JsonPropertyName("img")] public string? Img { get; set; }
        /// <summary>Rows of `[primary, secondary, image]` for list-style sections.</summary>
        [JsonPropertyName("items")] public List<List<string>> Items { get; set; } = new();
    }

    /// <summary>Section settings keys the adapter and the renderer agree on.</summary>
    public static class SettingKeys
    {
        public const string CtaLabel = "ctaLabel";
        public const string Cite = "cite";
        public const string ImageUrl = "imageUrl";
        public const string Items = "items";
    }

    /// <summary>
    /// Builds a production configuration from the design's object. Anything the
    /// design cannot express (resolved media, SEO images) keeps its production
    /// default rather than being invented.
    /// </summary>
    public static StorefrontConfigDto Import(PrototypeConfig source)
    {
        var config = StorefrontBuilderDefaults.Create();
        config.TemplateId = StorefrontTemplates.CanonicalId(source.Tpl) ?? config.TemplateId;

        ImportTheme(config.Theme, source.Theme);
        ImportHeader(config.Header, source.Header);
        ImportFooter(config.Footer, source.Foot);

        if (source.Pages.Count > 0)
        {
            config.Pages = source.Pages.Select(ImportPage).ToList();

            // The design keeps the hero and sections per page; production keeps a
            // storefront-wide pair for the home page. The home page's own values
            // become the storefront-wide ones, and the per-page copies stay on the
            // page so the multi-page renderer has them.
            var home = source.Pages.FirstOrDefault(p => string.IsNullOrEmpty(p.Slug)) ?? source.Pages[0];
            ImportHero(config.Hero, home.Hero);
            config.Sections = home.Secs.Select(ImportSection).ToList();
        }

        return config;
    }

    private static void ImportTheme(StorefrontThemeDto target, PrototypeTheme source)
    {
        target.Primary = source.P;
        target.Secondary = source.P;
        target.Accent = source.A;
        target.Background = source.Bg;
        target.Surface = source.Sf;
        target.Text = source.P;
        target.ButtonBackground = source.Btn;
        target.ButtonText = source.Bt;
        target.HeadingFont = source.Hf;
        target.BodyFont = source.Bf;
        target.RadiusPx = source.R;
        target.Radius = RadiusTokenFor(source.R);
        target.Mode = StorefrontLayoutCatalog.FromList(source.Mode, StorefrontLayoutCatalog.Modes, "light");
        target.Animation = StorefrontLayoutCatalog.FromList(source.Anim, StorefrontLayoutCatalog.Animations, "up");
        target.DurationMs = Math.Clamp(source.Sp, 200, 1500);
        target.HoverEffect = StorefrontLayoutCatalog.FromList(source.Hover, StorefrontLayoutCatalog.HoverEffects, "lift");
        target.PageTransition = StorefrontLayoutCatalog.FromList(source.Pt, StorefrontLayoutCatalog.PageTransitions, "fade");
    }

    private static void ImportHeader(StorefrontHeaderDto target, PrototypeHeader source)
    {
        target.Variant = StorefrontLayoutCatalog.NormaliseHeaderLayout(source.Layout);
        target.Sticky = source.Sticky;
        target.Transparent = source.Tr;
        target.ShowBusinessName = source.Name;
        target.Background = source.Hbg;
        target.TextColor = source.Htx;
        target.CtaLabel = source.Cta;
        target.Height = Math.Clamp(source.Pad, 6, 40);
        target.Shadow = source.Shadow;
        target.Border = source.Border;
        target.Drawer = StorefrontLayoutCatalog.FromList(source.Drawer, StorefrontLayoutCatalog.DrawerStyles, "left");
        target.ShowContactInDrawer = source.Dcta;
        target.LinkEffect = StorefrontLayoutCatalog.FromList(
            source.Fx, ["underline", "pill", "dot", "fade"], "underline");
        target.CtaStyle = StorefrontLayoutCatalog.FromList(
            source.Ctas, ["solid", "outline", "soft", "dark"], "solid");
        target.UppercaseLinks = source.Caps;
        target.Nav = source.Nav
            .Select(ImportNavItem)
            .Where(item => !string.IsNullOrWhiteSpace(item.Label))
            .ToList();
    }

    private static StorefrontNavItemDto ImportNavItem(PrototypeNavItem source) => new()
    {
        Label = source.L,
        Enabled = source.On,
        Destination = string.IsNullOrWhiteSpace(source.To) ? "home" : source.To,
        Url = source.Url ?? string.Empty,
        Children = source.Kids.Select(ImportNavItem).ToList(),
    };

    private static void ImportFooter(StorefrontFooterDto target, PrototypeFooter source)
    {
        target.Variant = StorefrontLayoutCatalog.NormaliseFooterLayout(source.Layout);
        target.BackgroundColor = source.Bg;
        target.TextColor = source.Tx;
        target.Description = NullIfBlank(source.Desc);
        target.Copyright = NullIfBlank(source.Cp);
        target.ShowSocial = source.Soc;
        target.ShowHours = source.Hrs;
        target.ShowLegal = source.Legal;
        target.Padding = Math.Clamp(source.Pad, 24, 120);
        target.NewsletterHeadline = NullIfBlank(source.News);
        target.ShowWordmark = target.Variant.Equals("wordmark", StringComparison.OrdinalIgnoreCase);
    }

    private static StorefrontPageDto ImportPage(PrototypePage source)
    {
        var slug = source.Slug ?? string.Empty;
        var page = new StorefrontPageDto
        {
            Id = string.IsNullOrWhiteSpace(source.Id) ? $"page-{Guid.NewGuid():n}" : source.Id,
            Title = source.T,
            Slug = slug,
            Kind = KindFor(slug),
            ShowInNav = true,
            SeoTitle = NullIfBlank(source.Seo),
            SeoDescription = NullIfBlank(source.Sd),
        };

        if (source.Hero != null)
        {
            page.Hero = new StorefrontHeroDto();
            ImportHero(page.Hero, source.Hero);
        }

        if (source.Secs.Count > 0)
            page.Sections = source.Secs.Select(ImportSection).ToList();

        return page;
    }

    private static void ImportHero(StorefrontHeroDto target, PrototypeHero? source)
    {
        if (source == null) return;
        target.Variant = StorefrontLayoutCatalog.NormaliseHeroLayout(source.L);
        target.Enabled = source.On;
        target.Heading = NullIfBlank(source.H);
        target.Description = NullIfBlank(source.D);
        target.PrimaryCtaLabel = source.C1;
        target.SecondaryCtaLabel = source.C2;
        target.OverlayOpacity = Math.Clamp(source.Ov, 0, 100);
        target.BackgroundColor = source.Col;
        target.TextAlign = source.Al is "center" or "right" ? source.Al : "left";
        target.Height = StorefrontLayoutCatalog.FromList(
            source.Ht, ["compact", "normal", "tall", "standard", "screen"], "normal");
        target.Animation = StorefrontLayoutCatalog.FromList(
            source.An, StorefrontLayoutCatalog.Animations, "inherit");
        target.Eyebrow = NullIfBlank(source.Eb);
    }

    private static StorefrontSectionDto ImportSection(PrototypeSection source)
    {
        var section = new StorefrontSectionDto
        {
            Id = string.IsNullOrWhiteSpace(source.Id) ? $"{source.K}-{Guid.NewGuid():n}" : source.Id,
            Key = source.K,
            Variant = SectionVariantToProduction(source.K, source.V),
            Enabled = source.On,
            Title = NullIfBlank(source.D.Title),
            Description = NullIfBlank(source.D.Lead),
            Background = StorefrontLayoutCatalog.FromList(
                source.Bg, StorefrontLayoutCatalog.SectionBackgrounds, "default"),
            Padding = StorefrontLayoutCatalog.FromList(
                source.Pad, StorefrontLayoutCatalog.SectionPaddings, "normal"),
            Alignment = source.Al is "center" ? "center" : "left",
            Animation = StorefrontLayoutCatalog.FromList(
                source.Anim, StorefrontLayoutCatalog.Animations, "inherit"),
            Columns = Math.Clamp(source.Cols, 1, 4),
        };

        // Typed content. `richtext` has no heading in the design — its body is the
        // quote and its button is the attribution.
        var isQuote = source.K.Equals("richtext", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(source.D.Btn))
            section.Settings[isQuote ? SettingKeys.Cite : SettingKeys.CtaLabel] = source.D.Btn!;

        if (!string.IsNullOrWhiteSpace(source.D.Img))
            section.Settings[SettingKeys.ImageUrl] = source.D.Img!;

        if (source.D.Items.Count > 0)
            section.Settings[SettingKeys.Items] = string.Join('\n',
                source.D.Items.Select(row => string.Join(" | ", row.Where(value => value != null))));

        return section;
    }

    /// <summary>The design's `a`/`b` section layouts, per section type.</summary>
    private static readonly Dictionary<string, (string A, string B)> SectionVariantMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["about"] = ("image-left", "image-right"),
            ["services"] = ("cards", "list"),
            ["gallery"] = ("grid", "feature-first"),
            ["testimonials"] = ("cards", "quote"),
        };

    /// <summary>`a`/`b` (or already a production variant) → the production variant.</summary>
    public static string SectionVariantToProduction(string key, string? variant)
    {
        if (string.IsNullOrWhiteSpace(variant)) return StorefrontSectionCatalog.NormaliseVariant(key, null);

        var letter = variant.Trim();
        if (SectionVariantMap.TryGetValue(key ?? string.Empty, out var map))
        {
            if (letter.Equals("a", StringComparison.OrdinalIgnoreCase)) return map.A;
            if (letter.Equals("b", StringComparison.OrdinalIgnoreCase)) return map.B;
        }

        // Anything else is already a production variant name.
        return StorefrontSectionCatalog.NormaliseVariant(key, letter);
    }

    /// <summary>The production variant → the design's `a`/`b`.</summary>
    public static string SectionVariantToPrototype(string key, string? variant)
    {
        if (!SectionVariantMap.TryGetValue(key ?? string.Empty, out var map)) return "a";
        var current = StorefrontSectionCatalog.NormaliseVariant(key, variant);
        return current.Equals(map.B, StringComparison.OrdinalIgnoreCase) ? "b" : "a";
    }

    private static string KindFor(string slug) => slug.ToLowerInvariant() switch
    {
        "" => "home",
        "services" => "services",
        "about" => "about",
        "contact" => "contact",
        "gallery" => "gallery",
        "reviews" => "reviews",
        "team" => "team",
        "book" or "booking" => "booking",
        _ => "custom",
    };

    private static string RadiusTokenFor(int px) => px switch
    {
        <= 4 => "sharp",
        <= 12 => "soft",
        _ => "round",
    };

    private static int RadiusTokenToPx(string? token) => token?.ToLowerInvariant() switch
    {
        "sharp" => 2,
        "round" => 18,
        _ => 8,
    };

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Renders a production configuration back into the design's object, so a real
    /// storefront can be exported and inspected in the design's own vocabulary.
    /// </summary>
    public static PrototypeConfig Export(StorefrontConfigDto config, string? businessName = null)
    {
        var home = config.Pages.FirstOrDefault(p => p.Kind.Equals("home", StringComparison.OrdinalIgnoreCase))
                   ?? config.Pages.FirstOrDefault();

        var prototype = new PrototypeConfig
        {
            Tpl = config.TemplateId,
            Biz = businessName,
            Theme = ExportTheme(config.Theme),
            Header = ExportHeader(config.Header),
            Foot = ExportFooter(config.Footer),
        };

        prototype.Pages = config.Pages
            .Select(page => ExportPage(page, config, ReferenceEquals(page, home)))
            .ToList();

        if (prototype.Pages.Count == 0)
        {
            prototype.Pages.Add(new PrototypePage
            {
                Id = "page-home",
                T = "Home",
                Slug = string.Empty,
                Hero = ExportHero(config.Hero),
                Secs = config.Sections.Select(ExportSection).ToList(),
            });
        }

        return prototype;
    }

    /// <summary>The design's object as JSON — used by the export endpoint.</summary>
    public static string ExportJson(StorefrontConfigDto config, string? businessName = null) =>
        JsonSerializer.Serialize(Export(config, businessName), Json);

    private static PrototypeTheme ExportTheme(StorefrontThemeDto source) => new()
    {
        P = source.Primary,
        A = source.Accent,
        Bg = source.Background,
        Sf = source.Surface,
        Hf = source.HeadingFont,
        Bf = source.BodyFont,
        R = source.RadiusPx ?? RadiusTokenToPx(source.Radius),
        Btn = source.ButtonBackground,
        Bt = source.ButtonText,
        Mode = source.Mode,
        Anim = source.Animation,
        Hover = source.HoverEffect,
        Sp = source.DurationMs,
        Pt = source.PageTransition,
    };

    private static PrototypeHeader ExportHeader(StorefrontHeaderDto source) => new()
    {
        Layout = StorefrontLayoutCatalog.NormaliseHeaderLayout(source.Variant),
        Sticky = source.Sticky,
        Tr = source.Transparent,
        Name = source.ShowBusinessName,
        Hbg = source.Background,
        Htx = source.TextColor,
        Cta = source.CtaLabel,
        Pad = source.Height,
        Shadow = source.Shadow,
        Border = source.Border,
        Drawer = source.Drawer,
        Dcta = source.ShowContactInDrawer,
        Fx = source.LinkEffect,
        Ctas = source.CtaStyle,
        Caps = source.UppercaseLinks,
        Nav = source.Nav.Select(ExportNavItem).ToList(),
    };

    private static PrototypeNavItem ExportNavItem(StorefrontNavItemDto source) => new()
    {
        L = source.Label,
        On = source.Enabled,
        To = source.Destination,
        Url = source.Url,
        Kids = source.Children.Select(ExportNavItem).ToList(),
    };

    private static PrototypeFooter ExportFooter(StorefrontFooterDto source) => new()
    {
        Layout = StorefrontLayoutCatalog.NormaliseFooterLayout(source.Variant),
        Bg = source.BackgroundColor,
        Tx = source.TextColor,
        Desc = source.Description ?? string.Empty,
        Cp = source.Copyright ?? string.Empty,
        Soc = source.ShowSocial,
        Hrs = source.ShowHours,
        Legal = source.ShowLegal,
        Pad = source.Padding,
        News = source.NewsletterHeadline,
    };

    private static PrototypePage ExportPage(StorefrontPageDto page, StorefrontConfigDto config, bool isHome)
    {
        // The home page always reads from the storefront-wide hero/sections: that
        // pair is the single source of truth for it (see StorefrontBuilderDefaults.Merge).
        var hero = isHome ? config.Hero : page.Hero;
        var sections = isHome
            ? config.Sections
            : page.Sections ?? new List<StorefrontSectionDto>();

        return new PrototypePage
        {
            Id = page.Id,
            T = page.Title,
            Slug = page.Slug,
            Seo = page.SeoTitle ?? string.Empty,
            Sd = page.SeoDescription ?? string.Empty,
            Hero = hero == null ? null : ExportHero(hero),
            Secs = sections.Select(ExportSection).ToList(),
        };
    }

    private static PrototypeHero ExportHero(StorefrontHeroDto source) => new()
    {
        On = source.Enabled,
        L = StorefrontLayoutCatalog.NormaliseHeroLayout(source.Variant),
        H = source.Heading ?? string.Empty,
        D = source.Description ?? string.Empty,
        C1 = source.PrimaryCtaLabel,
        C2 = source.SecondaryCtaLabel,
        Col = source.BackgroundColor,
        Ov = source.OverlayOpacity,
        Al = source.TextAlign,
        Ht = source.Height,
        An = source.Animation,
        Eb = source.Eyebrow ?? string.Empty,
        // The hero image is stored as a media id and resolved at render time, so
        // there is no URL to export; the design's key is kept, as an empty string.
        Img = string.Empty,
    };

    private static PrototypeSection ExportSection(StorefrontSectionDto source)
    {
        var settings = source.Settings ?? new Dictionary<string, string>();
        var isQuote = source.Key.Equals("richtext", StringComparison.OrdinalIgnoreCase);

        settings.TryGetValue(isQuote ? SettingKeys.Cite : SettingKeys.CtaLabel, out var cta);
        settings.TryGetValue(SettingKeys.ImageUrl, out var image);
        settings.TryGetValue(SettingKeys.Items, out var items);

        return new PrototypeSection
        {
            Id = source.Id,
            K = source.Key,
            On = source.Enabled,
            V = SectionVariantToPrototype(source.Key, source.Variant),
            Bg = source.Background,
            Pad = source.Padding,
            Al = source.Alignment,
            Anim = source.Animation,
            Cols = source.Columns,
            D = new PrototypeSectionContent
            {
                Title = source.Title,
                Lead = source.Description,
                Btn = cta,
                Img = image,
                Items = string.IsNullOrWhiteSpace(items)
                    ? new List<List<string>>()
                    : items
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(line => line.Split('|').Select(part => part.Trim()).ToList())
                        .ToList(),
            },
        };
    }
}
