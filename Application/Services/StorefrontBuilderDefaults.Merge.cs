using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Services;

public static partial class StorefrontBuilderDefaults
{
    private static void MergeTheme(StorefrontThemeDto target, StorefrontThemeDto? source)
    {
        if (source == null) return;
        target.Primary = Pick(source.Primary, target.Primary);
        target.Secondary = Pick(source.Secondary, target.Secondary);
        target.Accent = Pick(source.Accent, target.Accent);
        target.Background = Pick(source.Background, target.Background);
        target.Surface = Pick(source.Surface, target.Surface);
        target.Text = Pick(source.Text, target.Text);
        target.MutedText = Pick(source.MutedText, target.MutedText);
        target.ButtonBackground = Pick(source.ButtonBackground, target.ButtonBackground);
        target.ButtonText = Pick(source.ButtonText, target.ButtonText);
        target.Border = Pick(source.Border, target.Border);
        target.HeadingFont = Pick(source.HeadingFont, target.HeadingFont);
        target.BodyFont = Pick(source.BodyFont, target.BodyFont);
        target.HeadingWeight = source.HeadingWeight > 0 ? source.HeadingWeight : target.HeadingWeight;
        target.BodyWeight = source.BodyWeight > 0 ? source.BodyWeight : target.BodyWeight;
        target.BaseFontSize = source.BaseFontSize > 0 ? source.BaseFontSize : target.BaseFontSize;
        target.HeadingScale = Pick(source.HeadingScale, target.HeadingScale);
        target.LineHeight = Pick(source.LineHeight, target.LineHeight);
        target.LetterSpacing = Pick(source.LetterSpacing, target.LetterSpacing);
        target.ContentWidth = source.ContentWidth > 0 ? source.ContentWidth : target.ContentWidth;
        target.SectionSpacing = Pick(source.SectionSpacing, target.SectionSpacing);
        target.ContainerPadding = Pick(source.ContainerPadding, target.ContainerPadding);
        target.Radius = Pick(source.Radius, target.Radius);
        target.CardStyle = Pick(source.CardStyle, target.CardStyle);
        target.Shadow = Pick(source.Shadow, target.Shadow);
        target.ImageRatio = Pick(source.ImageRatio, target.ImageRatio);
        target.ButtonStyle = Pick(source.ButtonStyle, target.ButtonStyle);
        target.BackgroundStyle = Pick(source.BackgroundStyle, target.BackgroundStyle);
        target.BackgroundImageMediaId = source.BackgroundImageMediaId ?? target.BackgroundImageMediaId;
        target.GradientFrom = Pick(source.GradientFrom, target.GradientFrom);
        target.GradientTo = Pick(source.GradientTo, target.GradientTo);

        // Motion & presentation. Each value is clamped to its documented range, so a
        // hand-edited payload cannot produce an unusable animation.
        target.RadiusPx = source.RadiusPx ?? target.RadiusPx;
        target.Mode = StorefrontLayoutCatalog.FromList(source.Mode, StorefrontLayoutCatalog.Modes, target.Mode);
        target.Animation = StorefrontLayoutCatalog.FromList(
            source.Animation, StorefrontLayoutCatalog.Animations, target.Animation);
        target.DurationMs = source.DurationMs > 0
            ? Math.Clamp(source.DurationMs, 200, 1500)
            : target.DurationMs;
        target.HoverEffect = StorefrontLayoutCatalog.FromList(
            source.HoverEffect, StorefrontLayoutCatalog.HoverEffects, target.HoverEffect);
        target.PageTransition = StorefrontLayoutCatalog.FromList(
            source.PageTransition, StorefrontLayoutCatalog.PageTransitions, target.PageTransition);
    }

