namespace PunchedApi.Application.Services;

/// <summary>
/// The starter set of service categories seeded into every new business (and
/// back-filled for businesses that have none yet).
///
/// <para><b>Seeding is a convenience, not a constraint.</b> These are copied
/// per business as ordinary <c>service_categories</c> rows the owner can
/// rename, reorder, deactivate or delete — there is no global template table
/// and no code path that treats any of these names as special. Idempotency is
/// guaranteed by the caller: seeding only ever runs for a business that
/// currently has zero categories, and the (business_id, name) unique index is
/// the database backstop.</para>
/// </summary>
public static class ServiceCategoryDefaults
{
    /// <summary>A default category: display name plus a short owner-facing blurb.</summary>
    public readonly record struct DefaultCategory(string Name, string Description);

    /// <summary>The ordered default set.</summary>
    public static readonly IReadOnlyList<DefaultCategory> All =
    [
        new("Barber",       "Cuts, beard grooming and shaves."),
        new("Hair",         "Styling, colouring and treatments."),
        new("Tattoo",       "Custom and flash tattoo work."),
        new("Lashes",       "Lash extensions, lifts and tints."),
        new("Nails",        "Manicures, pedicures and nail art."),
        new("Massage",      "Relaxation and therapeutic massage."),
        new("Makeup",       "Event, bridal and everyday makeup."),
        new("Skincare",     "Facials, peels and skin treatments."),
        new("Spa",          "Body treatments and wellness packages."),
        new("Other",        "Anything that does not fit the categories above.")
    ];
}
