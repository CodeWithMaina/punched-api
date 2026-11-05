namespace PunchedApi.Domain.Entities;

/// <summary>
/// The lifecycle states a <see cref="BusinessSubscription"/> can hold.
///
/// <para>Entitlement resolution treats ONLY <see cref="Active"/> and
/// <see cref="Trial"/> (with a non-elapsed <c>EndsAt</c>) as granting access —
/// every other state withholds module access while preserving the subscription
/// row and its history.</para>
/// </summary>
public static class SubscriptionStatus
{
    /// <summary>Paying, in good standing.</summary>
    public const string Active = "active";

    /// <summary>Trial period; grants access like <see cref="Active"/> until it elapses.</summary>
    public const string Trial = "trial";

    /// <summary>Payment failed but not yet cancelled.</summary>
    public const string PastDue = "past_due";

    /// <summary>Administratively paused; access withheld, reversible.</summary>
    public const string Paused = "paused";

    /// <summary>Cancelled by the owner or an administrator; reversible via reactivation.</summary>
    public const string Canceled = "canceled";

    /// <summary>Lapsed; reversible via reactivation/renewal.</summary>
    public const string Expired = "expired";

    /// <summary>Administratively deactivated; access withheld, reversible.</summary>
    public const string Deactivated = "deactivated";

    /// <summary>States that grant module access (subject to <c>EndsAt</c>).</summary>
    public static readonly IReadOnlyList<string> AccessGranting = new[] { Active, Trial };

    /// <summary>States that withhold module access but retain the subscription.</summary>
    public static readonly IReadOnlyList<string> AccessWithholding =
        new[] { PastDue, Paused, Canceled, Expired, Deactivated };

    /// <summary>True when the status grants access (before the <c>EndsAt</c> check).</summary>
    public static bool GrantsAccess(string? status) =>
        status is Active or Trial;
}
