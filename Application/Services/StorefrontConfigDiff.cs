using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Services;

/// <summary>
/// Computes a structured, human-readable difference between two storefront
/// configurations.
///
/// <para>Why not a JSON text diff: the owner reads these sentences. A raw diff of
/// a 40 KB payload is unusable and hides intent behind property names. This
/// engine walks the configuration domain by domain, matches sections and pages by
/// their stable ids (falling back to section key / page slug when an id is
/// missing), and reports each meaningful change as a labelled sentence with an
/// optional before/after detail.</para>
/// </summary>
public static class StorefrontConfigDiff
{
    public static List<StorefrontChangeResponse> Compare(StorefrontConfigDto? from, StorefrontConfigDto? to)
    {
        var changes = new List<StorefrontChangeResponse>();
        var a = from ?? new StorefrontConfigDto();
        var b = to ?? new StorefrontConfigDto();

        if (!string.Equals(a.TemplateId, b.TemplateId, StringComparison.OrdinalIgnoreCase))
            Add(changes, "Template", "modified", "Template changed", Detail(a.TemplateId, b.TemplateId));

        CompareTheme(a.Theme, b.Theme, changes);
        CompareHeader(a.Header, b.Header, changes);
        CompareHero(a.Hero, b.Hero, changes);
        CompareSections(a.Sections, b.Sections, changes);
        ComparePages(a.Pages, b.Pages, changes);
        CompareFooter(a.Footer, b.Footer, changes);
        CompareSeo(a.Seo, b.Seo, changes);

        return changes;
    }

    // ── theme + typography ──────────────────────────────────────────────────────

    private static void CompareTheme(StorefrontThemeDto? from, StorefrontThemeDto? to, List<StorefrontChangeResponse> changes)
    {
        var a = from ?? new StorefrontThemeDto();
        var b = to ?? new StorefrontThemeDto();

        Colour(changes, "Primary brand colour", a.Primary, b.Primary);
        Colour(changes, "Secondary colour", a.Secondary, b.Secondary);
        Colour(changes, "Accent colour", a.Accent, b.Accent);
        Colour(changes, "Background colour", a.Background, b.Background);
        Colour(changes, "Surface colour", a.Surface, b.Surface);
        Colour(changes, "Text colour", a.Text, b.Text);
        Colour(changes, "Muted text colour", a.MutedText, b.MutedText);
        Colour(changes, "Button colour", a.ButtonBackground, b.ButtonBackground);
        Colour(changes, "Button text colour", a.ButtonText, b.ButtonText);
        Colour(changes, "Border colour", a.Border, b.Border);

        Text(changes, "Typography", "Heading font", a.HeadingFont, b.HeadingFont);
        Text(changes, "Typography", "Body font", a.BodyFont, b.BodyFont);
        Number(changes, "Typography", "Heading weight", a.HeadingWeight, b.HeadingWeight);
        Number(changes, "Typography", "Body weight", a.BodyWeight, b.BodyWeight);
        Number(changes, "Typography", "Base font size", a.BaseFontSize, b.BaseFontSize);
        Text(changes, "Typography", "Heading scale", a.HeadingScale, b.HeadingScale);
        Text(changes, "Typography", "Line height", a.LineHeight, b.LineHeight);
        Text(changes, "Typography", "Letter spacing", a.LetterSpacing, b.LetterSpacing);

        Number(changes, "Theme", "Content width", a.ContentWidth, b.ContentWidth);
        Text(changes, "Theme", "Section spacing", a.SectionSpacing, b.SectionSpacing);
        Text(changes, "Theme", "Container padding", a.ContainerPadding, b.ContainerPadding);
        Text(changes, "Theme", "Corner radius", a.Radius, b.Radius);
        Text(changes, "Theme", "Card style", a.CardStyle, b.CardStyle);
        Text(changes, "Theme", "Shadow", a.Shadow, b.Shadow);
        Text(changes, "Theme", "Image ratio", a.ImageRatio, b.ImageRatio);
        Text(changes, "Theme", "Button style", a.ButtonStyle, b.ButtonStyle);
        Text(changes, "Theme", "Background style", a.BackgroundStyle, b.BackgroundStyle);
        Media(changes, "Background image", a.BackgroundImageMediaId, b.BackgroundImageMediaId);
        Colour(changes, "Gradient start", a.GradientFrom, b.GradientFrom);
        Colour(changes, "Gradient end", a.GradientTo, b.GradientTo);
    }

    // ── header / navigation ─────────────────────────────────────────────────────

