using PunchedApi.Application.DTOs;
using PunchedApi.Application.Validators;

namespace PunchedApi.Tests;

/// <summary>
/// POST /v1/auth/refresh-token accepts a request with NO body token.
///
/// <para><b>Regression.</b> <c>RefreshTokenRequestValidator</c> required
/// <c>refreshToken</c> to be non-empty, but the controller deliberately falls
/// back to the shared cross-subdomain session cookie
/// (<c>body.RefreshToken ?? sessionCookie</c>). Model validation runs before the
/// action, so every cookie-only hydration — the whole point of Phase 2, "sign in
/// on the root, then visit a tenant subdomain" — was rejected with HTTP 400 and
/// each origin looked signed out.</para>
/// </summary>
public class RefreshTokenRequestValidatorTests
{
    private static readonly RefreshTokenRequestValidator Validator = new();

    [Fact]
    public void EmptyBody_IsValid_BecauseTheSharedCookieIsTheSource()
    {
        var result = Validator.Validate(new RefreshTokenRequest());

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void EmptyBody_WithTenantSlug_IsValid()
    {
        // This is exactly what lib/api/client.ts hydrateFromSharedSession sends
        // from a business subdomain: { businessSlug } and nothing else.
        var result = Validator.Validate(new RefreshTokenRequest
        {
            BusinessSlug = "aurelia-luxe-hair-atelier"
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void BodyToken_IsStillAccepted()
    {
        var result = Validator.Validate(new RefreshTokenRequest
        {
            RefreshToken = new string('a', 88),
            BusinessSlug = "java-house"
        });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void OversizedToken_IsRejected()
    {
        var result = Validator.Validate(new RefreshTokenRequest
        {
            RefreshToken = new string('a', 513)
        });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void OversizedSlug_IsRejected()
    {
        var result = Validator.Validate(new RefreshTokenRequest
        {
            BusinessSlug = new string('a', 64)
        });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void NullRequest_Shape_IsValid_SoTheControllerReturns401Not400()
    {
        // The endpoint is declared with a nullable body; the 401 for "neither a
        // body token nor a cookie" must come from the action, not from validation.
        var result = Validator.Validate(new RefreshTokenRequest
        {
            RefreshToken = string.Empty,
            BusinessSlug = null
        });

        Assert.True(result.IsValid);
    }
}
