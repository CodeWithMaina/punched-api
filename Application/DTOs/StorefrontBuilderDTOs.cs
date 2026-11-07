// ═══════════════════════════════════════════════════════════════
//  STOREFRONT BUILDER DTOs
//  The rich, section-based storefront configuration. Presentation only —
//  domain data (services, staff, bookings, reviews, gallery) stays in its
//  owning module and is fetched by the public renderer at read time.
//
//  Stored as JSON on business_landing_page_configs (draft + published), and
//  projected to the public storefront as `PublicStorefrontDto`.
// ═══════════════════════════════════════════════════════════════

using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

/// <summary>Global design tokens applied consistently across the storefront.</summary>
public class StorefrontThemeDto
{
    [JsonPropertyName("primary")] public string Primary { get; set; } = "#0d3a26";
    [JsonPropertyName("secondary")] public string Secondary { get; set; } = "#1f5c3f";
    [JsonPropertyName("accent")] public string Accent { get; set; } = "#cbb27a";
    [JsonPropertyName("background")] public string Background { get; set; } = "#ffffff";
    [JsonPropertyName("surface")] public string Surface { get; set; } = "#f7f5f1";
    [JsonPropertyName("text")] public string Text { get; set; } = "#1c1b19";
    [JsonPropertyName("mutedText")] public string MutedText { get; set; } = "#6b6660";
    [JsonPropertyName("buttonBackground")] public string ButtonBackground { get; set; } = "#0d3a26";
    [JsonPropertyName("buttonText")] public string ButtonText { get; set; } = "#ffffff";
    [JsonPropertyName("border")] public string Border { get; set; } = "#e6e1d8";

    [JsonPropertyName("headingFont")] public string HeadingFont { get; set; } = "Newsreader";
    [JsonPropertyName("bodyFont")] public string BodyFont { get; set; } = "Figtree";
    [JsonPropertyName("headingWeight")] public int HeadingWeight { get; set; } = 500;
    [JsonPropertyName("bodyWeight")] public int BodyWeight { get; set; } = 400;
    [JsonPropertyName("baseFontSize")] public int BaseFontSize { get; set; } = 16;
    [JsonPropertyName("headingScale")] public string HeadingScale { get; set; } = "balanced";
    [JsonPropertyName("lineHeight")] public string LineHeight { get; set; } = "normal";
    [JsonPropertyName("letterSpacing")] public string LetterSpacing { get; set; } = "normal";

    [JsonPropertyName("contentWidth")] public int ContentWidth { get; set; } = 1180;
    [JsonPropertyName("sectionSpacing")] public string SectionSpacing { get; set; } = "comfortable";
    [JsonPropertyName("containerPadding")] public string ContainerPadding { get; set; } = "normal";
    [JsonPropertyName("radius")] public string Radius { get; set; } = "soft";

    /// <summary>
    /// The corner radius in pixels when it came from the design's numeric control
    /// (0–28). <see cref="Radius"/> stays the token the renderer uses; this keeps the
    /// exact number so importing and re-exporting a design is lossless.
    /// </summary>
    [JsonPropertyName("radiusPx")] public int? RadiusPx { get; set; }
    [JsonPropertyName("cardStyle")] public string CardStyle { get; set; } = "soft";
    [JsonPropertyName("shadow")] public string Shadow { get; set; } = "soft";
    [JsonPropertyName("imageRatio")] public string ImageRatio { get; set; } = "4:5";

    /// <summary>`rounded` | `pill` | `square` | `outline` | `filled`.</summary>
    [JsonPropertyName("buttonStyle")] public string ButtonStyle { get; set; } = "rounded";

    /// <summary>`solid` | `gradient` | `image`.</summary>
    [JsonPropertyName("backgroundStyle")] public string BackgroundStyle { get; set; } = "solid";
    [JsonPropertyName("backgroundImageMediaId")] public Guid? BackgroundImageMediaId { get; set; }
    [JsonPropertyName("gradientFrom")] public string GradientFrom { get; set; } = "#0d3a26";
    [JsonPropertyName("gradientTo")] public string GradientTo { get; set; } = "#1f5c3f";

