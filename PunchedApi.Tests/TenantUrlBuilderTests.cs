using Microsoft.Extensions.Options;
using PunchedApi.Application.Services;
using PunchedApi.Application.Settings;

namespace PunchedApi.Tests;

/// <summary>
/// Tenant URL building — java-house.punched.app URLs used by share links,
/// referral links and the admin Business URL surface. The root domain is
/// configuration (PublicApp:RootDomain / BaseUrl), never hardcoded, and a URL
/// is only produced for a slug that passes BusinessSlugPolicy (never fabricated).
/// </summary>
public class TenantUrlBuilderTests
{
    private static TenantUrlBuilder Create(string baseUrl, string? rootDomain = null) =>
        new(Options.Create(new PublicAppSettings
        {
            BaseUrl = baseUrl,
            RootDomain = rootDomain ?? string.Empty
        }));

    [Fact]
    public void BuildForSlug_LocalDev_UsesBaseHostAndPort()
    {
        var builder = Create("http://localhost:3000");

        Assert.Equal("localhost", builder.RootDomain);
        Assert.Equal(
            "http://java-house.localhost:3000/refer/ABC123",
            builder.BuildForSlug("java-house", "/refer/ABC123"));
    }

    [Fact]
    public void BuildForSlug_Production_UsesRootDomain_NoPort()
    {
        var builder = Create("https://punched.app");

        Assert.Equal("punched.app", builder.RootDomain);
        Assert.Equal("https://java-house.punched.app/", builder.BuildForSlug("java-house", "/"));
        Assert.Equal("https://java-house.punched.app", builder.BuildForSlug("java-house"));
        Assert.Equal(
            "https://java-house.punched.app/dashboard",
            builder.BuildForSlug("java-house", "dashboard")); // path normalized
    }

    [Fact]
    public void BuildForSlug_ConfiguredRootDomain_Wins_AndIsNormalized()
    {
        // Trailing dot + case are tolerated; the emitted host never is.
        var builder = Create("https://example.com", rootDomain: ".Punched.APP.");

        Assert.Equal("punched.app", builder.RootDomain);
        Assert.Equal("https://java-house.punched.app/x", builder.BuildForSlug("java-house", "/x"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Not A Slug")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("has_underscore")]
    public void BuildForSlug_InvalidSlugs_ProduceNothing(string? slug)
    {
        var builder = Create("https://punched.app");

        Assert.Null(builder.BuildForSlug(slug, "/refer/x"));
    }

    [Fact]
    public void BuildRoot_ProducesPlatformRootUrl()
    {
        var builder = Create("https://punched.app");

        Assert.Equal("https://punched.app", builder.BuildRoot());
        Assert.Equal("https://punched.app/login", builder.BuildRoot("/login"));
        Assert.Equal("https://punched.app/login", builder.BuildRoot("login"));
    }

    [Fact]
    public void BuildForSlug_IsCaseInsensitiveOnInput()
    {
        var builder = Create("https://punched.app");

        Assert.Equal("https://java-house.punched.app", builder.BuildForSlug("Java-House"));
    }
}