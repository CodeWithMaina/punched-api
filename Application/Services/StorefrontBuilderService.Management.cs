using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

public partial class StorefrontBuilderService
{
    public async Task<ApiResponse<StorefrontBuilderStateResponse>> UnpublishAsync(Guid ownerId)
    {
        var businessId = await _businessScopeResolver.GetOwnedBusinessIdAsync(ownerId);
        if (businessId == null)
            return ApiResponse<StorefrontBuilderStateResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var row = await _context.BusinessLandingPageConfigs
            .FirstOrDefaultAsync(x => x.BusinessId == businessId.Value);
        if (row == null)
            return ApiResponse<StorefrontBuilderStateResponse>.Fail("NOT_FOUND", "There is no storefront to unpublish.");

        row.IsPublished = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return ApiResponse<StorefrontBuilderStateResponse>.Ok(await BuildStateAsync(businessId.Value, row));
    }

    public Task<ApiResponse<StorefrontBuilderStateResponse>> ResetDraftAsync(Guid ownerId)
        => ExecuteRevisionSafeAsync(() => ResetDraftCoreAsync(ownerId));

    private async Task<ApiResponse<StorefrontBuilderStateResponse>> ResetDraftCoreAsync(Guid ownerId)
    {
        var businessId = await _businessScopeResolver.GetOwnedBusinessIdAsync(ownerId);
        if (businessId == null)
            return ApiResponse<StorefrontBuilderStateResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var row = await _context.BusinessLandingPageConfigs
            .FirstOrDefaultAsync(x => x.BusinessId == businessId.Value);
        if (row == null)
            return ApiResponse<StorefrontBuilderStateResponse>.Fail("NOT_FOUND", "There is no storefront to reset.");

        var published = ParseConfig(row.PublishedConfigJson);
        if (published == null)
            return ApiResponse<StorefrontBuilderStateResponse>.Fail("NOT_FOUND", "There is no published version to restore.");

        var restored = StorefrontBuilderDefaults.Merge(published);
        var currentDraft = StorefrontBuilderDefaults.Merge(ParseConfig(row.DraftConfigJson));

        // Discarding a draft that already matches the published version is not an
        // event: history must never fill up with no-op resets.
        if (!SameConfig(currentDraft, restored))
        {
            row.DraftConfigJson = JsonSerializer.Serialize(restored, JsonOptions);
            row.TemplateId = restored.TemplateId;
            row.Version++;
            row.UpdatedAt = DateTime.UtcNow;

            AppendRevision(
                businessId.Value,
                await LatestRevisionNumberAsync(businessId.Value) + 1,
                restored,
                StorefrontRevisionType.Reset,
                ownerId,
                sourceRevisionId: null,
                isPublishedSnapshot: false,
                description: "Draft reset to the published version");
        }

        await _context.SaveChangesAsync();
        return ApiResponse<StorefrontBuilderStateResponse>.Ok(await BuildStateAsync(businessId.Value, row));
    }

    public Task<ApiResponse<StorefrontBuilderStateResponse>> ApplyTemplateAsync(Guid ownerId, string templateId)
        => ExecuteRevisionSafeAsync(() => ApplyTemplateCoreAsync(ownerId, templateId));

    private async Task<ApiResponse<StorefrontBuilderStateResponse>> ApplyTemplateCoreAsync(Guid ownerId, string templateId)
    {
        var businessId = await _businessScopeResolver.GetOwnedBusinessIdAsync(ownerId);
        if (businessId == null)
            return ApiResponse<StorefrontBuilderStateResponse>.Fail("NOT_FOUND", "No business found for this account.");
        var template = StorefrontTemplates.Find(templateId);
        if (template == null)
            return ApiResponse<StorefrontBuilderStateResponse>.Fail("VALIDATION_ERROR", "Unknown template.");

        var row = await _context.BusinessLandingPageConfigs
            .FirstOrDefaultAsync(x => x.BusinessId == businessId.Value);

        var current = row == null
            ? StorefrontBuilderDefaults.Create()
            : StorefrontBuilderDefaults.Merge(ParseConfig(row.DraftConfigJson));

        // Snapshot the "before" text first: ApplyTemplate reuses and mutates the
        // configuration it is handed, so comparing afterwards would always report
        // "no change".
        var before = Normalise(current);

        var applied = StorefrontBuilderDefaults.ApplyTemplate(current, templateId, preserveContent: true);

        // A template switch changes the design, so it is a real draft event and is
        // recorded as one. Re-applying the identical result is not.
        if (row != null && string.Equals(before, Normalise(applied), StringComparison.Ordinal))
        {
            return ApiResponse<StorefrontBuilderStateResponse>.Ok(await BuildStateAsync(businessId.Value, row));
        }

        if (row == null)
        {
            row = new BusinessLandingPageConfig
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId.Value,
                Version = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ConfigJson = "{}",
            };
            _context.BusinessLandingPageConfigs.Add(row);
        }
        else
        {
            row.Version++;
            row.UpdatedAt = DateTime.UtcNow;
        }