    private static void CompareHeader(StorefrontHeaderDto? from, StorefrontHeaderDto? to, List<StorefrontChangeResponse> changes)
    {
        var a = from ?? new StorefrontHeaderDto();
        var b = to ?? new StorefrontHeaderDto();

        Text(changes, "Header", "Header layout", a.Variant, b.Variant);
        Text(changes, "Header", "Header alignment", a.Alignment, b.Alignment);
        Text(changes, "Header", "Header spacing", a.Spacing, b.Spacing);
        Text(changes, "Header", "Mobile menu style", a.MobileStyle, b.MobileStyle);
        Text(changes, "Header", "Header logo size", a.LogoSize, b.LogoSize);
        Flag(changes, "Header", "Sticky header", a.Sticky, b.Sticky);
        Flag(changes, "Header", "Transparent header", a.Transparent, b.Transparent);
        Flag(changes, "Header", "Header logo", a.ShowLogo, b.ShowLogo);
        Flag(changes, "Header", "Business name in header", a.ShowBusinessName, b.ShowBusinessName);
        Flag(changes, "Header", "Header button", a.CtaEnabled, b.CtaEnabled);
        Text(changes, "Header", "Header button label", a.CtaLabel, b.CtaLabel);
        Text(changes, "Header", "Header button link", a.CtaType, b.CtaType);
        Colour(changes, "Header background", a.Background, b.Background);
        Colour(changes, "Header text colour", a.TextColor, b.TextColor);
        Media(changes, "Header logo image", a.LogoMediaId, b.LogoMediaId);
    }

    // ── hero ────────────────────────────────────────────────────────────────────

    private static void CompareHero(StorefrontHeroDto? from, StorefrontHeroDto? to, List<StorefrontChangeResponse> changes)
    {
        var a = from ?? new StorefrontHeroDto();
        var b = to ?? new StorefrontHeroDto();

        Flag(changes, "Hero", "Hero", a.Enabled, b.Enabled);
        if (!a.Enabled && !b.Enabled) return;

        Text(changes, "Hero", "Hero layout", a.Variant, b.Variant);
        Text(changes, "Hero", "Hero eyebrow", a.Eyebrow, b.Eyebrow);
        Text(changes, "Hero", "Hero headline", a.Heading, b.Heading);
        Text(changes, "Hero", "Hero subheading", a.Subheading, b.Subheading);
        Text(changes, "Hero", "Hero description", a.Description, b.Description);
        Text(changes, "Hero", "Hero primary button", a.PrimaryCtaLabel, b.PrimaryCtaLabel);
        Text(changes, "Hero", "Hero primary button link", a.PrimaryCtaType, b.PrimaryCtaType);
        Text(changes, "Hero", "Hero secondary button", a.SecondaryCtaLabel, b.SecondaryCtaLabel);
        Text(changes, "Hero", "Hero secondary button link", a.SecondaryCtaType, b.SecondaryCtaType);
        Text(changes, "Hero", "Hero height", a.Height, b.Height);
        Text(changes, "Hero", "Hero text alignment", a.TextAlign, b.TextAlign);
        Text(changes, "Hero", "Hero content width", a.ContentWidth, b.ContentWidth);
        Text(changes, "Hero", "Hero image position", a.ImagePosition, b.ImagePosition);
        Text(changes, "Hero", "Hero mobile layout", a.MobileLayout, b.MobileLayout);
        Number(changes, "Hero", "Hero overlay strength", a.OverlayOpacity, b.OverlayOpacity);
        Colour(changes, "Hero background", a.BackgroundColor, b.BackgroundColor);
        Flag(changes, "Hero", "Hero logo", a.ShowLogo, b.ShowLogo);
        Flag(changes, "Hero", "Hero business facts", a.ShowFacts, b.ShowFacts);
        Media(changes, "Hero background image", a.BackgroundMediaId, b.BackgroundMediaId);
        Text(changes, "Hero", "Hero video", a.VideoUrl, b.VideoUrl);

        var fromSlides = a.CarouselMediaIds ?? new List<Guid>();
        var toSlides = b.CarouselMediaIds ?? new List<Guid>();
        if (!fromSlides.SequenceEqual(toSlides))
            Add(changes, "Media", "modified", "Hero carousel images changed",
                $"from {fromSlides.Count} image(s) to {toSlides.Count} image(s)");
    }

    // ── sections ────────────────────────────────────────────────────────────────

