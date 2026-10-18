using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Notifications;

/// <summary>
/// Business logic for notification template overrides: CRUD, effective-template
/// resolution, preview with sample payloads, unknown-token rejection, durable
/// version history, pagination, and restore-as-new-version. All writes are
/// tenant-scoped to a single business.
/// </summary>
public sealed class TemplateOverrideService
{
    private static readonly Regex TokenRegex =
        new(@"\{\{([A-Za-z][A-Za-z0-9_]*)\}\}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Link tokens injected by the delivery layer (NotificationService / channel)
    // rather than the producer payload. Accepted as valid in any template.
    private static readonly HashSet<string> GlobalTokens =
        new(StringComparer.OrdinalIgnoreCase) { "BookLink", "BillingLink", "NotebookLink" };

    private readonly ApplicationDbContext _context;
    private readonly ITemplateRenderer _renderer;
    private readonly ITemplateOverrideResolver _resolver;
    private readonly ILogger<TemplateOverrideService> _logger;

    public TemplateOverrideService(
        ApplicationDbContext context,
        ITemplateRenderer renderer,
        ITemplateOverrideResolver resolver,
        ILogger<TemplateOverrideService> logger)
    {
        _context = context;
        _renderer = renderer;
        _resolver = resolver;
        _logger = logger;
    }

    // ══════════════════════════════════════════════════════════════════
    //  List / effective
    // ══════════════════════════════════════════════════════════════════

    public async Task<PaginatedNotificationTemplateOverridesDto> ListOverridesAsync(
        Guid businessId,
        string? type,
        string? channel,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 25;

        var query = _context.NotificationTemplateOverrides
            .AsNoTracking()
            .Where(o => o.BusinessId == businessId);

        if (!string.IsNullOrWhiteSpace(type))
            query = query.Where(o => o.Type == type);
        if (!string.IsNullOrWhiteSpace(channel))
            query = query.Where(o => o.Channel == channel);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(o => o.UpdatedAt ?? o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new NotificationTemplateOverrideDto
            {
                Id = o.Id,
                BusinessId = o.BusinessId,
                Type = o.Type,
                Channel = o.Channel,
                SubjectTemplate = o.SubjectTemplate,
                BodyTemplateHtml = o.BodyTemplateHtml,
                BodyTemplateText = o.BodyTemplateText,
                Version = o.Version,
                UpdatedBy = o.UpdatedBy,
                CreatedAt = o.CreatedAt.ToString("o"),
                UpdatedAt = (o.UpdatedAt ?? o.CreatedAt).ToString("o"),
                IsDefault = false,
            })
            .ToListAsync(ct);

        return new PaginatedNotificationTemplateOverridesDto
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            Total = total,
            TotalPages = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    /// <summary>Resolves the effective template for one (business, type, channel).</summary>
    public async Task<EffectiveTemplateDto?> GetEffectiveAsync(
        Guid? businessId,
        string type,
        string channel,
        CancellationToken ct = default)
    {
        var definition = NotificationTypes.TryGet(type, out var def);
        if (!definition)
            return null;

        var effective = await _resolver.ResolveAsync(businessId, type, channel, ct);
        if (effective is null)
            return null;

        var overrideRow = businessId is not null && businessId != Guid.Empty
            ? await _context.NotificationTemplateOverrides
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.BusinessId == businessId.Value && o.Type == type && o.Channel == channel, ct)
            : null;

        return new EffectiveTemplateDto
        {
            Type = type,
            Channel = channel,
            Source = effective.Source,
            Subject = effective.Subject,
            Title = effective.Title,
            Body = effective.Body,
            BodyHtml = effective.BodyHtml,
            OverrideId = overrideRow?.Id,
            OverrideVersion = overrideRow?.Version,
        };
    }
    // ══════════════════════════════════════════════════════════════════
    //  CRUD
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Creates an override (version 1). Rejects unknown tokens.</summary>
    public async Task<NotificationTemplateOverrideDto> CreateOverrideAsync(
        Guid businessId,
        UpsertNotificationTemplateOverrideRequest request,
        CancellationToken ct = default)
    {
        var definition = NotificationTypes.Get(request.Type);
        ValidateTokens(request, definition);

        var existing = await _context.NotificationTemplateOverrides
            .AnyAsync(o => o.BusinessId == businessId && o.Type == request.Type && o.Channel == request.Channel, ct);
        if (existing)
            throw new InvalidOperationException($"An override already exists for {request.Type}/{request.Channel} on business {businessId}.");

        var now = DateTime.UtcNow;
        var row = new NotificationTemplateOverride
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Type = request.Type,
            Channel = request.Channel,
            SubjectTemplate = request.SubjectTemplate,
            BodyTemplateHtml = request.BodyTemplateHtml,
            BodyTemplateText = request.BodyTemplateText,
            Version = 1,
            UpdatedBy = request.UpdatedBy,
            CreatedAt = now,
        };
        _context.NotificationTemplateOverrides.Add(row);

        await WriteVersionAsync(row, 1, request.UpdatedBy, ct);
        await _context.SaveChangesAsync(ct);

        return ToDto(row);
    }

