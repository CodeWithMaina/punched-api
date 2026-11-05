using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PunchedApi.Application.Authorization;

namespace PunchedApi.Tests;

/// <summary>
/// Deny-by-default authorization posture (migration Phase 0 / risk R1).
///
/// Three invariants, all build-blocking:
/// 1. The fallback policy actually requires an authenticated user — a new
///    endpoint with no attributes is protected the moment it exists.
/// 2. Every HTTP action is EXPLICITLY annotated ([Authorize] on the method or
///    class, or [AllowAnonymous]) — no endpoint silently leans on the fallback.
/// 3. The anonymous allow-list is exactly the documented storefront/auth/webhook
///    set — a newly anonymous endpoint (or an anonymous one losing its
///    annotation) fails here rather than shipping.
/// </summary>
public class AuthorizationPostureTests
{
    private static readonly Assembly ApiAssembly =
        typeof(PunchedApi.API.Controllers.AuthController).Assembly;

    /// <summary>
    /// The documented public allow-list: auth lifecycle, storefront reads
    /// (directory / profile / slug resolution / availability / catalog /
    /// loyalty preview / reviews / referral entry points), public media and
    /// the payment webhook. Anything else MUST be authenticated.
    /// </summary>
    private static readonly HashSet<string> AnonymousAllowList = new()
    {
        // Auth lifecycle
        "Auth|POST|register",
        "Auth|POST|verify-email",
        "Auth|POST|login",
        "Auth|POST|refresh-token",
        "Auth|POST|request-email",
        "Auth|POST|forgot-password",
        "Auth|POST|reset-password",
        // Storefront / directory reads
        "Business|GET|",
        "Business|GET|public/{businessId:guid}",
        "Business|GET|by-slug/{slug}",
        // Public storefront bootstrap (slug → identity + public profile +
        // landing page). Output-cached per slug; the single call that renders a
        // tenant storefront, so it must stay anonymous like the reads above.
        "Business|GET|by-slug/{slug}/bootstrap",
        // Public storefront enquiry (the contact form on a business's own page).
        // Anonymous because a visitor asking a question has no account and is not
        // asked to create one. It is a WRITE, so it is bounded on its own
        // (`storefront-enquiry`: 10/hour/IP) rather than sharing the general
        // bucket, and it can only ever reach a business the caller could already
        // read — the id resolves against active businesses, and an unknown one is
        // indistinguishable from a missing one. The service creates no customer
        // and sends the visitor nothing; the enquiry lands in the owner's console.
        "Business|POST|{businessId:guid}/enquiries",
        "Business|GET|{businessId:guid}/availability",
        "Business|GET|{businessId:guid}/availability/calendar",
        "LoyaltyCard|GET|program/{businessId:guid}",
        "LoyaltyCard|GET|programs/{businessId:guid}",
        "Media|GET|public/{id:guid}",
        "CardAsset|GET|deliver/{token}",
        "Referral|GET|programs/{businessId:guid}",
        "Referral|POST|links/{code}/open",
        "Review|GET|businesses/{businessId:guid}/reviews",
        "Review|GET|businesses/{businessId:guid}/reviews/summary",
        "ServiceCatalog|GET|{businessId:guid}",
        // Public storefront gallery, opening hours and search. All three are
        // read-only, anonymous, tenant-narrowed reads of ONE business the caller
        // could already read (its id resolves against active businesses), and
        // each applies its module entitlement gate inside the service because an
        // anonymous caller has no token for [RequireModule] to resolve. Search is
        // the heaviest of the three, so it carries its own tighter budget
        // (`storefront-search`: 120/hour/IP).
        "Storefront|GET|{businessId:guid}/gallery",
        "Storefront|GET|{businessId:guid}/opening-hours",
        "Storefront|GET|{businessId:guid}/search",
        // Guest booking: the two anonymous steps of the account-free flow. The
        // first sends a one-time code to an address the caller supplies; the
        // second exchanges that code for a short-lived CUSTOMER token. Neither
        // creates an appointment or reads any customer's data: the booking itself
        // goes through POST /v1/appointments, which still requires the Customer
        // role. Both are rate-limited per IP, and the code has its own resend
        // cooldown, expiry and attempt ceiling.
        "GuestBooking|POST|{businessId:guid}/guest-booking/start",
        "GuestBooking|POST|{businessId:guid}/guest-booking/verify",
        // Public storefront catalogue grouped by ACTIVE category. Same
        // visibility rules as the flat public list above (active + showcased
        // services only) plus the serviceCatalog entitlement check performed
        // inside the service, because an anonymous route has no caller business
        // for [RequireModule] to resolve. It exists so a storefront renders
        // groups from backend data instead of re-implementing visibility rules.
        "ServiceCatalog|GET|{businessId:guid}/catalogue",
        "ServiceCatalog|GET|{businessId:guid}/staff",
        "ServiceCatalog|GET|{businessId:guid}/{id:guid}",
        "LandingPage|GET|public/{businessId:guid}/landing-page",
        // Public Storefront Builder projection: the published storefront config
        // (template, theme tokens, ordered sections, header/footer, pages) with
        // resolved media variants. Read-only, anonymous, tenant-narrowed and
        // capability-filtered; it never returns a draft or another business's data.
        "StorefrontBuilder|GET|public/{businessId:guid}",
        // Public plans + payment webhook
        "Subscription|GET|v1/plans",
        "Subscription|POST|v1/webhooks/payments",
        // M-Pesa/Daraja callbacks (external caller, rate-limited + idempotent)
        "DarajaWebhook|POST|stk",
        "DarajaWebhook|POST|c2b/confirmation",
        "DarajaWebhook|POST|c2b/validation",
        // Staff invitation acceptance from email links (token-gated)
        "Invitations|GET|{token}",
        "Invitations|POST|{token}/accept",
        // Marketing unsubscribe (encrypted-token-gated, RFC 8058 one-click POST)
        "Unsubscribe|GET|",
        "Unsubscribe|POST|",
        // Filesystem media provider (development/CI only). Both routes are inert
        // unless MediaStorage:Provider is 'local' — the store is injected as an
        // IEnumerable, so a production (R2) deployment answers 404 here.
        //   • the upload route accepts only a key + content-type + expiry triple
        //     signed with an HMAC and compared in constant time, so it is scoped
        //     exactly like a presigned URL and needs no bearer token (and accepts
        //     none): a forged or expired grant is a 403;
        //   • delivery resolves only keys under the public `delivery/` prefix, so
        //     a customer's private source upload is unreachable even if its key
        //     were known.
        "MediaLocalStore|PUT|v1/media/local/upload/{token}",
        "MediaLocalStore|GET|v1/media/delivery/{**key}",
    };

