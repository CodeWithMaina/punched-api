using Microsoft.AspNetCore.Authorization;

namespace PunchedApi.Application.Authorization;

/// <summary>
/// The API's authorization default posture: <b>deny by default</b>.
///
/// Every endpoint requires an authenticated caller unless it is explicitly
/// marked <see cref="AllowAnonymousAttribute"/>. This closes the
/// "forgot the [Authorize] attribute" class of bug — a newly added action is
/// protected the moment it exists, and making it public is a deliberate,
/// reviewable act.
///
/// The explicit public allow-list (storefront reads: slug resolution, public
/// business profile, public directory, service catalog reads, review reads,
/// referral entry points, auth lifecycle, payment webhooks) is asserted by
/// <c>AuthorizationPostureTests</c>, so an unintended new anonymous endpoint
/// fails the build's test run rather than shipping.
/// </summary>
public static class DenyByDefaultAuthorization
{
    /// <summary>
    /// Applies the fallback policy to <see cref="AuthorizationOptions"/>.
    /// Extracted so the posture itself is unit-testable without a web host.
    /// </summary>
    public static void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // FallbackPolicy only applies where no [Authorize]/[AllowAnonymous]
        // attribute (or policy) already decided the outcome, so existing
        // role restrictions keep working untouched.
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    }
}