    /// <summary>Updates an override. Increments version, snapshots prior version.</summary>
    public async Task<NotificationTemplateOverrideDto> UpdateOverrideAsync(
        Guid businessId,
        Guid overrideId,
        UpsertNotificationTemplateOverrideRequest request,
        CancellationToken ct = default)
    {
        var row = await _context.NotificationTemplateOverrides
            .FirstOrDefaultAsync(o => o.Id == overrideId && o.BusinessId == businessId, ct);
        if (row is null)
            throw new KeyNotFoundException($"Override {overrideId} not found for business {businessId}.");

        var definition = NotificationTypes.Get(request.Type);
        ValidateTokens(request, definition);

        // Snapshot the current version before mutating.
        await WriteVersionAsync(row, row.Version, row.UpdatedBy, ct);

        row.Type = request.Type;
        row.Channel = request.Channel;
        row.SubjectTemplate = request.SubjectTemplate;
        row.BodyTemplateHtml = request.BodyTemplateHtml;
        row.BodyTemplateText = request.BodyTemplateText;
        row.Version = row.Version + 1;
        row.UpdatedBy = request.UpdatedBy;

        await WriteVersionAsync(row, row.Version, request.UpdatedBy, ct);
        await _context.SaveChangesAsync(ct);

        return ToDto(row);
    }

    /// <summary>Deletes an override. Future deliveries fall back to platform default.</summary>
    public async Task<bool> DeleteOverrideAsync(Guid businessId, Guid overrideId, CancellationToken ct = default)
    {
        var row = await _context.NotificationTemplateOverrides
            .FirstOrDefaultAsync(o => o.Id == overrideId && o.BusinessId == businessId, ct);
        if (row is null)
            return false;

        _context.NotificationTemplateOverrides.Remove(row);
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted template override {OverrideId} for business {BusinessId}", overrideId, businessId);
        return true;
    }

