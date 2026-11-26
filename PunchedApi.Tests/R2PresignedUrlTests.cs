using System.Reflection;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Media;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Services.Storage;

namespace PunchedApi.Tests;

/// <summary>
/// What the R2 presigned upload grant actually contains.
///
/// Presigning is a local signature computation — the AWS SDK never contacts the
/// bucket — so these tests exercise the real signing code with dummy credentials
/// and no network. They prove the grant is scoped to one bucket, one object key,
/// one content type and one expiry window.
///
/// They do NOT prove that R2 accepts the signature: that needs live credentials
/// and is covered by <c>scripts/media-upload-e2e.ps1</c> run against a real
/// account. No mock of R2's HTTP responses is used here or anywhere else as
/// evidence of production readiness.
/// </summary>
public sealed class R2PresignedUrlTests
{
    private const string ServiceUrl = "https://account-id.r2.cloudflarestorage.com";

    private static MediaStorageOptions StorageOptions() => new()
    {
        Provider = MediaStorageOptions.ProviderR2,
        AccountId = "account-id",
        AccessKeyId = "test-access-key-id",
        SecretAccessKey = "test-secret-access-key",
        PrivateBucket = "punched-private",
        PublicBucket = "punched-public",
        PublicBaseUrl = "https://media.punched.app",
        ServiceUrl = ServiceUrl
    };

    private static R2ObjectStore CreateStore() => new(Options.Create(StorageOptions()));

    /// <summary>
    /// Pins the AWS SDK's signing clock for the lifetime of the returned handle.
    ///
    /// <para>The presigner derives <c>X-Amz-Expires</c> from the difference between
    /// the requested deadline and its own "now". Without a pinned clock, two calls a
    /// few milliseconds apart can legitimately produce 299 and 300 seconds, which
    /// makes a same-input determinism assertion depend on execution timing rather
    /// than on the signing code. Pinning the clock keeps the assertion exact and
    /// actually stronger: the expiry window itself becomes assertable.</para>
    ///
    /// <para>Scoped to this test class, which xUnit runs sequentially; no other
    /// suite presigns. Restored by <see cref="IDisposable"/> even on failure.</para>
    /// </summary>
    private static IDisposable PinAwsSigningClock(DateTime utcNow)
    {
        var field = typeof(Amazon.AWSConfigs).GetField(
            "utcNowSource", BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(field);

        var original = (Func<DateTime>?)field!.GetValue(null);
        field.SetValue(null, (Func<DateTime>)(() => utcNow));

        return new ClockScope(field, original);
    }

    private sealed class ClockScope : IDisposable
    {
        private readonly FieldInfo _field;
        private readonly Func<DateTime>? _original;

        public ClockScope(FieldInfo field, Func<DateTime>? original)
        {
            _field = field;
            _original = original;
        }

        public void Dispose() => _field.SetValue(null, _original);
    }

    /// <summary>Dependency-free query parser so the test asserts on the real signed URL.</summary>
    private static Dictionary<string, string> Query(Uri url)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index < 0) continue;
            result[Uri.UnescapeDataString(pair[..index])] = Uri.UnescapeDataString(pair[(index + 1)..]);
        }
        return result;
    }

    [Fact]
    public async Task Grant_TargetsTheBucketAndExactObjectKey()
    {
        using var store = CreateStore();
        const string key = "pending/businesses/abc123/def456.bin";

        var grant = await store.PresignPutAsync(
            ObjectStoreBucket.PrivateSource, key, "image/jpeg",
            DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        // Virtual-hosted addressing: the bucket is the subdomain, not a path prefix.
        Assert.Equal("punched-private.account-id.r2.cloudflarestorage.com", grant.Url.Host);
        Assert.Contains(key, grant.Url.AbsolutePath, StringComparison.Ordinal);
        Assert.StartsWith("https://", grant.Url.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grant_PublicDeliveryBucket_IsADifferentBucketFromThePrivateSource()
    {
        using var store = CreateStore();

        var source = await store.PresignPutAsync(
            ObjectStoreBucket.PrivateSource, "pending/x.bin", "image/jpeg",
            DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);
        var delivery = await store.PresignPutAsync(
            ObjectStoreBucket.PublicDelivery, "delivery/x/recipe-320w.webp", "image/webp",
            DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        Assert.Equal("punched-private.account-id.r2.cloudflarestorage.com", source.Url.Host);
        Assert.Equal("punched-public.account-id.r2.cloudflarestorage.com", delivery.Url.Host);
    }

    [Fact]
    public async Task Grant_BindsTheContentTypeAndExpiry()
    {
        using var store = CreateStore();

        var grant = await store.PresignPutAsync(
            ObjectStoreBucket.PrivateSource, "pending/x.bin", "image/png",
            DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        var query = Query(grant.Url);
        Assert.True(query["X-Amz-Signature"] is { Length: > 16 }, "the grant must carry a signature");
        Assert.False(string.IsNullOrWhiteSpace(query["X-Amz-Credential"]));
        // Content-Type is part of the signed headers, so a PUT declaring a
        // different type is rejected by the store rather than accepted.
        Assert.Contains("content-type", query["X-Amz-SignedHeaders"], StringComparison.OrdinalIgnoreCase);

        var expires = int.Parse(query["X-Amz-Expires"]!);
        Assert.InRange(expires, 290, 301);

        // The caller is told exactly which headers it must send.
        Assert.Equal("image/png", grant.RequiredHeaders["Content-Type"]);
    }

    [Fact]
    public async Task Grant_SignatureIsDeterministicForTheSameKeyAndExpiry_AndVariesPerKey()
    {
        using var store = CreateStore();

        // Pinned clock + fixed deadline: the presigner's window is exactly 300 seconds,
        // so the two same-input grants are byte-identical rather than merely
        // second-adjacent.
        using var clock = PinAwsSigningClock(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var expires = new DateTimeOffset(2026, 1, 1, 12, 5, 0, TimeSpan.Zero);

        var first = await store.PresignPutAsync(
            ObjectStoreBucket.PrivateSource, "pending/a.bin", "image/jpeg", expires, CancellationToken.None);
        var repeat = await store.PresignPutAsync(
            ObjectStoreBucket.PrivateSource, "pending/a.bin", "image/jpeg", expires, CancellationToken.None);
        var other = await store.PresignPutAsync(
            ObjectStoreBucket.PrivateSource, "pending/b.bin", "image/jpeg", expires, CancellationToken.None);

        Assert.Equal("300", Query(first.Url)["X-Amz-Expires"]);
        Assert.Equal(first.Url.Query, repeat.Url.Query);
        Assert.NotEqual(first.Url.Query, other.Url.Query);
    }

    [Fact]
    public async Task Grant_HonoursAShorterExpiryWindow()
    {
        using var store = CreateStore();

        var grant = await store.PresignPutAsync(
            ObjectStoreBucket.PrivateSource, "pending/x.bin", "image/jpeg",
            DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        var expires = int.Parse(Query(grant.Url)["X-Amz-Expires"]!);
        Assert.InRange(expires, 50, 61);
    }

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("/absolute.bin")]
    [InlineData("pending/../../escape.bin")]
    [InlineData("pending\\windows.bin")]
    [InlineData("")]
    public async Task Grant_RefusesAKeyThatCouldEscapeItsPermittedPrefix(string key)
    {
        using var store = CreateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PresignPutAsync(
            ObjectStoreBucket.PrivateSource, key, "image/jpeg",
            DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None));
    }
}