    private static void CompareSections(List<StorefrontSectionDto>? fromList, List<StorefrontSectionDto>? toList, List<StorefrontChangeResponse> changes)
    {
        var from = (fromList ?? new List<StorefrontSectionDto>()).Where(s => s != null).OrderBy(s => s.Order).ToList();
        var to = (toList ?? new List<StorefrontSectionDto>()).Where(s => s != null).OrderBy(s => s.Order).ToList();

        var matched = new List<(StorefrontSectionDto F, StorefrontSectionDto T)>();
        var usedTo = new bool[to.Count];

        // 1. Stable identity: the section's own id.
        foreach (var source in from)
        {
            for (var j = 0; j < to.Count; j++)
            {
                if (usedTo[j]) continue;
                if (!string.Equals(to[j].Id, source.Id, StringComparison.Ordinal)) continue;
                matched.Add((source, to[j]));
                usedTo[j] = true;
                break;
            }
        }

        // 2. Fallback: the same key in order. An id can change when a section is
        //    re-created by a template switch, but the type is the same intent.
        foreach (var source in from)
        {
            if (matched.Any(m => ReferenceEquals(m.F, source))) continue;
            for (var j = 0; j < to.Count; j++)
            {
                if (usedTo[j]) continue;
                if (!string.Equals(to[j].Key, source.Key, StringComparison.OrdinalIgnoreCase)) continue;
                matched.Add((source, to[j]));
                usedTo[j] = true;
                break;
            }
        }

        for (var j = 0; j < to.Count; j++)
        {
            if (usedTo[j]) continue;
            Add(changes, "Sections", "added", $"{SectionLabel(to[j].Key)} section added", null, to[j].Id);
        }

        foreach (var source in from)
        {
            if (matched.Any(m => ReferenceEquals(m.F, source))) continue;
            Add(changes, "Sections", "removed", $"{SectionLabel(source.Key)} section removed", null, source.Id);
        }

        foreach (var (f, t) in matched)
        {
            var label = SectionLabel(t.Key);
            if (!string.Equals(f.Variant, t.Variant, StringComparison.OrdinalIgnoreCase))
                Add(changes, "Sections", "modified", $"{label} layout changed", Detail(f.Variant, t.Variant), t.Id);
            if (!string.Equals(f.Title, t.Title, StringComparison.Ordinal))
                Add(changes, "Sections", "modified", $"{label} heading changed", Detail(f.Title, t.Title), t.Id);
            if (!string.Equals(f.Description, t.Description, StringComparison.Ordinal))
                Add(changes, "Sections", "modified", $"{label} description changed", Detail(f.Description, t.Description), t.Id);
            if (f.Enabled != t.Enabled)
                Add(changes, "Sections", "modified", $"{label} {(t.Enabled ? "shown" : "hidden")}", null, t.Id);

            var fromSettings = f.Settings ?? new Dictionary<string, string>();
            var toSettings = t.Settings ?? new Dictionary<string, string>();
            foreach (var key in fromSettings.Keys.Union(toSettings.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                fromSettings.TryGetValue(key, out var before);
                toSettings.TryGetValue(key, out var after);
                if (string.Equals(before, after, StringComparison.Ordinal)) continue;
                Add(changes, "Sections", "modified", $"{label} {SettingLabel(key)} changed", Detail(before, after), t.Id);
            }
        }

        // Reordering: the matched sections' relative sequence differs.
        var orderInTo = new List<int>();
        foreach (var target in to)
        {
            var index = matched.FindIndex(m => ReferenceEquals(m.T, target));
            if (index >= 0) orderInTo.Add(index);
        }

        if (!orderInTo.SequenceEqual(Enumerable.Range(0, matched.Count)))
        {
            for (var position = 0; position < orderInTo.Count; position++)
            {
                if (orderInTo[position] == position) continue;
                var moved = matched[orderInTo[position]];
                Add(changes, "Sections", "reordered", "Section order changed",
                    $"“{SectionLabel(moved.T.Key)}” moved from position {orderInTo[position] + 1} to {position + 1}",
                    moved.T.Id);
                break;
            }
        }
    }

    // ── pages ───────────────────────────────────────────────────────────────────

    private static void ComparePages(List<StorefrontPageDto>? fromList, List<StorefrontPageDto>? toList, List<StorefrontChangeResponse> changes)
    {
        var from = (fromList ?? new List<StorefrontPageDto>()).Where(p => p != null).ToList();
        var to = (toList ?? new List<StorefrontPageDto>()).Where(p => p != null).ToList();

        var matched = new List<(StorefrontPageDto F, StorefrontPageDto T)>();
        var usedTo = new bool[to.Count];

        foreach (var source in from)
        {
            for (var j = 0; j < to.Count; j++)
            {
                if (usedTo[j]) continue;
                if (!string.Equals(to[j].Id, source.Id, StringComparison.Ordinal)) continue;
                matched.Add((source, to[j]));
                usedTo[j] = true;
                break;
            }
        }

        // Fallback: a page whose id changed but whose slug is the same URL.
        foreach (var source in from)
        {
            if (matched.Any(m => ReferenceEquals(m.F, source))) continue;
            for (var j = 0; j < to.Count; j++)
            {
                if (usedTo[j]) continue;
                if (!string.Equals(to[j].Slug, source.Slug, StringComparison.OrdinalIgnoreCase)) continue;
                matched.Add((source, to[j]));
                usedTo[j] = true;
                break;
            }
        }

        for (var j = 0; j < to.Count; j++)
        {
            if (usedTo[j]) continue;
            Add(changes, "Pages", "added", $"Page “{PageTitle(to[j])}” added", null, to[j].Id);
        }

        foreach (var source in from)
        {
            if (matched.Any(m => ReferenceEquals(m.F, source))) continue;
            Add(changes, "Pages", "removed", $"Page “{PageTitle(source)}” removed", null, source.Id);
        }

        foreach (var (f, t) in matched)
        {
            var name = PageTitle(t);
            if (!string.Equals(f.Title, t.Title, StringComparison.Ordinal))
                Add(changes, "Pages", "modified", $"Page “{name}” title changed", Detail(f.Title, t.Title), t.Id);
            if (!string.Equals(f.Slug, t.Slug, StringComparison.OrdinalIgnoreCase))
                Add(changes, "Pages", "modified", $"Page “{name}” URL changed", Detail(f.Slug, t.Slug), t.Id);
            if (!string.Equals(f.Kind, t.Kind, StringComparison.OrdinalIgnoreCase))
                Add(changes, "Pages", "modified", $"Page “{name}” type changed", Detail(f.Kind, t.Kind), t.Id);
            if (f.ShowInNav != t.ShowInNav)
                Add(changes, "Pages", "modified",
                    $"Page “{name}” {(t.ShowInNav ? "added to" : "removed from")} the navigation", null, t.Id);
            if (!string.Equals(f.SeoTitle, t.SeoTitle, StringComparison.Ordinal))
                Add(changes, "SEO", "modified", $"SEO title for “{name}” changed", Detail(f.SeoTitle, t.SeoTitle), t.Id);
            if (!string.Equals(f.SeoDescription, t.SeoDescription, StringComparison.Ordinal))
                Add(changes, "SEO", "modified", $"Meta description for “{name}” changed", Detail(f.SeoDescription, t.SeoDescription), t.Id);
        }

        var orderInTo = new List<int>();
        foreach (var target in to)
        {
            var index = matched.FindIndex(m => ReferenceEquals(m.T, target));
            if (index >= 0) orderInTo.Add(index);
        }

        if (!orderInTo.SequenceEqual(Enumerable.Range(0, matched.Count)))
            Add(changes, "Pages", "reordered", "Page order changed",
                "the running order of your pages changed");
    }

    // ── footer ──────────────────────────────────────────────────────────────────

    private static void CompareFooter(StorefrontFooterDto? from, StorefrontFooterDto? to, List<StorefrontChangeResponse> changes)
    {
        var a = from ?? new StorefrontFooterDto();
        var b = to ?? new StorefrontFooterDto();

        Text(changes, "Footer", "Footer layout", a.Variant, b.Variant);
        Text(changes, "Footer", "Footer description", a.Description, b.Description);
        Text(changes, "Footer", "Footer copyright", a.Copyright, b.Copyright);
        Flag(changes, "Footer", "Footer logo", a.ShowLogo, b.ShowLogo);
        Flag(changes, "Footer", "Footer contact details", a.ShowContact, b.ShowContact);
        Flag(changes, "Footer", "Footer address", a.ShowAddress, b.ShowAddress);
        Flag(changes, "Footer", "Footer opening hours", a.ShowHours, b.ShowHours);
        Flag(changes, "Footer", "Footer social links", a.ShowSocial, b.ShowSocial);
        Flag(changes, "Footer", "Footer navigation", a.ShowNav, b.ShowNav);
        Flag(changes, "Footer", "Footer legal links", a.ShowLegal, b.ShowLegal);
        Colour(changes, "Footer background", a.BackgroundColor, b.BackgroundColor);
        Colour(changes, "Footer text colour", a.TextColor, b.TextColor);
        Media(changes, "Footer logo image", a.LogoMediaId, b.LogoMediaId);

        var fromSocials = a.Socials ?? new List<StorefrontSocialLinkDto>();
        var toSocials = b.Socials ?? new List<StorefrontSocialLinkDto>();
        if (!fromSocials.Select(s => $"{s.Platform}|{s.Url}").SequenceEqual(toSocials.Select(s => $"{s.Platform}|{s.Url}")))
            Add(changes, "Footer", "modified", "Footer social links changed",
                $"from {fromSocials.Count} link(s) to {toSocials.Count} link(s)");

        var fromLegal = a.LegalLinks ?? new List<StorefrontNavLinkDto>();
        var toLegal = b.LegalLinks ?? new List<StorefrontNavLinkDto>();
        if (!fromLegal.Select(l => $"{l.Label}|{l.Href}").SequenceEqual(toLegal.Select(l => $"{l.Label}|{l.Href}")))
            Add(changes, "Footer", "modified", "Footer legal links changed",
                $"from {fromLegal.Count} link(s) to {toLegal.Count} link(s)");
    }

    // ── SEO ─────────────────────────────────────────────────────────────────────

    private static void CompareSeo(StorefrontSeoDto? from, StorefrontSeoDto? to, List<StorefrontChangeResponse> changes)
    {
        var a = from ?? new StorefrontSeoDto();
        var b = to ?? new StorefrontSeoDto();

        Text(changes, "SEO", "SEO title", a.Title, b.Title);
        Text(changes, "SEO", "Meta description", a.Description, b.Description);
        Text(changes, "SEO", "Social share title", a.OgTitle, b.OgTitle);
        Text(changes, "SEO", "Social share description", a.OgDescription, b.OgDescription);
        Text(changes, "SEO", "Keywords", a.Keywords, b.Keywords);
        Media(changes, "Social share image", a.OgImageMediaId, b.OgImageMediaId);
        Media(changes, "Favicon", a.FaviconMediaId, b.FaviconMediaId);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static void Add(
        List<StorefrontChangeResponse> changes,
        string category,
        string kind,
        string label,
        string? detail = null,
        string? targetId = null)
    {
        changes.Add(new StorefrontChangeResponse
        {
            Category = category,
            Kind = kind,
            Label = label,
            Detail = detail,
            TargetId = targetId,
        });
    }

    private static void Colour(List<StorefrontChangeResponse> changes, string label, string? from, string? to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
        Add(changes, "Theme", "modified", $"{label} changed", Detail(from, to));
    }

    private static void Text(List<StorefrontChangeResponse> changes, string category, string label, string? from, string? to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal)) return;
        Add(changes, category, "modified", $"{label} changed", Detail(from, to));
    }

