using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Services;

/// <summary>
/// Platform defaults for the controlled landing-page configuration.
/// New and legacy businesses without a stored row resolve to this —
/// which reproduces today's hardcoded storefront behavior.
/// </summary>
public static class LandingPageDefaults
{
    public static readonly IReadOnlyList<string> SectionKeys =
        ["about", "services", "loyalty", "gallery", "booking", "reviews", "contact", "businessInfo"];

    public static readonly IReadOnlyDictionary<string, int> DefaultOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["about"] = 5,
        ["services"] = 10,
        ["loyalty"] = 15,
        ["gallery"] = 20,
        ["booking"] = 25,
        ["reviews"] = 30,
        ["contact"] = 35,
        ["businessInfo"] = 40,
    };

    public static readonly IReadOnlySet<string> CtaTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "book", "services", "loyalty", "contact", "reviews", "directions" };

    public static LandingPageConfigDto Create()
    {
        return new LandingPageConfigDto
        {
            Hero = new LandingPageHeroDto { ShowLogo = true },
            Navigation = new LandingPageNavigationDto
            {
                Services = true, Loyalty = true, About = true, Reviews = true,
                Contact = true, Gallery = true, Booking = true,
            },
            Sections = DefaultOrder.ToDictionary(
                kv => kv.Key,
                kv => new LandingPageSectionDto { Enabled = true, Order = kv.Value },
                StringComparer.OrdinalIgnoreCase),
            PrimaryCta = new LandingPageCtaDto { Enabled = true, Type = "book", Label = "Book an appointment" },
            SecondaryCta = new LandingPageCtaDto { Enabled = true, Type = "services", Label = "View services" },
        };
    }

    /// <summary>
    /// Merge stored overrides over defaults so new keys added later default to on
    /// and partial/legacy payloads can never produce a half-empty page.
    /// Unknown section keys are dropped; missing ones are filled from defaults.
    /// </summary>
    public static LandingPageConfigDto Merge(LandingPageConfigDto? stored)
    {
        var merged = Create();
        if (stored == null) return merged;

        if (stored.Hero != null)
        {
            merged.Hero.ShowLogo = stored.Hero.ShowLogo;
            merged.Hero.TitleOverride = stored.Hero.TitleOverride;
            merged.Hero.TaglineOverride = stored.Hero.TaglineOverride;
            merged.Hero.DescriptionOverride = stored.Hero.DescriptionOverride;
        }

        if (stored.Navigation != null)
        {
            merged.Navigation.Services = stored.Navigation.Services;
            merged.Navigation.Loyalty = stored.Navigation.Loyalty;
            merged.Navigation.About = stored.Navigation.About;
            merged.Navigation.Reviews = stored.Navigation.Reviews;
            merged.Navigation.Contact = stored.Navigation.Contact;
            merged.Navigation.Gallery = stored.Navigation.Gallery;
            merged.Navigation.Booking = stored.Navigation.Booking;
        }

        if (stored.Sections != null)
        {
            foreach (var key in SectionKeys)
            {
                if (stored.Sections.TryGetValue(key, out var section) && section != null)
                {
                    merged.Sections[key] = new LandingPageSectionDto
                    {
                        Enabled = section.Enabled,
                        Order = section.Order,
                        Title = section.Title,
                        Description = section.Description,
                    };
                }
            }
        }

        if (stored.PrimaryCta != null && CtaTypes.Contains(stored.PrimaryCta.Type ?? string.Empty))
        {
            merged.PrimaryCta = new LandingPageCtaDto
            {
                Enabled = stored.PrimaryCta.Enabled,
                Type = stored.PrimaryCta.Type ?? merged.PrimaryCta.Type,
                Label = stored.PrimaryCta.Label ?? string.Empty,
            };
        }

        if (stored.SecondaryCta != null && CtaTypes.Contains(stored.SecondaryCta.Type ?? string.Empty))
        {
            merged.SecondaryCta = new LandingPageCtaDto
            {
                Enabled = stored.SecondaryCta.Enabled,
                Type = stored.SecondaryCta.Type ?? merged.SecondaryCta.Type,
                Label = stored.SecondaryCta.Label ?? string.Empty,
            };
        }

        return merged;
}
}