    // ── Motion & presentation ───────────────────────────────────────────────────
    // The prototype's Theme tab exposes these next to the colours/fonts; they are
    // additive so a stored configuration written before they existed resolves to
    // the documented defaults (see StorefrontBuilderDefaults.Merge).

    /// <summary>`light` | `dark` — the storefront's own presentation mode.</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "light";

    /// <summary>Default section entrance: none | fade | up | left | zoom | blur.</summary>
    [JsonPropertyName("animation")] public string Animation { get; set; } = "up";

    /// <summary>Entrance duration in milliseconds (200–1500).</summary>
    [JsonPropertyName("durationMs")] public int DurationMs { get; set; } = 600;

    /// <summary>Card/button hover: none | lift | glow | zoom.</summary>
    [JsonPropertyName("hoverEffect")] public string HoverEffect { get; set; } = "lift";

    /// <summary>Page transition: none | fade | slide.</summary>
    [JsonPropertyName("pageTransition")] public string PageTransition { get; set; } = "fade";
}

/// <summary>
/// One header navigation entry. The prototype lets an owner arrange the nav
/// independently of the page list, with dropdown children and external targets;
/// the richer production model keeps pages as the source of truth, so when this
/// list is empty the header falls back to the pages flagged `showInNav`.
/// </summary>
public class StorefrontNavItemDto
{
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    /// <summary>A page id, or `external` when <see cref="Url"/> is the target.</summary>
    [JsonPropertyName("destination")] public string Destination { get; set; } = "home";
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("children")] public List<StorefrontNavItemDto> Children { get; set; } = new();
}

/// <summary>Header/navigation configuration.</summary>
public class StorefrontHeaderDto
{
    /// <summary>minimal | solid | centered | split | floating | sticky-compact | mobile-first</summary>
    [JsonPropertyName("variant")] public string Variant { get; set; } = "minimal";
    [JsonPropertyName("showLogo")] public bool ShowLogo { get; set; } = true;
    [JsonPropertyName("logoMediaId")] public Guid? LogoMediaId { get; set; }
    [JsonPropertyName("logoSize")] public string LogoSize { get; set; } = "medium";
    [JsonPropertyName("showBusinessName")] public bool ShowBusinessName { get; set; } = true;
    [JsonPropertyName("sticky")] public bool Sticky { get; set; } = true;
    [JsonPropertyName("transparent")] public bool Transparent { get; set; } = true;
    [JsonPropertyName("background")] public string Background { get; set; } = "#ffffff";
    [JsonPropertyName("textColor")] public string TextColor { get; set; } = "#1c1b19";
    /// <summary>left | center | right</summary>
    [JsonPropertyName("alignment")] public string Alignment { get; set; } = "left";
    /// <summary>compact | normal | relaxed</summary>
    [JsonPropertyName("spacing")] public string Spacing { get; set; } = "normal";
    /// <summary>drawer | fullscreen</summary>
    [JsonPropertyName("mobileStyle")] public string MobileStyle { get; set; } = "drawer";
    [JsonPropertyName("ctaEnabled")] public bool CtaEnabled { get; set; } = true;
    [JsonPropertyName("ctaLabel")] public string CtaLabel { get; set; } = "Book now";
    /// <summary>book | services | contact | reviews | directions</summary>
    [JsonPropertyName("ctaType")] public string CtaType { get; set; } = "book";

