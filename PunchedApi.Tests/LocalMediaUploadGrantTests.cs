using Microsoft.Extensions.Options;
using PunchedApi.Application.Media;
using PunchedApi.Infrastructure.Services.Storage;

namespace PunchedApi.Tests;

/// <summary>
/// The filesystem provider's upload grant, which emulates an object-store
/// presigned URL. The properties asserted here are the same ones the R2 grant is
/// expected to have — one key, one content type, one short window — so the two
/// providers cannot drift apart in what a grant authorises.
///
/// The expiry case matters most: a grant that is still accepted after its window
/// is a URL that can be replayed indefinitely.
/// </summary>
public sealed class LocalMediaUploadGrantTests
{
    private static LocalMediaUrlSigner Signer() =>
        new(Options.Create(new MediaStorageOptions { LocalSigningKey = "unit-test-signing-key" }));

    private const string Key = "pending/businesses/abc/def.bin";

    [Fact]
    public void AValidGrant_IsAccepted()
    {
        var signer = Signer();
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        var signature = signer.Sign(Key, "image/jpeg", expires);

        Assert.True(signer.Validate(Key, "image/jpeg", expires.ToUnixTimeSeconds(), signature));
    }

    [Fact]
    public void AnExpiredGrant_IsRejectedEvenWithACorrectSignature()
    {
        var signer = Signer();
        var expires = DateTimeOffset.UtcNow.AddMinutes(-1);
        var signature = signer.Sign(Key, "image/jpeg", expires);

        Assert.False(signer.Validate(Key, "image/jpeg", expires.ToUnixTimeSeconds(), signature));
    }

    [Fact]
    public void AGrantForADifferentContentType_IsRejected()
    {
        var signer = Signer();
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        var signature = signer.Sign(Key, "image/jpeg", expires);

        Assert.False(signer.Validate(Key, "image/png", expires.ToUnixTimeSeconds(), signature));
    }

    [Fact]
    public void AGrantForADifferentKey_IsRejected()
    {
        var signer = Signer();
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        var signature = signer.Sign(Key, "image/jpeg", expires);

        Assert.False(signer.Validate("pending/businesses/abc/other.bin", "image/jpeg", expires.ToUnixTimeSeconds(), signature));
    }

    [Fact]
    public void AGrantForAnotherTenant_IsRejected()
    {
        var signer = Signer();
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        var signature = signer.Sign("pending/businesses/tenant-a/x.bin", "image/jpeg", expires);

        Assert.False(signer.Validate("pending/businesses/tenant-b/x.bin", "image/jpeg", expires.ToUnixTimeSeconds(), signature));
    }

    [Fact]
    public void ATamperedExpiry_IsRejected()
    {
        var signer = Signer();
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        var signature = signer.Sign(Key, "image/jpeg", expires);

        // Pushing the window out invalidates the signature, which covers the expiry.
        Assert.False(signer.Validate(Key, "image/jpeg", expires.AddHours(24).ToUnixTimeSeconds(), signature));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!!")]
    public void AGarbageSignature_IsRejected(string signature)
    {
        var signer = Signer();
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);

        Assert.False(signer.Validate(Key, "image/jpeg", expires.ToUnixTimeSeconds(), signature));
    }

    [Fact]
    public void ContentTypeComparison_IgnoresCaseAndSurroundingWhitespace()
    {
        // The browser sends whatever the client declared; the signed payload is
        // normalised so "IMAGE/JPEG" and " image/jpeg " cannot fail spuriously.
        var signer = Signer();
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        var signature = signer.Sign(Key, "image/jpeg", expires);

        Assert.True(signer.Validate(Key, "IMAGE/JPEG", expires.ToUnixTimeSeconds(), signature));
        Assert.True(signer.Validate(Key, " image/jpeg ", expires.ToUnixTimeSeconds(), signature));
    }
}
