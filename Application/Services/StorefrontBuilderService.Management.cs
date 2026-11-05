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
        return ApiResponse<StorefrontBuilderStateResponse>.Ok(BuildState(businessId.Value, row));
    }

    public async Task<ApiResponse<StorefrontBuilderStateResponse>> ResetDraftAsync(Guid ownerId)
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

        row.DraftConfigJson = JsonSerializer.Serialize(StorefrontBuilderDefaults.Merge(published), JsonOptions);
        row.Version++;
        row.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return ApiResponse<StorefrontBuilderStateResponse>.Ok(BuildState(businessId.Value, row));
    }

    public async Task<ApiResponse<StorefrontBuilderStateResponse>> ApplyTemplateAsync(Guid ownerId, string templateId)
    {
        var businessId = await _businessScopeResolver.GetOwnedBusinessIdAsync(ownerId);
        if (businessId == null)
            return ApiResponse<StorefrontBuilderStateResponse>.Fail("NOT_FOUND", "No business found for this account.");
        if (StorefrontTemplates.Find(templateId) == null)
            return ApiResponse<StorefrontBuilderStateResponse>.Fail("VALIDATION_ERROR", "Unknown template.");

        var row = await _context.BusinessLandingPageConfigs
            .FirstOrDefaultAsync(x => x.BusinessId == businessId.Value);

        var current = row == null
            ? StorefrontBuilderDefaults.Create()
            : StorefrontBuilderDefaults.Merge(ParseConfig(row.DraftConfigJson));

        var applied = StorefrontBuilderDefaults.ApplyTemplate(current, templateId, preserveContent: true);

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

        await _context.SaveChangesAsync();
        return ApiResponse<StorefrontBuilderStateResponse>.Ok(BuildState(businessId.Value, row));
    }

    // ── shared helpers ─────────────────────────────────────────────────────────

    internal static StorefrontConfigDto? ParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<StorefrontConfigDto>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    internal static StorefrontBuilderStateResponse BuildState(Guid businessId, BusinessLandingPageConfig? row)
    {
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
