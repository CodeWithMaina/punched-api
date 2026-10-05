using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace PunchedApi.Application.Assets;

public sealed record CardAssetDeliveryGrant(Guid BusinessId, Guid? CardId, Guid AssetId);

public interface ICardAssetDeliveryTokenService
{
    string CreateUrl(Guid businessId, Guid? cardId, Guid assetId);
    CardAssetDeliveryGrant? ReadGrant(string token);
}

/// <summary>Creates short-lived encrypted URLs for images embedded in rendered card HTML.</summary>
public sealed class CardAssetDeliveryTokenService : ICardAssetDeliveryTokenService
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);
    private readonly IDataProtector _protector;

    public CardAssetDeliveryTokenService(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Punched.CardAssetDelivery.v1");
    }

    public string CreateUrl(Guid businessId, Guid? cardId, Guid assetId)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(TokenLifetime).ToUnixTimeSeconds();
        var payload = $"{businessId:D}|{cardId?.ToString("D") ?? "-"}|{assetId:D}|{expiresAt}";
        var token = Uri.EscapeDataString(_protector.Protect(payload));
        return $"/v1/card-assets/deliver/{token}";
    }

    public CardAssetDeliveryGrant? ReadGrant(string token)
    {
        try
        {
            var values = _protector.Unprotect(Uri.UnescapeDataString(token)).Split('|');
            if (values.Length != 4
                || !Guid.TryParse(values[0], out var businessId)
                || !Guid.TryParse(values[2], out var assetId)
                || !long.TryParse(values[3], out var expiresAt)
                || DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiresAt)
                return null;

            Guid? cardId = values[1] == "-"
                ? null
                : Guid.TryParse(values[1], out var parsedCardId) ? parsedCardId : null;
            if (values[1] != "-" && !cardId.HasValue) return null;

            return new CardAssetDeliveryGrant(businessId, cardId, assetId);
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