    private static void MergeHeader(StorefrontHeaderDto target, StorefrontHeaderDto? source)
    {
        if (source == null) return;
        target.Variant = Pick(source.Variant, target.Variant);
        target.ShowLogo = source.ShowLogo;
        target.LogoMediaId = source.LogoMediaId ?? target.LogoMediaId;
        target.LogoSize = Pick(source.LogoSize, target.LogoSize);
        target.ShowBusinessName = source.ShowBusinessName;
        target.Sticky = source.Sticky;
        target.Transparent = source.Transparent;
        target.Background = Pick(source.Background, target.Background);
        target.TextColor = Pick(source.TextColor, target.TextColor);
        target.Alignment = Pick(source.Alignment, target.Alignment);
        target.Spacing = Pick(source.Spacing, target.Spacing);
        target.MobileStyle = Pick(source.MobileStyle, target.MobileStyle);
        target.CtaEnabled = source.CtaEnabled;
        target.CtaLabel = Pick(source.CtaLabel, target.CtaLabel);
        target.CtaType = Pick(source.CtaType, target.CtaType);

        // Independent navigation and the design's header controls.
        target.Nav = source.Nav ?? target.Nav;
        target.Drawer = StorefrontLayoutCatalog.FromList(
            source.Drawer, StorefrontLayoutCatalog.DrawerStyles, target.Drawer);
        target.LinkEffect = Pick(source.LinkEffect, target.LinkEffect);
        target.CtaStyle = Pick(source.CtaStyle, target.CtaStyle);
        target.UppercaseLinks = source.UppercaseLinks;
        target.Height = source.Height > 0 ? Math.Clamp(source.Height, 6, 40) : target.Height;
        target.Shadow = source.Shadow;
        target.Border = source.Border;
        target.Announcement = source.Announcement ?? target.Announcement;
        target.UtilityLeft = source.UtilityLeft ?? target.UtilityLeft;
        target.UtilityRight = source.UtilityRight ?? target.UtilityRight;
        target.ShowContactInDrawer = source.ShowContactInDrawer;
    }

    private static void MergeHero(StorefrontHeroDto target, StorefrontHeroDto? source)
    {
        if (source == null) return;
        target.Variant = Pick(source.Variant, target.Variant);
        target.Enabled = source.Enabled;
        target.Eyebrow = source.Eyebrow ?? target.Eyebrow;
        target.Heading = source.Heading ?? target.Heading;
        target.Subheading = source.Subheading ?? target.Subheading;
        target.Description = source.Description ?? target.Description;
        target.PrimaryCtaLabel = Pick(source.PrimaryCtaLabel, target.PrimaryCtaLabel);
        target.PrimaryCtaType = Pick(source.PrimaryCtaType, target.PrimaryCtaType);
        target.SecondaryCtaLabel = Pick(source.SecondaryCtaLabel, target.SecondaryCtaLabel);
        target.SecondaryCtaType = Pick(source.SecondaryCtaType, target.SecondaryCtaType);
        target.BackgroundMediaId = source.BackgroundMediaId ?? target.BackgroundMediaId;
        target.CarouselMediaIds = source.CarouselMediaIds ?? target.CarouselMediaIds;
        target.VideoUrl = source.VideoUrl ?? target.VideoUrl;
        target.OverlayOpacity = source.OverlayOpacity;
        target.BackgroundColor = Pick(source.BackgroundColor, target.BackgroundColor);
        target.TextAlign = Pick(source.TextAlign, target.TextAlign);
        target.Height = Pick(source.Height, target.Height);
        target.ContentWidth = Pick(source.ContentWidth, target.ContentWidth);
        target.ImagePosition = Pick(source.ImagePosition, target.ImagePosition);
        target.MobileLayout = Pick(source.MobileLayout, target.MobileLayout);
        target.ShowLogo = source.ShowLogo;
        target.ShowFacts = source.ShowFacts;
    }

    private static void MergeFooter(StorefrontFooterDto target, StorefrontFooterDto? source)
    {
        if (source == null) return;
        target.Variant = Pick(source.Variant, target.Variant);
        target.ShowLogo = source.ShowLogo;
        target.LogoMediaId = source.LogoMediaId ?? target.LogoMediaId;
        target.Description = source.Description ?? target.Description;
        target.ShowContact = source.ShowContact;
        target.ShowAddress = source.ShowAddress;
        target.ShowHours = source.ShowHours;
        target.ShowSocial = source.ShowSocial;
        target.Socials = source.Socials ?? target.Socials;
        target.ShowNav = source.ShowNav;
        target.ShowLegal = source.ShowLegal;
        target.LegalLinks = source.LegalLinks ?? target.LegalLinks;
        target.Copyright = source.Copyright ?? target.Copyright;
        target.BackgroundColor = Pick(source.BackgroundColor, target.BackgroundColor);
        target.TextColor = Pick(source.TextColor, target.TextColor);

        // The design's footer controls.
        target.Padding = source.Padding > 0 ? Math.Clamp(source.Padding, 24, 120) : target.Padding;
        target.NewsletterHeadline = source.NewsletterHeadline ?? target.NewsletterHeadline;
        target.NewsletterPlaceholder = Pick(source.NewsletterPlaceholder, target.NewsletterPlaceholder);
        target.NewsletterButtonLabel = Pick(source.NewsletterButtonLabel, target.NewsletterButtonLabel);
        target.ShowWordmark = source.ShowWordmark;
    }

    private static string Pick(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value!;
}
