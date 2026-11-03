using System.Security.Cryptography;
using System.Text;

namespace PunchedApi.Application.Services;

/// <summary>
/// The one-time-code primitives used to verify an identity by proving control of
/// an inbox.
///
/// <para>Codes are six digits from a cryptographic RNG and are stored only as a
/// SHA-256 hash, so a database read cannot reveal a live code. The same scheme
/// the account-verification flow uses, kept in one place so a guest booking and a
/// registration cannot drift apart.</para>
/// </summary>
public static class OneTimeCode
{
    /// <summary>A uniformly distributed six-digit code, `000000`–`999999`.</summary>
    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        // Unsigned modulo: `Math.Abs(int.MinValue)` would overflow, and a signed
        // value would bias the distribution.
        var value = BitConverter.ToUInt32(bytes) % 1_000_000;
        return value.ToString("D6");
    }

    /// <summary>SHA-256 of the code, base64-encoded — what the database stores.</summary>
    public static string Hash(string code)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        return Convert.ToBase64String(hash);
    }

    /// <summary>Constant-time comparison of a presented code against a stored hash.</summary>
    public static bool Matches(string presented, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;
        var candidate = Encoding.UTF8.GetBytes(Hash(presented));
        var stored = Encoding.UTF8.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(candidate, stored);
    }
}
