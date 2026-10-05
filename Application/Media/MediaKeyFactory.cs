using Microsoft.Extensions.Options;
using PunchedApi.Application.Media;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Media;

public interface IMediaKeyFactory
{
    string CreatePendingKey(Domain.Entities.Media media);
    string CreateDeliveryKey(Guid mediaId, string recipe, int width, string extension);
}

public sealed class MediaKeyFactory : IMediaKeyFactory
{
    public string CreatePendingKey(Domain.Entities.Media media)
    {
        var scope = media.BusinessId.HasValue ? $"businesses/{media.BusinessId.Value:N}" : $"users/{media.OwnerUserId!.Value:N}";
        return $"pending/{scope}/{media.Id:N}.bin";
    }

    public string CreateDeliveryKey(Guid mediaId, string recipe, int width, string extension)
    {
        if (string.IsNullOrWhiteSpace(recipe) || recipe.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Invalid media recipe.", nameof(recipe));
        if (width < 1 || extension is not ("webp" or "jpg")) throw new ArgumentOutOfRangeException(nameof(width));
        return $"delivery/{mediaId:N}/{recipe}-{width}w.{extension}";
    }
}

public interface IMediaUrlFactory
{
    string CreatePublicUrl(string deliveryKey);
    string? TryGetDeliveryKey(string publicUrl);
}

public sealed class MediaUrlFactory(IOptions<MediaStorageOptions> options) : IMediaUrlFactory
{
    public string CreatePublicUrl(string deliveryKey)
    {
        if (string.IsNullOrWhiteSpace(deliveryKey) || deliveryKey.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Invalid delivery key.", nameof(deliveryKey));
        return $"{options.Value.PublicBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(deliveryKey).Replace("%2F", "/")}";
    }

    public string? TryGetDeliveryKey(string publicUrl)
    {
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        var baseUri = options.Value.PublicBaseUrl.TrimEnd('/') + "/";
        if (!publicUrl.StartsWith(baseUri, StringComparison.OrdinalIgnoreCase)) return null;
        var key = Uri.UnescapeDataString(publicUrl[baseUri.Length..]);
        return key.StartsWith("delivery/", StringComparison.Ordinal) && !key.Contains("..", StringComparison.Ordinal) ? key : null;
    }
}
