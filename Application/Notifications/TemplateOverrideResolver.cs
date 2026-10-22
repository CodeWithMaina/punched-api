using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Notifications;

/// <summary>
/// Resolves the effective template for a delivery: business override first,
/// platform default as the safe fallback. Never silently bypasses an override
/// — when an override exists it is used; when it is absent the platform default
/// from <see cref="NotificationTemplates"/> is returned.
/// </summary>
public sealed class TemplateOverrideResolver : ITemplateOverrideResolver
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<TemplateOverrideResolver> _logger;

    public TemplateOverrideResolver(ApplicationDbContext context, ILogger<TemplateOverrideResolver> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<EffectiveTemplate?> ResolveAsync(
        Guid? businessId,
        string type,
        string channel,
        CancellationToken ct = default)
    {
        // Unregistered type: cannot render — signal permanent dead-letter.
        NotificationTemplate platformDefault;
        try
        {
            platformDefault = NotificationTemplates.Get(type, channel);
        }
        catch (ArgumentException)
        {
            return null;
        }

        // No business scope: platform default is the only option.
        if (businessId is null || businessId == Guid.Empty)
            return ToEffective(platformDefault, EffectiveTemplateSource.Default, null, null);

        var overrideRow = await _context.NotificationTemplateOverrides
            .AsNoTracking()
            .FirstOrDefaultAsync(o =>
                o.BusinessId == businessId.Value &&
                o.Type == type &&
                o.Channel == channel, ct);

        if (overrideRow is null)
            return ToEffective(platformDefault, EffectiveTemplateSource.Default, null, null);

        _logger.LogDebug(
            "Using business template override {OverrideId} v{Version} for {Type}/{Channel} business {BusinessId}",
            overrideRow.Id, overrideRow.Version, type, channel, businessId.Value);

        return ToEffective(platformDefault, EffectiveTemplateSource.Override, overrideRow.Id, overrideRow.Version)
            with
            {
                Subject = overrideRow.SubjectTemplate ?? platformDefault.Subject,
                Title = string.IsNullOrWhiteSpace(overrideRow.SubjectTemplate)
                    ? platformDefault.Title
                    : overrideRow.SubjectTemplate,
                Body = overrideRow.BodyTemplateText,
                BodyHtml = overrideRow.BodyTemplateHtml,
            };
    }

    private static EffectiveTemplate ToEffective(
        NotificationTemplate platformDefault,
        string source,
        Guid? overrideId,
        int? overrideVersion)
    {
        return new EffectiveTemplate(
            platformDefault.Subject,
            platformDefault.Title,
            platformDefault.Body,
            platformDefault.Body, // BodyHtml defaults to body; EmailChannel wraps in layout
            source,
            overrideId,
            overrideVersion);
    }
}
