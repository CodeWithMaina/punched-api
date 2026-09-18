using System.Security.Cryptography;
using System.Text;

namespace PunchedApi.Application.Attendance;

/// <summary>
/// Single factory for attendance credentials (plan §8.2–§8.3). 32 CSPRNG
/// bytes → base64url (the <c>QrService.GenerateSecureToken</c> pattern),
/// stored only as SHA-256 hex (the <c>QrToken.TokenHash</c> pattern). The
/// payload is namespaced so clients can reject foreign codes pre-network.
/// No business/location id, no PII, no timestamp in the payload.
/// </summary>
public static class AttendanceTokenFactory
{
    /// <summary>Namespaced payload prefix — clients reject foreign QR codes offline.</summary>
    public const string PayloadPrefix = "punched:attendance:v1:";

    /// <summary>
    /// Creates a full printable payload:
    /// <c>punched:attendance:v1:&lt;43-char base64url token&gt;</c>.
    /// Returned ONCE at mint/rotate; only its SHA-256 hex is stored.
    /// </summary>
    public static string CreatePayload() => PayloadPrefix + CreateToken();

    /// <summary>32 random bytes → base64url ('-'/'_', padding trimmed → 43 chars).</summary>
    public static string CreateToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    /// <summary>SHA-256 hex (lowercase) of the raw token — the DB storage form.</summary>
    public static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>True when the payload carries the attendance v1 namespace prefix.</summary>
    public static bool HasValidPrefix(string payload) =>
        payload != null && payload.StartsWith(PayloadPrefix, StringComparison.Ordinal);
}