    // ── Prototype header controls ───────────────────────────────────────────────
    /// <summary>Independent header navigation. Empty = derive from the page list.</summary>
    [JsonPropertyName("nav")] public List<StorefrontNavItemDto> Nav { get; set; } = new();
    /// <summary>Mobile menu presentation: left | right | top | full.</summary>
    [JsonPropertyName("drawer")] public string Drawer { get; set; } = "left";
    /// <summary>Link hover treatment: underline | pill | dot | fade.</summary>
    [JsonPropertyName("linkEffect")] public string LinkEffect { get; set; } = "underline";
    /// <summary>CTA treatment: solid | outline | soft | dark.</summary>
    [JsonPropertyName("ctaStyle")] public string CtaStyle { get; set; } = "solid";
    [JsonPropertyName("uppercaseLinks")] public bool UppercaseLinks { get; set; }
    /// <summary>Bar height in pixels (6–40).</summary>
    [JsonPropertyName("height")] public int Height { get; set; } = 18;
    [JsonPropertyName("shadow")] public bool Shadow { get; set; }
    [JsonPropertyName("border")] public bool Border { get; set; }
    /// <summary>Message for the `bar` layout.</summary>
    [JsonPropertyName("announcement")] public string? Announcement { get; set; }
    /// <summary>Left cell for the `utility` layout.</summary>
    [JsonPropertyName("utilityLeft")] public string? UtilityLeft { get; set; }
    /// <summary>Right cell for the `utility` layout.</summary>
    [JsonPropertyName("utilityRight")] public string? UtilityRight { get; set; }
    /// <summary>Show the CTA and contact details inside the mobile drawer.</summary>
    [JsonPropertyName("showContactInDrawer")] public bool ShowContactInDrawer { get; set; } = true;
}

/// <summary>Hero section configuration (the storefront's opening statement).</summary>
public class StorefrontHeroDto
{
    /// <summary>full-image | split | centered | video | carousel | editorial | minimal | premium | gradient | overlay | split-reverse | facts | collage | booking</summary>
    [JsonPropertyName("variant")] public string Variant { get; set; } = "full-image";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    /// <summary>Entrance animation override: inherit | none | fade | up | left | zoom | blur.</summary>
    [JsonPropertyName("animation")] public string Animation { get; set; } = "inherit";
    [JsonPropertyName("eyebrow")] public string? Eyebrow { get; set; }
    [JsonPropertyName("heading")] public string? Heading { get; set; }
    [JsonPropertyName("subheading")] public string? Subheading { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("primaryCtaLabel")] public string PrimaryCtaLabel { get; set; } = "Book an appointment";
    [JsonPropertyName("primaryCtaType")] public string PrimaryCtaType { get; set; } = "book";
    [JsonPropertyName("secondaryCtaLabel")] public string SecondaryCtaLabel { get; set; } = "View services";
    [JsonPropertyName("secondaryCtaType")] public string SecondaryCtaType { get; set; } = "services";
    [JsonPropertyName("backgroundMediaId")] public Guid? BackgroundMediaId { get; set; }
    [JsonPropertyName("carouselMediaIds")] public List<Guid> CarouselMediaIds { get; set; } = new();
    /// <summary>Optional background video for the `video` hero variant (direct MP4/WebM URL).</summary>
    [JsonPropertyName("videoUrl")] public string? VideoUrl { get; set; }
    [JsonPropertyName("overlayOpacity")] public int OverlayOpacity { get; set; } = 55;
    [JsonPropertyName("backgroundColor")] public string BackgroundColor { get; set; } = "#0d3a26";
    /// <summary>left | center | right</summary>
    [JsonPropertyName("textAlign")] public string TextAlign { get; set; } = "left";
    /// <summary>compact | standard | tall | screen</summary>
    [JsonPropertyName("height")] public string Height { get; set; } = "tall";
    [JsonPropertyName("contentWidth")] public string ContentWidth { get; set; } = "normal";
    /// <summary>cover | contain | top | center | bottom</summary>
    [JsonPropertyName("imagePosition")] public string ImagePosition { get; set; } = "center";
    /// <summary>stack | image-first | text-first</summary>
    [JsonPropertyName("mobileLayout")] public string MobileLayout { get; set; } = "stack";
    [JsonPropertyName("showLogo")] public bool ShowLogo { get; set; } = false;
    [JsonPropertyName("showFacts")] public bool ShowFacts { get; set; } = true;
}

