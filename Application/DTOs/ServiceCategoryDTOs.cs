using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ===============================================================
//  SERVICE CATEGORIES - DTOs
// ===============================================================

/// <summary>
/// A business-scoped grouping of catalogue services. Categories are free-form
/// per business: the platform never imposes salon-specific (or
/// barbershop/tattoo/spa) labels globally.
/// </summary>
public class ServiceCategoryResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>Owner-controlled display order (ascending).</summary>
    [JsonPropertyName("sortOrder")] public int SortOrder { get; set; }

    /// <summary>
    /// False hides the category from PUBLIC grouping. Services assigned to a
    /// deactivated category keep their assignment and stay bookable.
    /// </summary>
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }

    /// <summary>How many services reference this category (owner surfaces only).</summary>
    [JsonPropertyName("serviceCount")] public int ServiceCount { get; set; }

    /// <summary>True when the category has no services and can therefore be deleted.</summary>
    [JsonPropertyName("canDelete")] public bool CanDelete { get; set; }

    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }
}

/// <summary>Creates a category for the owner business.</summary>
public class CreateServiceCategoryRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>Omitted appends to the end of the current order.</summary>
    [JsonPropertyName("sortOrder")] public int? SortOrder { get; set; }

    [JsonPropertyName("isActive")] public bool? IsActive { get; set; }
}

/// <summary>Partially updates a category. Only provided fields are applied.</summary>
public class UpdateServiceCategoryRequest
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("isActive")] public bool? IsActive { get; set; }
}

/// <summary>
/// Deterministic reordering: the supplied ids are written in the given order,
/// starting at 0. Ids that belong to another business are rejected outright
/// rather than silently ignored.
/// </summary>
public class ReorderServiceCategoriesRequest
{
    [JsonPropertyName("orderedIds")] public Guid[] OrderedIds { get; set; } = [];
}

/// <summary>Filter for the owner service list.</summary>
public class ServiceCatalogQueryRequest
{
    /// <summary>Restrict to one category. Use the sentinel <c>uncategorized</c> for services with no category.</summary>
    [JsonPropertyName("categoryId")] public Guid? CategoryId { get; set; }

    /// <summary>When true, only services with no category are returned.</summary>
    [JsonPropertyName("uncategorized")] public bool Uncategorized { get; set; }

    [JsonPropertyName("isActive")] public bool? IsActive { get; set; }
    [JsonPropertyName("search")] public string? Search { get; set; }
    [JsonPropertyName("sortBy")] public string? SortBy { get; set; }
    [JsonPropertyName("sortDirection")] public string? SortDirection { get; set; }
    [JsonPropertyName("page")] public int Page { get; set; } = 1;
    [JsonPropertyName("pageSize")] public int PageSize { get; set; } = 25;
}

/// <summary>
/// A public catalogue category with the business published services that belong
/// to it. Powers storefront grouping without the storefront re-implementing
/// visibility rules.
/// </summary>
public class PublicServiceCategoryGroupResponse
{
    [JsonPropertyName("categoryId")] public Guid? CategoryId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("sortOrder")] public int SortOrder { get; set; }
    [JsonPropertyName("services")] public List<ServiceCatalogItemResponse> Services { get; set; } = [];
}

/// <summary>The public, category-grouped catalogue of one business.</summary>
public class PublicServiceCatalogResponse
{
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("businessName")] public string BusinessName { get; set; } = string.Empty;

    /// <summary>Active categories in display order, each with its published services.</summary>
    [JsonPropertyName("categories")] public List<PublicServiceCategoryGroupResponse> Categories { get; set; } = [];

    /// <summary>
    /// Published services that are not assigned to any ACTIVE category (either
    /// uncategorised, or filed under a deactivated category). Always present as
    /// a group so a storefront never silently hides bookable services.
    /// </summary>
    [JsonPropertyName("uncategorized")] public List<ServiceCatalogItemResponse> Uncategorized { get; set; } = [];
}

// ===============================================================
//  BUSINESS-TYPE STARTER CATEGORY TEMPLATES
// ===============================================================

