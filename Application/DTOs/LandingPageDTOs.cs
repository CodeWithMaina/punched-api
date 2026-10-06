// ═══════════════════════════════════════════════════════════════
//  LANDING PAGE CONFIGURATION DTOs
//  Controlled presentation/visibility/ordering/overrides only —
//  domain data stays in its owning module.
// ═══════════════════════════════════════════════════════════════

using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

/// <summary>Hero/header presentation overrides.</summary>
public class LandingPageHeroDto
{
    [JsonPropertyName("backgroundMediaId")]
    public Guid? BackgroundMediaId { get; set; }

    [JsonPropertyName("showLogo")]
    public bool ShowLogo { get; set; } = true;

    [JsonPropertyName("titleOverride")]
    public string? TitleOverride { get; set; }

    [JsonPropertyName("taglineOverride")]
    public string? TaglineOverride { get; set; }

    [JsonPropertyName("descriptionOverride")]
    public string? DescriptionOverride { get; set; }
}

/// <summary>Navigation visibility toggles.</summary>
public class LandingPageNavigationDto
{
    [JsonPropertyName("services")]
    public bool Services { get; set; } = true;

    [JsonPropertyName("loyalty")]
    public bool Loyalty { get; set; } = true;

    [JsonPropertyName("about")]
    public bool About { get; set; } = true;

    [JsonPropertyName("reviews")]
    public bool Reviews { get; set; } = true;

    [JsonPropertyName("contact")]
    public bool Contact { get; set; } = true;

    [JsonPropertyName("gallery")]
    public bool Gallery { get; set; } = true;

    [JsonPropertyName("booking")]
    public bool Booking { get; set; } = true;
}

/// <summary>One configurable section: visibility + order + optional overrides.</summary>
public class LandingPageSectionDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>One CTA button: visibility + destination type + label.</summary>
public class LandingPageCtaDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "book";

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}

/// <summary>Full landing-page configuration payload (owner + public share the shape).</summary>
public class LandingPageConfigDto
{
    [JsonPropertyName("hero")]
    public LandingPageHeroDto Hero { get; set; } = new();

    [JsonPropertyName("navigation")]
    public LandingPageNavigationDto Navigation { get; set; } = new();

    [JsonPropertyName("sections")]
    public Dictionary<string, LandingPageSectionDto> Sections { get; set; } = new();

    [JsonPropertyName("primaryCta")]
    public LandingPageCtaDto PrimaryCta { get; set; } = new();

    [JsonPropertyName("secondaryCta")]
    public LandingPageCtaDto SecondaryCta { get; set; } = new();
}

/// <summary>Owner read/write view: full config + optimistic-concurrency version.</summary>
public class LandingPageConfigResponse
{
    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }

    [JsonPropertyName("config")]
    public LandingPageConfigDto Config { get; set; } = new();
}

/// <summary>Owner save request: full config + expected version (0 = create/defaults).</summary>
public class UpdateLandingPageRequest
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("config")]
    public LandingPageConfigDto Config { get; set; } = new();
}

/// <summary>
/// Anonymous public projection: resolved strings + sanitized media variants +
/// capability-filtered nav/sections. Never carries OwnerId/MpesaNumber/source keys.
/// </summary>
public class PublicLandingPageResponse
{
    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("heroTitle")]
    public string HeroTitle { get; set; } = string.Empty;

    [JsonPropertyName("heroTagline")]
    public string HeroTagline { get; set; } = string.Empty;

    [JsonPropertyName("heroDescription")]
    public string? HeroDescription { get; set; }

    [JsonPropertyName("showLogo")]
    public bool ShowLogo { get; set; } = true;

    [JsonPropertyName("heroBackgroundMediaId")]
    public Guid? HeroBackgroundMediaId { get; set; }

    [JsonPropertyName("heroBackgroundVariants")]
    public IReadOnlyList<ServiceImageVariantResponse> HeroBackgroundVariants { get; set; } = [];

    [JsonPropertyName("navigation")]
    public Dictionary<string, bool> Navigation { get; set; } = new();

    [JsonPropertyName("sections")]
    public List<PublicLandingPageSection> Sections { get; set; } = new();

    [JsonPropertyName("primaryCta")]
    public LandingPageCtaDto PrimaryCta { get; set; } = new();

    [JsonPropertyName("secondaryCta")]
    public LandingPageCtaDto SecondaryCta { get; set; } = new();
}

public class PublicLandingPageSection
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
