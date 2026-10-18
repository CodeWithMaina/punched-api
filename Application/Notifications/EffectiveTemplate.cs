namespace PunchedApi.Application.Notifications;

/// <summary>
/// The resolved template for a (business, type, channel) tuple, with a flag
/// indicating whether the content came from a business override or the
/// platform default. Callers use <see cref="Source"/> to decide audit/logging
/// but always render through <see cref="ITemplateRenderer"/> so unknown tokens
/// degrade safely (empty text) rather than failing the notification.
/// </summary>
public sealed record EffectiveTemplate(
    string? Subject,
    string Title,
    string Body,
    string BodyHtml,
    string Source); // "override" | "default"

/// <summary>Source values for <see cref="EffectiveTemplate"/>.</summary>
public static class EffectiveTemplateSource
{
    public const string Override = "override";
    public const string Default = "default";
}

/// <summary>Resolves the effective template for one delivery, consulting the
/// business override table before falling back to the platform default.</summary>
public interface ITemplateOverrideResolver
{
    /// <summary>
    /// Resolves the effective template. Returns the business override when one
    /// exists for (businessId, type, channel); otherwise the platform default
    /// from <see cref="NotificationTemplates"/>. Returns null when the type is
    /// unregistered (caller should treat as a permanent dead-letter).
    /// </summary>
    Task<EffectiveTemplate?> ResolveAsync(
        Guid? businessId,
        string type,
        string channel,
        CancellationToken ct = default);
}
