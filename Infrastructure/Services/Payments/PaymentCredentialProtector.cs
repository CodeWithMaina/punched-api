using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Settings;

namespace PunchedApi.Application.Services;

/// <summary>
/// AES-GCM encryption of business Daraja credentials at rest. The 32-byte key comes
/// from Payments:CredentialEncryptionKey (base64). When the key is not configured:
/// dev/test falls back to a deterministic unprotected prefix (so local development
/// works); PRODUCTION refuses to store credentials (fail closed).
/// Encrypted values are never logged and never returned to the browser.
/// </summary>
public sealed class PaymentCredentialProtector
{
    private const string DevPrefix = "dev-unprotected:";

    private readonly byte[]? _key;
    private readonly bool _isProduction;

    public PaymentCredentialProtector(IOptions<PaymentOptions> options, IHostEnvironment environment)
    {
        _isProduction = environment.IsProduction();
        var raw = options.Value.CredentialEncryptionKey;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try { _key = Convert.FromBase64String(raw); } catch (FormatException) { _key = null; }
        }
    }

    /// <summary>Encrypt a credential value. Returns null for null/empty input.</summary>
    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;

        if (_key is not { Length: 32 })
        {
            if (_isProduction)
                throw new InvalidOperationException(
                    "Payments:CredentialEncryptionKey must be configured (base64 32 bytes) before storing payment credentials in production.");
            return DevPrefix + plaintext;
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length + ciphertext.Length, tag.Length);
        return Convert.ToBase64String(result);
    }

    /// <summary>Decrypt a stored credential. Returns null when the value is missing/corrupt.</summary>
    public string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;

        if (stored.StartsWith(DevPrefix, StringComparison.Ordinal))
            return stored[DevPrefix.Length..];

        if (_key is not { Length: 32 }) return null;

        try
        {
            var bytes = Convert.FromBase64String(stored);
            var nonce = bytes[..12];
            var tag = bytes[^16..];
            var ciphertext = bytes[12..^16];
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