    private static void Number(List<StorefrontChangeResponse> changes, string category, string label, int from, int to)
    {
        if (from == to) return;
        Add(changes, category, "modified", $"{label} changed", $"from {from} to {to}");
    }

    private static void Flag(List<StorefrontChangeResponse> changes, string category, string label, bool from, bool to)
    {
        if (from == to) return;
        Add(changes, category, "modified", $"{label} {(to ? "enabled" : "disabled")}",
            $"was {(from ? "on" : "off")}");
    }

    private static void Media(List<StorefrontChangeResponse> changes, string label, Guid? from, Guid? to)
    {
        if (from == to) return;
        var detail = from == null
            ? "an image was added"
            : to == null
                ? "the image was removed"
                : "a different image was selected";
        Add(changes, "Media", "modified", $"{label} changed", detail);
    }

    /// <summary>The owner-facing name of a section type ("Gallery", "About"…).</summary>
    private static string SectionLabel(string? key) =>
        StorefrontSectionCatalog.Find(key ?? string.Empty)?.Label ?? Prettify(key);

    private static string PageTitle(StorefrontPageDto page) =>
        string.IsNullOrWhiteSpace(page.Title) ? page.Id : page.Title!;

    /// <summary>Friendly names for the free-form section settings keys.</summary>
    private static readonly Dictionary<string, string> SettingLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["body"] = "text",
        ["mission"] = "mission",
        ["ctaLabel"] = "button label",
        ["note"] = "note",
        ["items"] = "items",
        ["imageUrl"] = "image",
        ["html"] = "embed code",
        ["message"] = "message",
    };

    private static string SettingLabel(string key) =>
        SettingLabels.TryGetValue(key, out var label) ? label : Prettify(key);

    private static string Prettify(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "value";
        var spaced = System.Text.RegularExpressions.Regex.Replace(key, "(?<=[a-z0-9])([A-Z])", " $1");
        return spaced.Replace('_', ' ').ToLowerInvariant();
    }

    private static string? Detail(string? from, string? to) => $"from {Show(from)} to {Show(to)}";

    private static string Show(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "empty" : $"“{Truncate(value)}”";

    private static string Truncate(string value, int max = 48) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
