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

    /// <summary>Single override by id, or null when it does not belong to the tenant.</summary>
    public async Task<NotificationTemplateOverrideDto?> GetOverrideAsync(
        Guid businessId,
        Guid overrideId,
        CancellationToken ct = default)
    {
        var row = await _context.NotificationTemplateOverrides
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == overrideId && o.BusinessId == businessId, ct);
        return row is null ? null : ToDto(row);
    }

    /// <summary>Creates an override (version 1). Rejects unknown tokens.</summary>
    public async Task<NotificationTemplateOverrideDto> CreateOverrideAsync(
        Guid businessId,
        UpsertNotificationTemplateOverrideRequest request,
        CancellationToken ct = default)
    {
        var definition = ValidateType(request.Type);
        var channel = NormalizeChannel(request.Channel);
        ValidateTokens(request, definition.Name, definition);

        var existing = await _context.NotificationTemplateOverrides
            .AnyAsync(o => o.BusinessId == businessId && o.Type == definition.Name && o.Channel == channel, ct);
        if (existing)
            throw new InvalidOperationException($"An override already exists for {definition.Name}/{channel} on business {businessId}.");

        var now = DateTime.UtcNow;
        var row = new NotificationTemplateOverride
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Type = definition.Name,
            Channel = channel,
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

        var definition = ValidateType(request.Type);
        var channel = NormalizeChannel(request.Channel);
        ValidateTokens(request, definition.Name, definition);

        // (type, channel) is unique per business. Moving an override onto an
        // occupied pair would otherwise fail on the unique index with a 500.
        var collision = await _context.NotificationTemplateOverrides
            .AnyAsync(o => o.BusinessId == businessId
                && o.Id != overrideId
                && o.Type == definition.Name
                && o.Channel == channel, ct);
        if (collision)
            throw new InvalidOperationException(
                $"An override already exists for {definition.Name}/{channel} on business {businessId}.");

        // Snapshot the current version before mutating, unless this version is
        // already in history (it is, once it was written by a previous update).
        // Without this check the second update re-inserts the same
        // (override_id, version) pair and violates
        // ux_notification_template_override_versions_override_version.
        var alreadySnapshotted = await _context.NotificationTemplateOverrideVersions
            .AnyAsync(v => v.OverrideId == row.Id && v.Version == row.Version, ct);
        if (!alreadySnapshotted)
            await WriteVersionAsync(row, row.Version, row.UpdatedBy, ct);

        row.Type = definition.Name;
        row.Channel = channel;
        row.SubjectTemplate = request.SubjectTemplate;
        row.BodyTemplateHtml = request.BodyTemplateHtml;
        row.BodyTemplateText = request.BodyTemplateText;
        row.Version = row.Version + 1;
        row.UpdatedBy = request.UpdatedBy;
        row.UpdatedAt = DateTime.UtcNow;

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

        var definition = ValidateType(historical.Type);
        var channel = NormalizeChannel(historical.Channel);
        var tmpRequest = new UpsertNotificationTemplateOverrideRequest
        {
            Channel = channel,
            SubjectTemplate = historical.SubjectTemplate,
            BodyTemplateHtml = historical.BodyTemplateHtml,
            BodyTemplateText = historical.BodyTemplateText,
            UpdatedBy = updatedBy,
        };
        ValidateTokens(tmpRequest, definition.Name, definition);

        var collision = await _context.NotificationTemplateOverrides
            .AnyAsync(o => o.BusinessId == businessId
                && o.Id != overrideId
                && o.Type == definition.Name
                && o.Channel == channel, ct);
        if (collision)
            throw new InvalidOperationException(
                $"An override already exists for {definition.Name}/{channel} on business {businessId}.");

        var alreadySnapshotted = await _context.NotificationTemplateOverrideVersions
            .AnyAsync(v => v.OverrideId == row.Id && v.Version == row.Version, ct);
        if (!alreadySnapshotted)
            await WriteVersionAsync(row, row.Version, row.UpdatedBy, ct);

        row.Type = definition.Name;
        row.Channel = channel;
        row.SubjectTemplate = historical.SubjectTemplate;
        row.BodyTemplateHtml = historical.BodyTemplateHtml;
        row.BodyTemplateText = historical.BodyTemplateText;
        row.Version = row.Version + 1;
        row.UpdatedBy = updatedBy;
        row.UpdatedAt = DateTime.UtcNow;

        await WriteVersionAsync(row, row.Version, updatedBy, ct);
        await _context.SaveChangesAsync(ct);

        return ToDto(row);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Private helpers
    // ═══════════════════════════════════════════════════════════════════

    private static NotificationTypeDef ValidateType(string type)
    {
        var trimmed = (type ?? string.Empty).Trim();
        if (!NotificationTypes.TryGet(trimmed, out var definition))
        {
            throw new InvalidOperationException(
                $"Unknown notification type '{type}'. Use one of the registered types " +
                $"listed by the notification-templates/types endpoint.");
        }

        return definition;
    }

    private static string NormalizeChannel(string channel)
    {
        var normalized = (channel ?? string.Empty).Trim().ToLowerInvariant();
        if (!NotificationDefaults.AllChannels.Contains(normalized, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported channel '{channel}'. Supported channels: " +
                $"{string.Join(", ", NotificationDefaults.AllChannels)}.");
        }

        return normalized;
    }

    private static void ValidateTokens(
        UpsertNotificationTemplateOverrideRequest request,
        string type,
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
                $"Allowed tokens for {type}: {string.Join(", ", validTokens.OrderBy(x => x))}.");
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

    private Task WriteVersionAsync(
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
        return Task.CompletedTask;
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