/// <summary>One independently-configurable storefront section instance.</summary>
public class StorefrontSectionDto
{
    /// <summary>Stable instance id (lets two sections of the same key coexist).</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    /// <summary>Section type key — see StorefrontSectionCatalog.</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    /// <summary>Design variant of this section.</summary>
    [JsonPropertyName("variant")] public string Variant { get; set; } = "default";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("order")] public int Order { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    /// <summary>Free-form, section-specific settings (kept as JSON to avoid schema churn).</summary>
    [JsonPropertyName("settings")] public Dictionary<string, string> Settings { get; set; } = new();

    // ── Prototype section controls ──────────────────────────────────────────────
    /// <summary>Columns for grid layouts (1–4).</summary>
    [JsonPropertyName("columns")] public int Columns { get; set; } = 3;
    /// <summary>default | surface | primary | accent.</summary>
    [JsonPropertyName("background")] public string Background { get; set; } = "default";
    /// <summary>compact | normal | generous.</summary>
    [JsonPropertyName("padding")] public string Padding { get; set; } = "normal";
    /// <summary>left | center.</summary>
    [JsonPropertyName("alignment")] public string Alignment { get; set; } = "left";
    /// <summary>Entrance override: inherit | none | fade | up | left | zoom | blur.</summary>
    [JsonPropertyName("animation")] public string Animation { get; set; } = "inherit";
}

/// <summary>A social profile link.</summary>
public class StorefrontSocialLinkDto
{
    [JsonPropertyName("platform")] public string Platform { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
}

/// <summary>A free navigation/link entry.</summary>
public class StorefrontNavLinkDto
{
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("href")] public string Href { get; set; } = string.Empty;
}

/// <summary>Footer configuration.</summary>
public class StorefrontFooterDto
{
    /// <summary>minimal | columns | editorial | centered | dark | contact</summary>
    [JsonPropertyName("variant")] public string Variant { get; set; } = "columns";
    [JsonPropertyName("showLogo")] public bool ShowLogo { get; set; } = true;
    [JsonPropertyName("logoMediaId")] public Guid? LogoMediaId { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("showContact")] public bool ShowContact { get; set; } = true;
    [JsonPropertyName("showAddress")] public bool ShowAddress { get; set; } = true;
    [JsonPropertyName("showHours")] public bool ShowHours { get; set; } = true;
    [JsonPropertyName("showSocial")] public bool ShowSocial { get; set; } = true;
    [JsonPropertyName("socials")] public List<StorefrontSocialLinkDto> Socials { get; set; } = new();
    [JsonPropertyName("showNav")] public bool ShowNav { get; set; } = true;
    [JsonPropertyName("showLegal")] public bool ShowLegal { get; set; } = true;
    [JsonPropertyName("legalLinks")] public List<StorefrontNavLinkDto> LegalLinks { get; set; } = new();
    [JsonPropertyName("copyright")] public string? Copyright { get; set; }
    [JsonPropertyName("backgroundColor")] public string BackgroundColor { get; set; } = "#12110f";
    [JsonPropertyName("textColor")] public string TextColor { get; set; } = "#efe6d2";

    // ── Prototype footer controls ───────────────────────────────────────────────
    /// <summary>Vertical padding in pixels (24–120).</summary>
    [JsonPropertyName("padding")] public int Padding { get; set; } = 72;
    /// <summary>Headline for the `newsletter` and `split` layouts.</summary>
    [JsonPropertyName("newsletterHeadline")] public string? NewsletterHeadline { get; set; }
    [JsonPropertyName("newsletterPlaceholder")] public string NewsletterPlaceholder { get; set; } = "Your email";
    [JsonPropertyName("newsletterButtonLabel")] public string NewsletterButtonLabel { get; set; } = "Subscribe";
    /// <summary>Render the oversized business wordmark (the `wordmark` layout).</summary>
    [JsonPropertyName("showWordmark")] public bool ShowWordmark { get; set; }
}

