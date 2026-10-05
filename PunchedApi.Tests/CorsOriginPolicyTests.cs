using PunchedApi.Application.Authorization;

namespace PunchedApi.Tests;

/// <summary>
/// CORS origin matching for the tenant architecture.
///
/// Regression: <c>CorsPolicyBuilder.WithOrigins</c> treats
/// <c>http://*.localhost:3000</c> as a literal, so no business-subdomain origin
/// ever got an <c>Access-Control-Allow-Origin</c> header and the tenant
/// storefront rendered its "nobody home" screen instead of the business page.
/// </summary>
public class CorsOriginPolicyTests
{
    private static readonly string[] AllowList = CorsOriginPolicy.DefaultAllowedOrigins;

    [Theory]
    // Platform root + alternate dev ports + Swagger origin.
    [InlineData("http://localhost:3000")]
    [InlineData("http://localhost:3001")]
    [InlineData("http://localhost:5091")]
    // Local tenant subdomain — the address that was broken.
    [InlineData("http://aurelia-luxe-hair-atelier.localhost:3000")]
    [InlineData("http://java-house.localhost:3001")]
    // Production root / www / tenant subdomain.
    [InlineData("https://punched.app")]
    [InlineData("https://www.punched.app")]
    [InlineData("https://java-house.punched.app")]
    public void AllowedOrigins_Match(string origin) =>
        Assert.True(CorsOriginPolicy.IsAllowedOrigin(origin, AllowList), origin);

    [Theory]
    // Wrong scheme: dev wildcards are http, production wildcards https.
    [InlineData("https://java-house.localhost:3000")]
    [InlineData("http://java-house.punched.app")]
    // Multi-label host is infrastructure, never a tenant.
    [InlineData("https://a.b.punched.app")]
    [InlineData("https://a.b.localhost:3000")]
    // The wildcard suffix itself is not covered by the wildcard entry.
    [InlineData("https://sub.punched.app.attacker.test")]
    [InlineData("https://punched-app.attacker.test")]
    [InlineData("https://xpunched.app")]
    // Unrelated domains and lookalikes.
    [InlineData("https://punched.app.attacker.test")]
    [InlineData("https://attacker.test")]
    [InlineData("http://localhost:4000")]
    // Malformed / empty / opaque origins.
    [InlineData("https://.punched.app")]
    [InlineData("https://-bad.punched.app")]
    [InlineData("https://bad-.punched.app")]
    [InlineData("https://under_score.punched.app")]
    [InlineData("*")]
    [InlineData("null")]
    [InlineData("")]
    public void DisallowedOrigins_DoNotMatch(string origin) =>
        Assert.False(CorsOriginPolicy.IsAllowedOrigin(origin, AllowList), origin);

    [Fact]
    public void NullOrigin_NeverMatches() =>
        Assert.False(CorsOriginPolicy.IsAllowedOrigin(null, AllowList));

    [Fact]
    public void NullAllowList_NeverMatches() =>
        Assert.False(CorsOriginPolicy.IsAllowedOrigin("http://localhost:3000", null));

    [Fact]
    public void NormalizesTrailingSlashCaseAndWhitespace()
    {
        Assert.True(CorsOriginPolicy.IsAllowedOrigin("  HTTP://LOCALHOST:3000/  ", AllowList));
        Assert.True(CorsOriginPolicy.IsAllowedOrigin("https://Java-House.Punched.APP", AllowList));
    }

    [Fact]
    public void BareWildcardEntry_IsIgnored()
    {
        // A "*" entry would otherwise make the strict list meaningless.
        Assert.False(CorsOriginPolicy.IsAllowedOrigin("https://anything.test", new[] { "*" }));
    }

    [Fact]
    public void ConfiguredOrigins_AreRespectedInsteadOfDefaults()
    {
        var custom = new[] { "https://shop.example.com", "https://*.example.com" };

        Assert.True(CorsOriginPolicy.IsAllowedOrigin("https://shop.example.com", custom));
        Assert.True(CorsOriginPolicy.IsAllowedOrigin("https://branch.example.com", custom));
        // The defaults are not implicitly merged in.
        Assert.False(CorsOriginPolicy.IsAllowedOrigin("http://localhost:3000", custom));
        Assert.False(CorsOriginPolicy.IsAllowedOrigin("https://java-house.punched.app", custom));
    }

    [Fact]
    public void DefaultPolicyName_IsStable()
    {
        // Program.cs registers and applies the policy by this name.
        Assert.Equal("AllowFrontend", CorsOriginPolicy.PolicyName);
    }
}
