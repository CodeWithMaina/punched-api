using System.Reflection;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Controllers;

namespace PunchedApi.Tests;

/// <summary>
/// Rate-limit posture for the auth surface.
///
/// <para><b>Regression.</b> The whole controller used to carry
/// <c>[EnableRateLimiting("login")]</c>, so <c>refresh-token</c> shared the
/// 5-per-30-minutes credential bucket. With the shared cross-subdomain session
/// every origin hydrates once per fresh load, which meant signing in on the root
/// and then visiting a handful of business subdomains locked the user — and the
/// login form itself — out with 429s. The tenant storefront also probes the
/// endpoint anonymously on every visit.</para>
///
/// <para>Credential endpoints keep the tight budget; session refresh gets its
/// own generous one; the limit is now declared per action so the distinction
/// cannot silently regress.</para>
/// </summary>
public class AuthRateLimitPostureTests
{
    private static readonly Type Controller = typeof(AuthController);

    /// <summary>Actions that must keep the brute-force "login" bucket.</summary>
    private static readonly string[] CredentialActions =
    {
        "Register", "RegisterBusiness", "Login", "ForgotPassword", "ResetPassword", "ChangePassword",
    };

    /// <summary>Actions that must keep the tight "otp" bucket.</summary>
    private static readonly string[] OtpActions = { "VerifyEmail", "RequestEmail" };

    private static string? PolicyOf(string actionName)
    {
        var method = Controller.GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"AuthController.{actionName} not found");

        return method.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName;
    }

    [Fact]
    public void Controller_DoesNotImposeOneBucketOnEveryAction()
    {
        Assert.Null(Controller.GetCustomAttribute<EnableRateLimitingAttribute>());
    }

    [Theory]
    [InlineData("Register")]
    [InlineData("RegisterBusiness")]
    [InlineData("Login")]
    [InlineData("ForgotPassword")]
    [InlineData("ResetPassword")]
    [InlineData("ChangePassword")]
    public void CredentialActions_KeepTheLoginPolicy(string action) =>
        Assert.Equal("login", PolicyOf(action));

    [Theory]
    [InlineData("VerifyEmail")]
    [InlineData("RequestEmail")]
    public void CodeRequestActions_KeepTheOtpPolicy(string action) =>
        Assert.Equal("otp", PolicyOf(action));

    [Fact]
    public void RefreshToken_HasItsOwnPolicy_NotTheLoginBucket()
    {
        var policy = PolicyOf("RefreshToken");

        Assert.Equal("refresh", policy);
        Assert.NotEqual("login", policy);
    }

    [Fact]
    public void EveryLimitedAuthAction_IsInTheDocumentedMatrix()
    {
        var limited = Controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<EnableRateLimitingAttribute>() != null)
            .Select(m => m.Name)
            .ToHashSet();

        var documented = CredentialActions.Concat(OtpActions).Append("RefreshToken").ToHashSet();

        Assert.Equal(documented.OrderBy(x => x), limited.OrderBy(x => x));
    }
}