/// <summary>One managed storefront page.</summary>
public class StorefrontPageDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
    /// <summary>home | services | about | contact | team | gallery | reviews | booking | custom</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "custom";
    [JsonPropertyName("showInNav")] public bool ShowInNav { get; set; } = true;
    [JsonPropertyName("order")] public int Order { get; set; }
    [JsonPropertyName("seoTitle")] public string? SeoTitle { get; set; }
    [JsonPropertyName("seoDescription")] public string? SeoDescription { get; set; }

    // ── Per-page design (the prototype's multi-page model) ──────────────────────
    /// <summary>
    /// This page's own hero. Null keeps the storefront-wide hero — which is what
    /// every configuration written before per-page heroes existed resolves to, so
    /// an existing storefront renders exactly as it did.
    /// </summary>
    [JsonPropertyName("hero")] public StorefrontHeroDto? Hero { get; set; }

    /// <summary>This page's own sections. Null/empty keeps the storefront-wide list.</summary>
    [JsonPropertyName("sections")] public List<StorefrontSectionDto>? Sections { get; set; }
}

/// <summary>SEO + social-sharing metadata.</summary>
public class StorefrontSeoDto
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("ogTitle")] public string? OgTitle { get; set; }
    [JsonPropertyName("ogDescription")] public string? OgDescription { get; set; }
    [JsonPropertyName("ogImageMediaId")] public Guid? OgImageMediaId { get; set; }
    [JsonPropertyName("faviconMediaId")] public Guid? FaviconMediaId { get; set; }
    [JsonPropertyName("keywords")] public string? Keywords { get; set; }
}

/// <summary>The complete storefront builder configuration (draft or published).</summary>
public class StorefrontConfigDto
{
    // Mirrors StorefrontBuilderDefaults.DefaultTemplateId (the `Classic` template).
    [JsonPropertyName("templateId")] public string TemplateId { get; set; } = "classic";
    [JsonPropertyName("theme")] public StorefrontThemeDto Theme { get; set; } = new();
    [JsonPropertyName("header")] public StorefrontHeaderDto Header { get; set; } = new();
    [JsonPropertyName("hero")] public StorefrontHeroDto Hero { get; set; } = new();
    [JsonPropertyName("sections")] public List<StorefrontSectionDto> Sections { get; set; } = new();
    [JsonPropertyName("footer")] public StorefrontFooterDto Footer { get; set; } = new();
    [JsonPropertyName("pages")] public List<StorefrontPageDto> Pages { get; set; } = new();
    [JsonPropertyName("seo")] public StorefrontSeoDto Seo { get; set; } = new();
}

/// <summary>Owner builder state: draft + published + publication metadata.</summary>
public class StorefrontBuilderStateResponse
{
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("templateId")] public string TemplateId { get; set; } = "classic";
    [JsonPropertyName("isPublished")] public bool IsPublished { get; set; }
    [JsonPropertyName("publishedAt")] public DateTime? PublishedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime? UpdatedAt { get; set; }
    [JsonPropertyName("hasDraft")] public bool HasDraft { get; set; }
    [JsonPropertyName("hasUnpublishedChanges")] public bool HasUnpublishedChanges { get; set; }
    [JsonPropertyName("draft")] public StorefrontConfigDto Draft { get; set; } = new();
    [JsonPropertyName("published")] public StorefrontConfigDto? Published { get; set; }

    /// <summary>
    /// Optimistic-concurrency token for the draft. The editor echoes this back on
    /// save / publish / restore; a mismatch means someone else saved in between and
    /// the request is refused with <c>STALE_VERSION</c> rather than clobbering it.
    /// </summary>
    [JsonPropertyName("draftVersion")] public int DraftVersion { get; set; }

    /// <summary>Highest revision number recorded for this business (0 = none yet).</summary>
    [JsonPropertyName("latestRevisionNumber")] public int LatestRevisionNumber { get; set; }
}

