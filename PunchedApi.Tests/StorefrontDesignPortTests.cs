using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;

namespace PunchedApi.Tests;

/// <summary>
/// The storefront design port: the adapter that translates between the design's
/// configuration object and the production one, the design's theme presets and
/// layout vocabulary, and the guarantees that make the port additive rather than
/// breaking.
/// </summary>
public class StorefrontDesignPortTests
{
    /// <summary>
    /// A configuration in the design's own shape (`fresh('atelier')`), carrying
    /// every key the design can produce.
    /// </summary>
    private const string DesignJson = """
    {
      "tpl": "atelier",
      "biz": "Atelier Hair Studio",
      "theme": {
        "p": "#1c1917", "a": "#b45309", "bg": "#fffdf8", "sf": "#f5f0e6",
        "hf": "Newsreader", "bf": "Figtree", "r": 0,
        "btn": "#1c1917", "bt": "#ffffff",
        "mode": "light", "anim": "up", "hover": "lift", "sp": 600, "pt": "fade"
      },
      "header": {
        "layout": "classic", "sticky": false, "tr": true, "name": true,
        "hbg": "#ffffff", "htx": "#1c1b19", "cta": "Book now", "pad": 18,
        "shadow": false, "border": false, "drawer": "left", "dcta": true,
        "fx": "underline", "ctas": "solid", "caps": false,
        "nav": [
          { "l": "Home", "on": true, "to": "home", "url": "", "kids": [] },
          { "l": "Services", "on": true, "to": "services", "url": "",
            "kids": [ { "l": "Hair", "on": true, "to": "services", "url": "", "kids": [] } ] }
        ]
      },
      "foot": {
        "layout": "columns", "bg": "#1c1917", "tx": "#fffdf8",
        "desc": "Independent studio.", "cp": "(c) 2026 Atelier",
        "soc": true, "hrs": true, "legal": true, "pad": 72,
        "news": "Ready when you are?"
      },
      "pages": [
        { "id": "home", "t": "Home", "slug": "", "seo": "", "sd": "",
          "hero": {
            "on": true, "l": "overlay", "h": "The art of a good haircut.",
            "d": "Colour, cut and care.", "c1": "Book an appointment", "c2": "View services",
            "img": "", "col": "#1c1917", "ov": 55, "al": "left", "ht": "normal",
            "an": "inherit", "eb": ""
          },
          "secs": [
            { "id": "about1", "k": "about", "on": true, "v": "a", "bg": "default",
              "pad": "normal", "al": "left", "anim": "inherit", "cols": 3,
              "d": { "title": "A studio built around you", "lead": "We keep the team small.",
                     "btn": "Meet the team", "img": "", "items": [] } },
            { "id": "svc1", "k": "services", "on": true, "v": "a", "bg": "surface",
              "pad": "generous", "al": "center", "anim": "up", "cols": 2,
              "d": { "title": "Our services", "lead": "Choose from our most-loved treatments.",
                     "btn": "", "img": "",
                     "items": [ [ "Signature cut", "45 min", "" ], [ "Colour", "2 hr", "" ] ] } }
          ]
        },
        { "id": "services", "t": "Services", "slug": "services", "seo": "", "sd": "",
          "hero": {
            "on": true, "l": "minimal", "h": "Services and pricing",
            "d": "Everything we offer.", "c1": "Book an appointment", "c2": "",
            "img": "", "col": "#1c1917", "ov": 55, "al": "left", "ht": "compact",
            "an": "inherit", "eb": ""
          },
          "secs": [
            { "id": "svc2", "k": "services", "on": true, "v": "b", "bg": "default",
              "pad": "normal", "al": "left", "anim": "inherit", "cols": 3,
              "d": { "title": "Services", "lead": "", "btn": "", "img": "", "items": [] } }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void ImportCarriesEveryKeyTheDesignDefines()
    {
        var prototype = StorefrontPrototypeAdapter.Parse(DesignJson);
        Assert.NotNull(prototype);

        var config = StorefrontPrototypeAdapter.Import(prototype!);

        // Theme: all eleven of the design's keys survive.
        Assert.Equal("atelier", config.TemplateId);
        Assert.Equal("#1c1917", config.Theme.Primary);
        Assert.Equal("#b45309", config.Theme.Accent);
        Assert.Equal("#fffdf8", config.Theme.Background);
        Assert.Equal("#f5f0e6", config.Theme.Surface);
        Assert.Equal("Newsreader", config.Theme.HeadingFont);
        Assert.Equal("Figtree", config.Theme.BodyFont);
        Assert.Equal(0, config.Theme.RadiusPx);
        Assert.Equal("#1c1917", config.Theme.ButtonBackground);
        Assert.Equal("#ffffff", config.Theme.ButtonText);
        Assert.Equal("light", config.Theme.Mode);
        Assert.Equal("up", config.Theme.Animation);
        Assert.Equal("lift", config.Theme.HoverEffect);
        Assert.Equal(600, config.Theme.DurationMs);
        Assert.Equal("fade", config.Theme.PageTransition);

        // Header: every control, including the independent nav tree with dropdowns.
        Assert.Equal("classic", config.Header.Variant);
        Assert.False(config.Header.Sticky);
        Assert.True(config.Header.Transparent);
        Assert.Equal("#ffffff", config.Header.Background);
        Assert.Equal("#1c1b19", config.Header.TextColor);
        Assert.Equal("Book now", config.Header.CtaLabel);
        Assert.Equal(18, config.Header.Height);
        Assert.Equal("left", config.Header.Drawer);
        Assert.Equal("underline", config.Header.LinkEffect);
        Assert.Equal("solid", config.Header.CtaStyle);
        Assert.False(config.Header.UppercaseLinks);
        Assert.True(config.Header.ShowContactInDrawer);
        Assert.Equal(2, config.Header.Nav.Count);
        Assert.Equal("Services", config.Header.Nav[1].Label);
        Assert.Equal("Hair", Assert.Single(config.Header.Nav[1].Children).Label);

        // Footer.
        Assert.Equal("columns", config.Footer.Variant);
        Assert.Equal("#1c1917", config.Footer.BackgroundColor);
        Assert.Equal("#fffdf8", config.Footer.TextColor);
        Assert.Equal("Independent studio.", config.Footer.Description);
        Assert.Equal("(c) 2026 Atelier", config.Footer.Copyright);
        Assert.Equal(72, config.Footer.Padding);
        Assert.Equal("Ready when you are?", config.Footer.NewsletterHeadline);

        // The home hero, and typed section content mapped onto production settings.
        Assert.Equal("overlay", config.Hero.Variant);
        Assert.Equal("The art of a good haircut.", config.Hero.Heading);
        Assert.Equal("Colour, cut and care.", config.Hero.Description);
        Assert.Equal(55, config.Hero.OverlayOpacity);
        Assert.Equal("#1c1917", config.Hero.BackgroundColor);

        Assert.Equal(2, config.Sections.Count);
        Assert.Equal("about", config.Sections[0].Key);
        Assert.Equal("image-left", config.Sections[0].Variant);      // the design's `a`
        Assert.Equal("A studio built around you", config.Sections[0].Title);
        Assert.Equal("We keep the team small.", config.Sections[0].Description);
        Assert.Equal("Meet the team", config.Sections[0].Settings["ctaLabel"]);

        Assert.Equal("cards", config.Sections[1].Variant);
        Assert.Equal("surface", config.Sections[1].Background);
        Assert.Equal("generous", config.Sections[1].Padding);
        Assert.Equal("center", config.Sections[1].Alignment);
        Assert.Equal(2, config.Sections[1].Columns);
        Assert.Contains("Signature cut | 45 min", config.Sections[1].Settings["items"]);

        // The design's pages, each with its own hero copy and section list.
        Assert.Equal(new[] { "home", "services" }, config.Pages.Select(p => p.Id));
        Assert.Equal("services", config.Pages[1].Slug);
        Assert.Equal("services", config.Pages[1].Kind);
        Assert.Equal("Services and pricing", config.Pages[1].Hero!.Heading);
        Assert.Equal("list", Assert.Single(config.Pages[1].Sections!).Variant);  // the design's `b`
    }

    [Fact]
    public void MergeKeepsTheStorefrontWidePairAsTheHomePagesSingleSourceOfTruth()
    {
        var config = StorefrontPrototypeAdapter.Import(StorefrontPrototypeAdapter.Parse(DesignJson)!);

        // The import is faithful: it keeps the design's per-page copies.
        Assert.NotNull(config.Pages[0].Hero);
        Assert.NotNull(config.Pages[0].Sections);

        var merged = StorefrontBuilderDefaults.Merge(config);

        // After a save the home page has no copies of its own, so a later template
        // switch cannot leave a stale one behind...
        Assert.Null(merged.Pages[0].Hero);
        Assert.Null(merged.Pages[0].Sections);
        // ...while every other page keeps its own hero and sections.
        Assert.Equal("Services and pricing", merged.Pages[1].Hero!.Heading);
        Assert.NotEmpty(merged.Pages[1].Sections!);
        // And the storefront-wide pair still holds the home page's content.
        Assert.Equal("The art of a good haircut.", merged.Hero.Heading);
        Assert.Equal(2, merged.Sections.Count);
    }

    [Fact]
    public void ExportRoundTripsEveryKeyTheDesignDefines()
    {
        var original = StorefrontPrototypeAdapter.Parse(DesignJson)!;

        var exported = StorefrontPrototypeAdapter.Export(StorefrontPrototypeAdapter.Import(original));

        Assert.Equal(original.Tpl, exported.Tpl);

        Assert.Equal(original.Theme.P, exported.Theme.P);
        Assert.Equal(original.Theme.A, exported.Theme.A);
        Assert.Equal(original.Theme.Bg, exported.Theme.Bg);
        Assert.Equal(original.Theme.Sf, exported.Theme.Sf);
        Assert.Equal(original.Theme.Hf, exported.Theme.Hf);
        Assert.Equal(original.Theme.Bf, exported.Theme.Bf);
        Assert.Equal(original.Theme.R, exported.Theme.R);
        Assert.Equal(original.Theme.Btn, exported.Theme.Btn);
        Assert.Equal(original.Theme.Bt, exported.Theme.Bt);
        Assert.Equal(original.Theme.Mode, exported.Theme.Mode);
        Assert.Equal(original.Theme.Anim, exported.Theme.Anim);
        Assert.Equal(original.Theme.Hover, exported.Theme.Hover);
        Assert.Equal(original.Theme.Sp, exported.Theme.Sp);
        Assert.Equal(original.Theme.Pt, exported.Theme.Pt);

        Assert.Equal(original.Header.Layout, exported.Header.Layout);
        Assert.Equal(original.Header.Sticky, exported.Header.Sticky);
        Assert.Equal(original.Header.Tr, exported.Header.Tr);
        Assert.Equal(original.Header.Hbg, exported.Header.Hbg);
        Assert.Equal(original.Header.Pad, exported.Header.Pad);
        Assert.Equal(original.Header.Drawer, exported.Header.Drawer);
        Assert.Equal(original.Header.Fx, exported.Header.Fx);
        Assert.Equal(original.Header.Ctas, exported.Header.Ctas);
        Assert.Equal(original.Header.Nav.Count, exported.Header.Nav.Count);
        Assert.Equal(original.Header.Nav[1].L, exported.Header.Nav[1].L);
        Assert.Equal(original.Header.Nav[1].Kids[0].L, exported.Header.Nav[1].Kids[0].L);

        Assert.Equal(original.Foot.Layout, exported.Foot.Layout);
        Assert.Equal(original.Foot.Bg, exported.Foot.Bg);
        Assert.Equal(original.Foot.Tx, exported.Foot.Tx);
        Assert.Equal(original.Foot.Desc, exported.Foot.Desc);
        Assert.Equal(original.Foot.Cp, exported.Foot.Cp);
        Assert.Equal(original.Foot.Pad, exported.Foot.Pad);
        Assert.Equal(original.Foot.News, exported.Foot.News);

        Assert.Equal(original.Pages.Count, exported.Pages.Count);
        Assert.Equal(original.Pages[0].Id, exported.Pages[0].Id);
        Assert.Equal(original.Pages[0].Hero!.H, exported.Pages[0].Hero!.H);
        Assert.Equal(original.Pages[0].Hero!.D, exported.Pages[0].Hero!.D);
        Assert.Equal(original.Pages[0].Hero!.L, exported.Pages[0].Hero!.L);
        Assert.Equal(original.Pages[0].Hero!.Ov, exported.Pages[0].Hero!.Ov);
        Assert.Equal(original.Pages[1].Slug, exported.Pages[1].Slug);
        Assert.Equal(original.Pages[1].Secs[0].V, exported.Pages[1].Secs[0].V);

        Assert.Equal(original.Pages[0].Secs[0].K, exported.Pages[0].Secs[0].K);
        Assert.Equal(original.Pages[0].Secs[0].V, exported.Pages[0].Secs[0].V);
        Assert.Equal(original.Pages[0].Secs[0].Pad, exported.Pages[0].Secs[0].Pad);
        Assert.Equal(original.Pages[0].Secs[0].Al, exported.Pages[0].Secs[0].Al);
        Assert.Equal(original.Pages[0].Secs[1].Bg, exported.Pages[0].Secs[1].Bg);
        Assert.Equal(original.Pages[0].Secs[1].Cols, exported.Pages[0].Secs[1].Cols);
        Assert.Equal(original.Pages[0].Secs[1].D.Title, exported.Pages[0].Secs[1].D.Title);
        Assert.Equal(original.Pages[0].Secs[1].D.Items[0][0], exported.Pages[0].Secs[1].D.Items[0][0]);
        Assert.Equal(original.Pages[0].Secs[1].D.Items[1][1], exported.Pages[0].Secs[1].D.Items[1][1]);
    }

    [Fact]
    public void ThemePresetsAreTheDesignsSix()
    {
        Assert.Equal(
            new[] { "warm-studio", "noir-gold", "sage", "navy", "claret", "mono" },
            StorefrontThemePresets.All.Select(preset => preset.Id));

        var mono = StorefrontThemePresets.Find("mono")!;
        Assert.Equal("Mono", mono.Name);
        Assert.Equal("#111111", mono.Primary);
        Assert.Equal("#ff5a36", mono.Accent);
        Assert.Equal("#f4f4f2", mono.Background);

        // Applying a preset moves only the palette the design's preset moves.
        var theme = StorefrontTemplates.ThemeFor("atelier");
        theme.HeadingFont = "Playfair Display";
        theme.Animation = "zoom";

        Assert.True(StorefrontThemePresets.Apply(theme, "mono"));
        Assert.Equal("#111111", theme.Primary);
        Assert.Equal("#f4f4f2", theme.Background);
        Assert.Equal("#111111", theme.ButtonBackground);
        Assert.Equal("Playfair Display", theme.HeadingFont);
        Assert.Equal("zoom", theme.Animation);

        Assert.False(StorefrontThemePresets.Apply(theme, "not-a-preset"));
        Assert.Null(StorefrontThemePresets.Find("not-a-preset"));
    }

    [Fact]
    public void LayoutVocabularyAcceptsTheDesignsNamesAndResolvesTheOlderOnes()
    {
        Assert.Equal("classic", StorefrontLayoutCatalog.NormaliseHeaderLayout("classic"));
        Assert.Equal("pill", StorefrontLayoutCatalog.NormaliseHeaderLayout("pill"));
        Assert.Equal("glass", StorefrontLayoutCatalog.NormaliseHeaderLayout("glass"));
        Assert.Equal("bar", StorefrontLayoutCatalog.NormaliseHeaderLayout("bar"));
        // Earlier names resolve to the closest design layout, never to nothing.
        Assert.Equal("pill", StorefrontLayoutCatalog.NormaliseHeaderLayout("floating"));
        Assert.Equal("classic", StorefrontLayoutCatalog.NormaliseHeaderLayout("solid"));

        Assert.Equal("overlay", StorefrontLayoutCatalog.NormaliseHeroLayout("overlay"));
        Assert.Equal("facts", StorefrontLayoutCatalog.NormaliseHeroLayout("facts"));
        Assert.Equal("collage", StorefrontLayoutCatalog.NormaliseHeroLayout("collage"));
        Assert.Equal("booking", StorefrontLayoutCatalog.NormaliseHeroLayout("booking"));
        Assert.Equal("split-reverse", StorefrontLayoutCatalog.NormaliseHeroLayout("splitr"));
        Assert.Equal("overlay", StorefrontLayoutCatalog.NormaliseHeroLayout("full-image"));

        Assert.Equal("newsletter", StorefrontLayoutCatalog.NormaliseFooterLayout("newsletter"));
        Assert.Equal("wordmark", StorefrontLayoutCatalog.NormaliseFooterLayout("wordmark"));
        Assert.Equal("columns", StorefrontLayoutCatalog.NormaliseFooterLayout("nonsense"));

        // The design's `a`/`b` section layouts map onto production variants.
        Assert.Equal("image-left", StorefrontPrototypeAdapter.SectionVariantToProduction("about", "a"));
        Assert.Equal("image-right", StorefrontPrototypeAdapter.SectionVariantToProduction("about", "b"));
        Assert.Equal("feature-first", StorefrontPrototypeAdapter.SectionVariantToProduction("gallery", "b"));
        Assert.Equal("b", StorefrontPrototypeAdapter.SectionVariantToPrototype("about", "image-right"));
        Assert.Equal("a", StorefrontPrototypeAdapter.SectionVariantToPrototype("about", "image-left"));
    }

    [Fact]
    public void AConfigurationWrittenBeforeTheDesignPortStillMergesAndResolves()
    {
        // The shape production stored before this port: no motion keys, no header
        // nav, and a template id and header variant from the previous generation.
        var legacy = new StorefrontConfigDto
        {
            TemplateId = "motion",
            Header = new StorefrontHeaderDto { Variant = "sticky-compact" },
            Theme = new StorefrontThemeDto { Primary = "#0f172a" },
            Sections =
            [
                new StorefrontSectionDto { Id = "s1", Key = "services", Variant = "cards", Order = 10 },
            ],
        };

        var merged = StorefrontBuilderDefaults.Merge(legacy);

        // The old template id still resolves to a live design template.
        Assert.Equal("fade", StorefrontTemplates.CanonicalId("motion"));
        Assert.Equal("fade", merged.TemplateId);
        // The stored look is untouched; the new keys take their documented defaults.
        Assert.Equal("#0f172a", merged.Theme.Primary);
        Assert.Equal("up", merged.Theme.Animation);
        Assert.Equal(600, merged.Theme.DurationMs);
        Assert.Equal("lift", merged.Theme.HoverEffect);
        Assert.Equal("fade", merged.Theme.PageTransition);
        Assert.Equal("light", merged.Theme.Mode);
        // An empty nav list means "derive the navigation from the pages".
        Assert.Empty(merged.Header.Nav);
        Assert.Equal("sticky-compact", merged.Header.Variant);
        // The section keeps its content and gains the design's defaults.
        var section = Assert.Single(merged.Sections);
        Assert.Equal("cards", section.Variant);
        Assert.Equal(3, section.Columns);
        Assert.Equal("default", section.Background);
        Assert.Equal("normal", section.Padding);
        Assert.Equal("inherit", section.Animation);
    }
}