    // ══════════════════════════════════════════════════════════════════
    //  Preview
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Renders a template with a sample payload and reports unknown tokens.</summary>
    public PreviewTemplateResponse PreviewTemplate(PreviewTemplateRequest request)
    {
        var unknownTokens = CollectUnknownTokens(
            request.SubjectTemplate, request.BodyTemplateHtml, request.BodyTemplateText);

        var renderedSubject = request.SubjectTemplate is not null
            ? _renderer.RenderText(request.SubjectTemplate, request.Payload)
            : string.Empty;
        var renderedBody = _renderer.RenderText(request.BodyTemplateText, request.Payload);
        var renderedHtml = _renderer.RenderHtml(request.BodyTemplateHtml, request.Payload);
        var renderedTitle = string.IsNullOrWhiteSpace(request.SubjectTemplate)
            ? string.Empty
            : request.SubjectTemplate;

        return new PreviewTemplateResponse
        {
            Subject = renderedSubject,
            Title = renderedTitle,
            Body = renderedBody,
            BodyHtml = renderedHtml,
            UnknownTokens = unknownTokens,
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Version history / restore
    // ═══════════════════════════════════════════════════════════════════

    public async Task<List<TemplateOverrideVersionDto>> ListVersionsAsync(
        Guid businessId,
        Guid overrideId,
        CancellationToken ct = default)
    {
        // Verify ownership first.
        var owned = await _context.NotificationTemplateOverrides
            .AsNoTracking()
            .AnyAsync(o => o.Id == overrideId && o.BusinessId == businessId, ct);
        if (!owned)
            return new();

        return await _context.NotificationTemplateOverrideVersions
            .AsNoTracking()
            .Where(v => v.OverrideId == overrideId)
            .OrderByDescending(v => v.Version)
            .Select(v => new TemplateOverrideVersionDto
            {
                Version = v.Version,
                SubjectTemplate = v.SubjectTemplate,
                BodyTemplateHtml = v.BodyTemplateHtml,
                BodyTemplateText = v.BodyTemplateText,
                UpdatedBy = v.UpdatedBy,
                CreatedAt = v.CreatedAt.ToString("o"),
            })
            .ToListAsync(ct);
    }

    /// <summary>Restores a historical version as a new (current) version.</summary>
    public async Task<NotificationTemplateOverrideDto> RestoreVersionAsync(
        Guid businessId,
        Guid overrideId,
        int version,
        string updatedBy,
        CancellationToken ct = default)
    {
        var row = await _context.NotificationTemplateOverrides
            .FirstOrDefaultAsync(o => o.Id == overrideId && o.BusinessId == businessId, ct);
        if (row is null)
            throw new KeyNotFoundException($"Override {overrideId} not found for business {businessId}.");

        var historical = await _context.NotificationTemplateOverrideVersions
            .FirstOrDefaultAsync(v => v.OverrideId == overrideId && v.Version == version, ct);
        if (historical is null)
            throw new KeyNotFoundException($"Version {version} not found for override {overrideId}.");

        var definition = NotificationTypes.Get(historical.Type);
        var tmpRequest = new UpsertNotificationTemplateOverrideRequest
        {
            Channel = historical.Channel,
            SubjectTemplate = historical.SubjectTemplate,
            BodyTemplateHtml = historical.BodyTemplateHtml,
            BodyTemplateText = historical.BodyTemplateText,
            UpdatedBy = updatedBy,
        };
        ValidateTokens(tmpRequest, definition);

        await WriteVersionAsync(row, row.Version, row.UpdatedBy, ct);

        row.Type = historical.Type;
        row.Channel = historical.Channel;
        row.SubjectTemplate = historical.SubjectTemplate;
        row.BodyTemplateHtml = historical.BodyTemplateHtml;
        row.BodyTemplateText = historical.BodyTemplateText;
        row.Version = row.Version + 1;
        row.UpdatedBy = updatedBy;

        await WriteVersionAsync(row, row.Version, updatedBy, ct);
        await _context.SaveChangesAsync(ct);

        return ToDto(row);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Private helpers
    // ═══════════════════════════════════════════════════════════════════

    private static void ValidateTokens(
        UpsertNotificationTemplateOverrideRequest request,
        NotificationTypeDef definition)
    {
        var validTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in definition.PayloadSchema.Required) validTokens.Add(key);
        foreach (var key in definition.PayloadSchema.Optional) validTokens.Add(key);
        foreach (var token in GlobalTokens) validTokens.Add(token);

        var unknown = CollectUnknownTokens(
            request.SubjectTemplate, request.BodyTemplateHtml, request.BodyTemplateText)
            .Where(t => !validTokens.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"Template references unknown token(s): {string.Join(", ", unknown)}. " +
                $"Allowed tokens for {request.Type}: {string.Join(", ", validTokens.OrderBy(x => x))}.");
        }
    }

    private static List<string> CollectUnknownTokens(
        string? subject,
        string html,
        string text)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in new[] { subject, html, text })
        {
            if (string.IsNullOrWhiteSpace(template)) continue;
            foreach (Match m in TokenRegex.Matches(template))
                found.Add(m.Groups[1].Value);
        }

        return found.OrderBy(x => x).ToList();
    }

    private async Task WriteVersionAsync(
        NotificationTemplateOverride row,
        int version,
        string updatedBy,
        CancellationToken ct)
    {
        _context.NotificationTemplateOverrideVersions.Add(new NotificationTemplateOverrideVersion
        {
            Id = Guid.NewGuid(),
            OverrideId = row.Id,
            BusinessId = row.BusinessId,
            Type = row.Type,
            Channel = row.Channel,
            SubjectTemplate = row.SubjectTemplate,
            BodyTemplateHtml = row.BodyTemplateHtml,
            BodyTemplateText = row.BodyTemplateText,
            Version = version,
            UpdatedBy = updatedBy,
            CreatedAt = DateTime.UtcNow,
        });
    }

    private static NotificationTemplateOverrideDto ToDto(NotificationTemplateOverride o) =>
        new()
        {
            Id = o.Id,
            BusinessId = o.BusinessId,
            Type = o.Type,
            Channel = o.Channel,
            SubjectTemplate = o.SubjectTemplate,
            BodyTemplateHtml = o.BodyTemplateHtml,
            BodyTemplateText = o.BodyTemplateText,
            Version = o.Version,
            UpdatedBy = o.UpdatedBy,
            CreatedAt = o.CreatedAt.ToString("o"),
            UpdatedAt = (o.UpdatedAt ?? o.CreatedAt).ToString("o"),
            IsDefault = false,
        };
}