/// <summary>
/// Optional starter category templates, keyed by the business type the owner
/// declared at onboarding (<c>Business.Category</c>).
///
/// <para><b>These are templates, not global categories.</b> They are only ever
/// COPIED into a business on request, and only when the business has no
/// categories of its own — so re-running onboarding or a seed operation can
/// never duplicate or overwrite a business curated labels.</para>
///
/// <para>Business types are matched leniently (case/space/separator
/// insensitive, substring aware) because <c>Business.Category</c> is free text
/// ("salon", "Hair &amp; Beauty Salon", "barber shop", "tattoo studio",
/// "Day Spa", ...). An unrecognised type simply yields no template, which is
/// the correct outcome: the platform does not guess.</para>
/// </summary>
public static class ServiceCategoryTemplates
{
    /// <summary>One starter category in a business-type template.</summary>
    public sealed record StarterCategory(string Name, string? Description);

    /// <summary>Canonical business-type key -> starter categories (in display order).</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<StarterCategory>> Templates =
        new Dictionary<string, IReadOnlyList<StarterCategory>>(StringComparer.Ordinal)
        {
            ["salon"] = new[]
            {
                new StarterCategory("Haircuts", "Cuts, trims and restyling."),
                new StarterCategory("Hair Colouring", "Colour, highlights and toning."),
                new StarterCategory("Braiding", "Braids, twists and protective styles."),
                new StarterCategory("Hair Treatments", "Conditioning, relaxing and scalp care."),
                new StarterCategory("Nail Services", "Manicures, pedicures and nail art."),
                new StarterCategory("Makeup", "Everyday, bridal and event makeup.")
            },
            ["barbershop"] = new[]
            {
                new StarterCategory("Haircuts", "Fades, tapers and classic cuts."),
                new StarterCategory("Beard Grooming", "Beard shaping, trims and oiling."),
                new StarterCategory("Shaving", "Hot-towel and clean shaves."),
                new StarterCategory("Hair Treatments", "Scalp care and hairline treatments."),
                new StarterCategory("Grooming Packages", "Bundled cut, beard and treatment packages.")
            },
            ["tattoo"] = new[]
            {
                new StarterCategory("Custom Tattoos", "Bespoke, one-off designs."),
                new StarterCategory("Flash Tattoos", "Ready-to-ink designs from the studio book."),
                new StarterCategory("Tattoo Touch-ups", "Reworks and refreshes of existing pieces."),
                new StarterCategory("Piercing", "Studio piercings and jewellery changes."),
                new StarterCategory("Consultations", "Design consultations and quotes.")
            },
            ["spa"] = new[]
            {
                new StarterCategory("Massages", "Swedish, deep tissue and sports massage."),
                new StarterCategory("Facials", "Cleansing, anti-ageing and hydrating facials."),
                new StarterCategory("Body Treatments", "Scrubs, wraps and body therapies."),
                new StarterCategory("Waxing", "Full-body and targeted waxing."),
                new StarterCategory("Wellness Packages", "Multi-treatment spa journeys.")
            }
        };

    /// <summary>The canonical business-type keys that have a starter template.</summary>
    public static IReadOnlyCollection<string> SupportedBusinessTypes => Templates.Keys.ToArray();

    /// <summary>
    /// Resolves a declared business type to its canonical template key, or null
    /// when the type is unknown or the value is blank.
    /// </summary>
    public static string? ResolveTemplateKey(string? businessType)
    {
        if (string.IsNullOrWhiteSpace(businessType)) return null;

        var normalized = Normalize(businessType);

        foreach (var key in Templates.Keys)
        {
            if (normalized.Contains(key, StringComparison.Ordinal)) return key;
        }

        // A couple of very common synonyms that do not contain the key text.
        if (normalized.Contains("hairsalon", StringComparison.Ordinal) ||
            normalized.Contains("beautysalon", StringComparison.Ordinal)) return "salon";
        if (normalized.Contains("barber", StringComparison.Ordinal)) return "barbershop";
        if (normalized.Contains("ink", StringComparison.Ordinal) ||
            normalized.Contains("piercing", StringComparison.Ordinal)) return "tattoo";

        return null;
    }

    /// <summary>
    /// The starter categories for a declared business type. Empty when the type
    /// is unrecognised, so no business ever receives guessed labels.
    /// </summary>
    public static IReadOnlyList<StarterCategory> For(string? businessType)
    {
        var key = ResolveTemplateKey(businessType);
        return key != null && Templates.TryGetValue(key, out var template)
            ? template
            : Array.Empty<StarterCategory>();
    }

    /// <summary>Lowercases and drops every non-alphanumeric character.</summary>
    private static string Normalize(string value)
    {
        var buffer = new char[value.Length];
        var length = 0;
        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c)) continue;
            buffer[length++] = char.ToLowerInvariant(c);
        }

        return new string(buffer, 0, length);
    }
}
