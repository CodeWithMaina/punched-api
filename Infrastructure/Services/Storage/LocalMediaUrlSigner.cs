using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Media;

namespace PunchedApi.Infrastructure.Services.Storage;

/// <summary>
/// Signs and verifies the filesystem provider's upload grants.
///
/// The local provider has no cloud presigner, so it emulates one: the grant URL
/// carries an HMAC over the object key, the declared content type and the expiry.
/// That preserves the property that matters — a URL authorises exactly one key,
/// one content type and a short window, and cannot be forged or replayed outside
/// it — without committing a shared secret to configuration.
/// </summary>
public interface ILocalMediaUrlSigner
{
    string Sign(string key, string contentType, DateTimeOffset expiresAt);
    bool Validate(string key, string contentType, long expiresUnixSeconds, string signature);
}

public sealed class LocalMediaUrlSigner : ILocalMediaUrlSigner
{
    private readonly byte[] _key;

    public LocalMediaUrlSigner(IOptions<MediaStorageOptions> options)
    {
        var configured = options.Value.LocalSigningKey;
        // A random per-process key is the safe default: nothing to leak, and a
        // restart merely invalidates outstanding grants (which the client retries).
        _key = string.IsNullOrWhiteSpace(configured)
            ? RandomNumberGenerator.GetBytes(32)
            : Encoding.UTF8.GetBytes(configured);
    }

    public string Sign(string key, string contentType, DateTimeOffset expiresAt)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(Payload(key, contentType, expiresAt.ToUnixTimeSeconds())));
        return WebEncoders.Base64UrlEncode(mac);
    }

    public bool Validate(string key, string contentType, long expiresUnixSeconds, string signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresUnixSeconds) return false;

        byte[] provided;
        try { provided = WebEncoders.Base64UrlDecode(signature); }
        catch (FormatException) { return false; }

        var expected = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(Payload(key, contentType, expiresUnixSeconds)));
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private static string Payload(string key, string contentType, long expiresUnixSeconds) =>
        $"{key}\n{contentType.Trim().ToLowerInvariant()}\n{expiresUnixSeconds}";
}