/// <summary>Owner draft save request (whole-config replacement).</summary>
public class SaveStorefrontDraftRequest
{
    [JsonPropertyName("config")] public StorefrontConfigDto Config { get; set; } = new();

    /// <summary>
    /// The <c>draftVersion</c> the editor last saw. Optional for backward
    /// compatibility; when supplied and stale the save is refused.
    /// </summary>
    [JsonPropertyName("expectedVersion")] public int? ExpectedVersion { get; set; }
}

/// <summary>Owner publish request. Omitted config publishes the current draft.</summary>
public class PublishStorefrontRequest
{
    [JsonPropertyName("config")] public StorefrontConfigDto? Config { get; set; }

    /// <summary>See <see cref="SaveStorefrontDraftRequest.ExpectedVersion"/>.</summary>
    [JsonPropertyName("expectedVersion")] public int? ExpectedVersion { get; set; }
}


/// <summary>
/// Public, sanitized storefront projection. Carries only presentation + resolved
/// media variants — never owner ids, mpesa numbers or source keys.
/// </summary>
public class PublicStorefrontDto
{
    [JsonPropertyName("templateId")] public string TemplateId { get; set; } = "classic";
    [JsonPropertyName("isPublished")] public bool IsPublished { get; set; }
    [JsonPropertyName("publishedAt")] public DateTime? PublishedAt { get; set; }
    [JsonPropertyName("theme")] public StorefrontThemeDto Theme { get; set; } = new();
    [JsonPropertyName("header")] public StorefrontHeaderDto Header { get; set; } = new();
    [JsonPropertyName("hero")] public StorefrontHeroDto Hero { get; set; } = new();
    [JsonPropertyName("sections")] public List<StorefrontSectionDto> Sections { get; set; } = new();
    [JsonPropertyName("footer")] public StorefrontFooterDto Footer { get; set; } = new();
    [JsonPropertyName("pages")] public List<StorefrontPageDto> Pages { get; set; } = new();
    [JsonPropertyName("seo")] public StorefrontSeoDto Seo { get; set; } = new();

    /// <summary>Resolved hero background variants (widest-first).</summary>
    [JsonPropertyName("heroBackgroundVariants")] public List<ServiceImageVariantResponse> HeroBackgroundVariants { get; set; } = new();
    /// <summary>Resolved carousel variants, one list per slide.</summary>
    [JsonPropertyName("heroCarouselVariants")] public List<List<ServiceImageVariantResponse>> HeroCarouselVariants { get; set; } = new();
    /// <summary>Resolved theme background image variants.</summary>
    [JsonPropertyName("themeBackgroundVariants")] public List<ServiceImageVariantResponse> ThemeBackgroundVariants { get; set; } = new();
    /// <summary>Resolved header logo variants.</summary>
    [JsonPropertyName("headerLogoVariants")] public List<ServiceImageVariantResponse> HeaderLogoVariants { get; set; } = new();
    /// <summary>Resolved footer logo variants.</summary>
    [JsonPropertyName("footerLogoVariants")] public List<ServiceImageVariantResponse> FooterLogoVariants { get; set; } = new();
    /// <summary>Resolved Open Graph image variants.</summary>
    [JsonPropertyName("ogImageVariants")] public List<ServiceImageVariantResponse> OgImageVariants { get; set; } = new();
    /// <summary>Resolved favicon variants.</summary>
    [JsonPropertyName("faviconVariants")] public List<ServiceImageVariantResponse> FaviconVariants { get; set; } = new();
}

