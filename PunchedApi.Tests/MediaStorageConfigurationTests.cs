using PunchedApi.Application.Media;

namespace PunchedApi.Tests;

/// <summary>
/// Production-readiness rules for the media object store configuration.
///
/// The API validates these at startup (<c>Program.cs</c>) so a deployment with
/// missing credentials or a committed placeholder fails to boot instead of
/// reporting itself healthy and then failing on the first upload. These tests
/// pin that contract.
/// </summary>
public sealed class MediaStorageConfigurationTests
{
    /// <summary>A complete, plausible R2 configuration.</summary>
    private static MediaStorageOptions R2Options() => new()
    {
        Provider = MediaStorageOptions.ProviderR2,
        AccountId = "account-id",
        AccessKeyId = "access-key-id",
        SecretAccessKey = "secret-access-key",
        PrivateBucket = "punched-private",
        PublicBucket = "punched-public",
        PublicBaseUrl = "https://media.punched.app",
        ServiceUrl = "https://account-id.r2.cloudflarestorage.com"
    };

    [Fact]
    public void R2Provider_AcceptsACompleteConfiguration()
    {
        R2Options().Validate(requireR2: true);
    }

    [Fact]
    public void R2Provider_RejectsMissingCredentials()
    {
        var options = R2Options();
        options.SecretAccessKey = string.Empty;

        var error = Assert.Throws<InvalidOperationException>(() => options.Validate(requireR2: true));
        Assert.Contains("credentials", error.Message, StringComparison.OrdinalIgnoreCase);
        // The message must never echo the secret it rejected.
        Assert.DoesNotContain("secret-access-key", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The values committed in <c>appsettings.json</c>. A placeholder reaching
    /// production is worse than a missing value: every delivered image would 404
    /// while the API reported itself healthy.
    /// </summary>
    [Theory]
    [InlineData("https://media.example.invalid", "https://account-id.r2.cloudflarestorage.com", "PublicBaseUrl")]
    [InlineData("https://media.punched.app", "https://ACCOUNT_ID.r2.cloudflarestorage.com", "ServiceUrl")]
    public void R2Provider_RejectsTheCommittedPlaceholders(string publicBaseUrl, string serviceUrl, string expectedKey)
    {
        var options = R2Options();
        options.PublicBaseUrl = publicBaseUrl;
        options.ServiceUrl = serviceUrl;

        var error = Assert.Throws<InvalidOperationException>(() => options.Validate(requireR2: true));
        Assert.Contains(expectedKey, error.Message, StringComparison.Ordinal);
        Assert.Contains("placeholder", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://media.punched.app", "https://account-id.r2.cloudflarestorage.com")]
    [InlineData("https://media.punched.app", "http://account-id.r2.cloudflarestorage.com")]
    public void R2Provider_RequiresHttpsEndpoints(string publicBaseUrl, string serviceUrl)
    {
        var options = R2Options();
        options.PublicBaseUrl = publicBaseUrl;
        options.ServiceUrl = serviceUrl;

        Assert.Throws<InvalidOperationException>(() => options.Validate(requireR2: true));
    }

    [Fact]
    public void LocalProvider_RequiresAnAbsoluteLocalBaseUrl()
    {
        var options = new MediaStorageOptions
        {
            Provider = MediaStorageOptions.ProviderLocal,
            LocalBaseUrl = string.Empty,
            PublicBaseUrl = "http://localhost:5091/v1/media/delivery"
        };

        var error = Assert.Throws<InvalidOperationException>(() => options.Validate(requireR2: true));
        Assert.Contains("LocalBaseUrl", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalProvider_ValidatesItsOwnRulesAndSkipsTheR2Ones()
    {
        // requireR2: true is what Program.cs passes for both providers; the local
        // branch must return before the R2 credential checks are reached, or a
        // credential-free local run could not start.
        var options = new MediaStorageOptions
        {
            Provider = MediaStorageOptions.ProviderLocal,
            LocalBaseUrl = "http://localhost:5091",
            PublicBaseUrl = "http://localhost:5091/v1/media/delivery"
        };

        options.Validate(requireR2: true);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void PresignMinutes_OutsideTheR2MaximumIsRejected(int minutes)
    {
        // R2/S3 presigned URLs are capped at 7 days, so a longer window would
        // silently produce signatures the store rejects.
        var options = R2Options();
        options.PresignMinutes = minutes;

        Assert.Throws<InvalidOperationException>(() => options.Validate(requireR2: true));
    }

    [Fact]
    public void DeliveryProbeTimeout_OutsideThePermittedRangeIsRejected()
    {
        // The probe bounds how long one stalled delivery URL can hold the worker's
        // sequential batch, so an unusable value must not reach production.
        var tooSmall = R2Options();
        tooSmall.DeliveryProbeTimeoutSeconds = 0;
        Assert.Throws<InvalidOperationException>(() => tooSmall.Validate(requireR2: true));

        var tooLarge = R2Options();
        tooLarge.DeliveryProbeTimeoutSeconds = 121;
        Assert.Throws<InvalidOperationException>(() => tooLarge.Validate(requireR2: true));
    }

    [Fact]
    public void RequireR2False_SkipsTheCloudChecksForUnitScenarios()
    {
        // MediaServiceRelationshipsTests and friends construct options with no
        // credentials; they must stay constructible without a cloud account.
        var options = new MediaStorageOptions { Provider = MediaStorageOptions.ProviderR2 };
        options.Validate(requireR2: false);
    }
}
