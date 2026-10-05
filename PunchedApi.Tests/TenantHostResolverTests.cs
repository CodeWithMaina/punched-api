using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using PunchedApi.API.Middleware;
using PunchedApi.Application.Services;
using PunchedApi.Application.Settings;

namespace PunchedApi.Tests;

/// <summary>
/// Host ↔ tenant parsing (Phase 5 / risk R4): the host decides which tenant
/// PAGE is shown — it is never an authorization input, but it must parse
/// exactly like the frontend's <c>parseTenantSlug</c> so the two sides never
/// disagree about which address carries a tenant.
/// </summary>
public class TenantHostResolverTests
{
    private static TenantHostResolver Create(string baseUrl, string? rootDomain = null) =>
        new(
            new Mock<IServiceScopeFactory>().Object,
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new PublicAppSettings
            {
                BaseUrl = baseUrl,
                RootDomain = rootDomain ?? string.Empty
            }));

    [Theory]
    // Production root: punched.app
    [InlineData("java-house.punched.app", "java-house")]
    [InlineData("JAVA-HOUSE.PUNCHED.APP", "java-house")]
    [InlineData("java-house.punched.app:443", "java-house")]
    // Reserved / platform surfaces
    [InlineData("punched.app", null)]
    [InlineData("www.punched.app", null)]
    [InlineData("admin.punched.app", null)]
    [InlineData("api.punched.app", null)]
    [InlineData("dashboard.punched.app", null)]
    // Multi-level labels are infrastructure, never a tenant
    [InlineData("a.b.punched.app", null)]
    // Unrelated domains
    [InlineData("java-house.evil.com", null)]
    [InlineData("evil.com", null)]
    // Degenerate input
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("[::1]:3000", null)]
    [InlineData(".punched.app", null)]
    public void ParseSlug_ProductionRoot(string host, string? expected) =>
        Assert.Equal(expected, Create("https://punched.app").ParseSlug(host));

    [Theory]
    [InlineData("java-house.localhost:3000", "java-house")]
    [InlineData("java-house.localhost", "java-house")]
    [InlineData("localhost:3000", null)]
    [InlineData("java-house.localhost.evil.com", null)]
    public void ParseSlug_LocalDevHosts(string host, string? expected) =>
        Assert.Equal(expected, Create("http://localhost:3000").ParseSlug(host));

    [Fact]
    public void ParseSlug_NullInput_IsPlatformContext()
    {
        var resolver = Create("https://punched.app");
        Assert.Null(resolver.ParseSlug(null));
    }

    [Fact]
    public void ParseSlug_UsesConfiguredRootDomain_WhenSet()
    {
        var resolver = Create("https://example.com", rootDomain: "punched.app");

        Assert.Equal("java-house", resolver.ParseSlug("java-house.punched.app"));
        Assert.Null(resolver.ParseSlug("java-house.example.com"));
    }

    [Fact]
    public async Task ResolveBusinessIdAsync_NullOrInvalidSlug_IsNull_WithoutTouchingTheDatabase()
    {
        // The scope factory is a strict mock: any DB access would throw —
        // proving the guard clauses short-circuit before resolution.
        var resolver = Create("https://punched.app");

        Assert.Null(await resolver.ResolveBusinessIdAsync(null));
        Assert.Null(await resolver.ResolveBusinessIdAsync(""));
        Assert.Null(await resolver.ResolveBusinessIdAsync("Not A Slug"));
    }
}