    private sealed record ActionInfo(
        string Controller,
        string Method,
        string HttpVerb,
        string Template,
        bool Anonymous,
        bool ExplicitlyAuthorized);

    private static IEnumerable<ActionInfo> EnumerateActions()
    {
        var controllers = ApiAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("Controller"));

        foreach (var controller in controllers)
        {
            var classAnonymous = HasAttribute<AllowAnonymousAttribute>(controller);
            var classAuthorized = HasAttribute<AuthorizeAttribute>(controller);

            foreach (var method in controller.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var http = method.GetCustomAttributes(true)
                    .OfType<Attribute>()
                    .FirstOrDefault(a => a.GetType().Name.StartsWith("Http") &&
                                         a.GetType().Name.EndsWith("Attribute"));
                if (http == null) continue;

                var verb = http.GetType().Name
                    .Replace("Attribute", "")
                    .Replace("Http", "")          // HttpPost → Post
                    .ToUpperInvariant();          // → POST
                var template = http.GetType().GetProperty("Template")?.GetValue(http) as string ?? "";

                yield return new ActionInfo(
                    controller.Name.Replace("Controller", ""),
                    method.Name,
                    verb,
                    template,
                    classAnonymous || HasAttribute<AllowAnonymousAttribute>(method),
                    classAuthorized || HasAttribute<AuthorizeAttribute>(method));
            }
        }

        static bool HasAttribute<T>(MemberInfo member) where T : Attribute =>
            member.GetCustomAttributes<T>(true).Any();
    }

    private static string Key(ActionInfo action) =>
        $"{action.Controller}|{action.HttpVerb}|{action.Template}";

    [Fact]
    public void FallbackPolicy_RequiresAuthenticatedUser()
    {
        var options = new AuthorizationOptions();
        DenyByDefaultAuthorization.Configure(options);

        Assert.NotNull(options.FallbackPolicy);
        Assert.Contains(
            options.FallbackPolicy.Requirements,
            r => r is Microsoft.AspNetCore.Authorization.Infrastructure
                .DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public void EveryHttpAction_IsExplicitlyAnnotated()
    {
        var unannotated = EnumerateActions()
            .Where(a => !a.Anonymous && !a.ExplicitlyAuthorized)
            .Select(a => $"{Key(a)} ({a.Method})")
            .OrderBy(k => k)
            .ToList();

        Assert.True(
            unannotated.Count == 0,
            "HTTP actions with neither [Authorize] nor [AllowAnonymous]: " +
            string.Join(", ", unannotated));
    }

    [Fact]
    public void AnonymousEndpoints_MatchTheDocumentedAllowList()
    {
        var actual = EnumerateActions()
            .Where(a => a.Anonymous)
            .Select(Key)
            .ToHashSet();

        var unexpected = actual.Except(AnonymousAllowList).OrderBy(k => k).ToList();
        var missing = AnonymousAllowList.Except(actual).OrderBy(k => k).ToList();

        Assert.True(
            unexpected.Count == 0,
            "Newly anonymous endpoints (add to the allow-list only after review): " +
            string.Join(", ", unexpected));
        Assert.True(
            missing.Count == 0,
            "Endpoints that lost their [AllowAnonymous] annotation: " +
            string.Join(", ", missing));
    }

    [Fact]
    public void Scan_IsNotVacuous()
    {
        // If reflection ever finds nothing, the two tests above would pass
        // vacuously — this pins minimum coverage instead.
        var actions = EnumerateActions().ToList();
        Assert.True(actions.Count > 50, $"Only {actions.Count} HTTP actions discovered.");
        Assert.Equal(AnonymousAllowList.Count, actions.Count(a => a.Anonymous));
    }
}