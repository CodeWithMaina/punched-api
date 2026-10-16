using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace PunchedApi.Application.Notifications;

public sealed record UnsubscribeGrant(Guid UserId, Guid? BusinessId);

public interface IUnsubscribeTokenService
{
    string CreateToken(Guid userId, Guid? businessId);
    UnsubscribeGrant? ReadGrant(string token);
}

/// <summary>
/// Creates long-lived encrypted unsubscribe tokens minted at email-render time
/// (Phase 4). Mirrors <c>CardAssetDeliveryTokenService</c>: the payload carries an
/// embedded expiry, and only the encrypted token travels through the email — no
/// raw user identifiers appear in marketing mail headers or links.
/// </summary>
/// <remarks>
/// Lifetime is deliberately long (1 year): unsubscribe links must keep working
/// long after the send, and the action itself is idempotent and only ever
/// narrows a preference (marketing off), so replay of a captured token is a
/// nuisance at worst, never a privilege gain.
/// </remarks>
public sealed class UnsubscribeTokenService : IUnsubscribeTokenService
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(365);
    private readonly IDataProtector _protector;

    public UnsubscribeTokenService(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Punched.Unsubscribe.v1");
    }

    public string CreateToken(Guid userId, Guid? businessId)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(TokenLifetime).ToUnixTimeSeconds();
        var business = businessId.HasValue ? businessId.Value.ToString("D") : "-";
        var payload = $"{userId:D}|{business}|{expiresAt}";
        return Uri.EscapeDataString(_protector.Protect(payload));
    }

    public UnsubscribeGrant? ReadGrant(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var values = _protector.Unprotect(Uri.UnescapeDataString(token)).Split('|');
            if (values.Length != 3
                || !Guid.TryParse(values[0], out var userId)
                || !long.TryParse(values[2], out var expiresAt)
                || DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiresAt)
                return null;

            Guid? businessId = values[1] == "-"
                ? null
                : Guid.TryParse(values[1], out var parsedBusinessId) ? parsedBusinessId : null;
            if (values[1] != "-" && !businessId.HasValue) return null;

            return new UnsubscribeGrant(userId, businessId);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
