namespace PunchedApi.Domain.Entities;

/// <summary>
/// The two ways a business can operate, persisted on
/// <see cref="Business.OperatingMode"/> and treated as first-class business
/// configuration — not a UI hint.
///
/// <list type="bullet">
/// <item><see cref="Solo"/> — the owner is the only service provider. Every
/// active service is bookable with the owner and the staff layer is not
/// required (and not shown) anywhere.</item>
/// <item><see cref="MultiStaff"/> — the business has a team. The owner may also
/// provide services, and providers are resolved from category assignments.</item>
/// </list>
///
/// <para><b>Why a string and not a C# enum?</b> The value is persisted next to
/// other free-form-but-validated business state (status codes, goal types) and
/// constrained by a check constraint, so a rolling deploy that adds a third
/// mode does not need an enum migration. Callers must go through
/// <see cref="Normalize"/> / <see cref="All"/> instead of comparing literals.</para>
/// </summary>
public static class BusinessOperatingModes
{
    /// <summary>The owner provides every service; no staff layer.</summary>
    public const string Solo = "solo";

    /// <summary>A team of providers; owners and staff are resolved from categories.</summary>
    public const string MultiStaff = "multi_staff";

    /// <summary>Every persisted value. Used by the database check constraint.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Solo, MultiStaff };

    /// <summary>True when <paramref name="value"/> is one of the persisted modes.</summary>
    public static bool IsValid(string? value) =>
        value is not null && All.Contains(value.Trim());

    /// <summary>
    /// Canonicalizes an incoming value. Unknown/blank input falls back to
    /// <see cref="MultiStaff"/>, which is the behaviour every pre-existing
    /// business had before this column existed — so an old client that never
    /// sends a mode cannot accidentally flip a multi-staff tenant into solo.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return MultiStaff;
        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "solo" or "single" or "single_staff" or "single-staff" or "owner" => Solo,
            "multi" or "multi_staff" or "multi-staff" or "team" or "staff" => MultiStaff,
            _ => All.Contains(normalized) ? normalized : MultiStaff
        };
    }

    /// <summary>True when the mode means "the owner is the provider for everything".</summary>
    public static bool IsSolo(string? value) =>
        string.Equals(Normalize(value), Solo, StringComparison.Ordinal);

    /// <summary>Human label for the dashboard badge.</summary>
    public static string Label(string? value) =>
        IsSolo(value) ? "Solo Business" : "Multi-Staff Business";
}
