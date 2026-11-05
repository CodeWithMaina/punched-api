namespace PunchedApi.Domain.Entities;

/// <summary>
/// Platform-administration lifecycle of a <see cref="Business"/>.
///
/// <para>This is a SEPARATE axis from the business's subscription state and from
/// <see cref="Business.IsDeleted"/>:</para>
/// <list type="bullet">
/// <item><see cref="Active"/> — normal operation; the business may use the platform
/// subject to its module entitlements.</item>
/// <item><see cref="Suspended"/> — reversible administrative hold. The business and
/// all of its data are retained; access is withheld until an administrator
/// reactivates it.</item>
/// <item><see cref="Deactivated"/> — administrative closure. Data is retained (never
/// hard-deleted) and the business is excluded from active operations; it may be
/// reactivated by an administrator.</item>
/// </list>
///
/// <para>The value is stored in <c>businesses.status</c> and validated against this
/// set on write.</para>
/// </summary>
public static class BusinessStatus
{
    /// <summary>Normal operation.</summary>
    public const string Active = "active";

    /// <summary>Reversible administrative hold.</summary>
    public const string Suspended = "suspended";

    /// <summary>Administrative closure (data retained).</summary>
    public const string Deactivated = "deactivated";

    /// <summary>All values that may be persisted.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Active, Suspended, Deactivated };

    /// <summary>True when <paramref name="value"/> is a persisted status value.</summary>
    public static bool IsValid(string? value) =>
        value is Active or Suspended or Deactivated;
}