        row.TemplateId = applied.TemplateId;
        row.DraftConfigJson = JsonSerializer.Serialize(applied, JsonOptions);

        AppendRevision(
            businessId.Value,
            await LatestRevisionNumberAsync(businessId.Value) + 1,
            applied,
            StorefrontRevisionType.DraftSave,
            ownerId,
            sourceRevisionId: null,
            isPublishedSnapshot: false,
            description: $"Template changed to {template.Name}");

        await _context.SaveChangesAsync();
        return ApiResponse<StorefrontBuilderStateResponse>.Ok(await BuildStateAsync(businessId.Value, row));
    }

    // ── shared helpers ─────────────────────────────────────────────────────────

    internal static StorefrontConfigDto? ParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<StorefrontConfigDto>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    internal async Task<StorefrontBuilderStateResponse> BuildStateAsync(Guid businessId, BusinessLandingPageConfig? row)
    {
        var latestRevisionNumber = await LatestRevisionNumberAsync(businessId);

        if (row == null)
        {
            return new StorefrontBuilderStateResponse
            {
                BusinessId = businessId,
                TemplateId = StorefrontBuilderDefaults.DefaultTemplateId,
                IsPublished = true,
                HasDraft = false,
                HasUnpublishedChanges = false,
                Draft = StorefrontBuilderDefaults.Create(),
                Published = null,
                DraftVersion = 0,
                LatestRevisionNumber = latestRevisionNumber,
            };
        }

        var draft = StorefrontBuilderDefaults.Merge(ParseConfig(row.DraftConfigJson));
        var published = ParseConfig(row.PublishedConfigJson);
        var hasUnpublishedChanges =
            !string.IsNullOrWhiteSpace(row.PublishedConfigJson) &&
            !string.Equals(row.PublishedConfigJson, row.DraftConfigJson, StringComparison.Ordinal);

        return new StorefrontBuilderStateResponse
        {
            BusinessId = businessId,
            TemplateId = string.IsNullOrWhiteSpace(row.TemplateId) ? draft.TemplateId : row.TemplateId,
            IsPublished = row.IsPublished,
            PublishedAt = row.PublishedAt,
            UpdatedAt = row.UpdatedAt,
            HasDraft = !string.IsNullOrWhiteSpace(row.DraftConfigJson),
            HasUnpublishedChanges = hasUnpublishedChanges,
            Draft = draft,
            Published = published == null ? null : StorefrontBuilderDefaults.Merge(published),
            DraftVersion = row.Version,
            LatestRevisionNumber = latestRevisionNumber,
        };
    }

    /// <summary>
    /// Every media id the configuration references must belong to the business and
    /// be ready + public. Returns a human message on the first failure, else null.
    /// </summary>
    private async Task<string?> ValidateMediaAsync(Guid businessId, StorefrontConfigDto config)
    {
        var ids = CollectMediaIds(config);
        if (ids.Count == 0) return null;

        var valid = await _context.Media.AsNoTracking()
            .Where(m => ids.Contains(m.Id) && m.BusinessId == businessId &&
                        m.Status == MediaStatus.Ready && m.Visibility == MediaVisibility.Public)
            .Select(m => m.Id)
            .ToListAsync();

        var invalid = ids.Except(valid).ToList();
        if (invalid.Count == 0) return null;
        _logger.LogWarning("Storefront save referenced {Count} invalid media id(s) for business {BusinessId}.", invalid.Count, businessId);
        return "One of the selected images could not be found for this business.";
    }

    private static List<Guid> CollectMediaIds(StorefrontConfigDto config)
    {
        var ids = new List<Guid>();
        void Add(Guid? id) { if (id.HasValue) ids.Add(id.Value); }

        Add(config.Theme.BackgroundImageMediaId);
        Add(config.Header.LogoMediaId);
        Add(config.Hero.BackgroundMediaId);
        foreach (var id in config.Hero.CarouselMediaIds) ids.Add(id);
        Add(config.Footer.LogoMediaId);
        Add(config.Seo.OgImageMediaId);
        Add(config.Seo.FaviconMediaId);

        return ids.Distinct().ToList();
    }
}
