using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Storefront revision history: list, inspect, compare and restore.
///
/// <para>The service never allocates revision numbers itself except when it writes
/// a rollback snapshot, and that write goes through
/// <see cref="StorefrontBuilderService.RunRevisionSafeAsync{T}"/> so it shares the
/// builder's atomicity and replay guarantees. The business is always resolved from
/// the caller's membership.</para>
/// </summary>
public class StorefrontRevisionService : IStorefrontRevisionService
{
    private const int MaxPageSize = 100;

    private readonly ApplicationDbContext _context;
    private readonly IBusinessScopeResolver _businessScopeResolver;
    private readonly ILogger<StorefrontRevisionService> _logger;
    private readonly ITenantContext? _tenant;

    public StorefrontRevisionService(
        ApplicationDbContext context,
        IBusinessScopeResolver businessScopeResolver,
        ILogger<StorefrontRevisionService> logger,
        ITenantContext? tenant = null)
    {
        _context = context;
        _businessScopeResolver = businessScopeResolver;
        _logger = logger;
        _tenant = tenant;
    }

    public async Task<ApiResponse<StorefrontRevisionListResponse>> ListAsync(
        Guid ownerId, StorefrontRevisionQuery query, CancellationToken cancellationToken = default)
    {
        var businessId = await _businessScopeResolver.GetOwnedBusinessIdAsync(ownerId);
        if (businessId == null)
            return ApiResponse<StorefrontRevisionListResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        StorefrontRevisionType? type = null;
        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            if (!Enum.TryParse<StorefrontRevisionType>(query.Type, ignoreCase: true, out var parsed))
                return ApiResponse<StorefrontRevisionListResponse>.Fail("VALIDATION_ERROR", "Unknown revision type.");
            type = parsed;
        }

        // Filtering happens in SQL: the payload column is never read here, so the
        // feed stays cheap however large the history grows.
        var revisions = _context.StorefrontRevisions.AsNoTracking()
            .Where(r => r.BusinessId == businessId.Value);

        if (type is StorefrontRevisionType revisionType)
            revisions = revisions.Where(r => r.RevisionType == revisionType);
        if (query.PublishedOnly)
            revisions = revisions.Where(r => r.IsPublishedSnapshot);
        if (query.From is DateTime from)
            revisions = revisions.Where(r => r.CreatedAt >= from);
        if (query.To is DateTime to)
            revisions = revisions.Where(r => r.CreatedAt <= to);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            revisions = revisions.Where(r =>
                (r.Description != null && r.Description.Contains(term)) ||
                (r.CreatedByUser != null && r.CreatedByUser.FullName.Contains(term)));
        }

        var totalCount = await revisions.CountAsync(cancellationToken);
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);

        var rows = await revisions
            .OrderByDescending(r => r.RevisionNumber)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new
            {
                r.Id,
                r.RevisionNumber,
                r.RevisionType,
                r.Description,
                r.CreatedAt,
                r.CreatedByUserId,
                r.IsPublishedSnapshot,
                r.SourceRevisionId,
                CreatedByName = r.CreatedByUser != null ? r.CreatedByUser.FullName : null,
                SourceRevisionNumber = r.SourceRevision != null ? (int?)r.SourceRevision.RevisionNumber : null,
            })
            .ToListAsync(cancellationToken);

        var draftVersion = await _context.BusinessLandingPageConfigs.AsNoTracking()
            .Where(x => x.BusinessId == businessId.Value)
            .Select(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);

        return ApiResponse<StorefrontRevisionListResponse>.Ok(new StorefrontRevisionListResponse
        {
            Items = rows.Select(r => new StorefrontRevisionSummaryResponse
            {
                Id = r.Id,
                RevisionNumber = r.RevisionNumber,
                RevisionType = TypeName(r.RevisionType),
                Description = r.Description,
                CreatedAt = r.CreatedAt,
                CreatedByUserId = r.CreatedByUserId,
                CreatedByName = r.CreatedByName,
                SourceRevisionId = r.SourceRevisionId,
                SourceRevisionNumber = r.SourceRevisionNumber,
                IsPublishedSnapshot = r.IsPublishedSnapshot,
            }).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            HasMore = page < totalPages,
            DraftVersion = draftVersion,
        });
    }

    /// <summary>Projection shape shared by the detail, compare and restore reads.</summary>
    private sealed record RevisionRow(
        Guid Id,
        int RevisionNumber,
        StorefrontRevisionType RevisionType,
        string? Description,
        DateTime CreatedAt,
        Guid? CreatedByUserId,
        string? CreatedByName,
        Guid? SourceRevisionId,
        int? SourceRevisionNumber,
        bool IsPublishedSnapshot,
        string ConfigurationJson);

    /// <summary>
    /// Reads a stored snapshot into a configuration ready to render or restore.
    /// A snapshot that cannot be parsed is rejected (and the caller reports it)
    /// rather than silently treated as the platform defaults.
    /// </summary>
    private static bool TryReadConfig(string? configurationJson, out StorefrontConfigDto config)
    {
        var parsed = StorefrontBuilderService.ParseConfig(configurationJson);
        if (parsed == null)
        {
            config = new StorefrontConfigDto();
            return false;
        }
        config = StorefrontBuilderDefaults.Merge(parsed);
        return true;
    }

    private static StorefrontRevisionSummaryResponse Summary(RevisionRow row) => new()
    {
        Id = row.Id,
        RevisionNumber = row.RevisionNumber,
        RevisionType = TypeName(row.RevisionType),
        Description = row.Description,
        CreatedAt = row.CreatedAt,
        CreatedByUserId = row.CreatedByUserId,
        CreatedByName = row.CreatedByName,
        SourceRevisionId = row.SourceRevisionId,
        SourceRevisionNumber = row.SourceRevisionNumber,
        IsPublishedSnapshot = row.IsPublishedSnapshot,
    };

    public async Task<ApiResponse<StorefrontRevisionDetailResponse>> GetAsync(
        Guid ownerId, Guid revisionId, CancellationToken cancellationToken = default)
    {
        var businessId = await _businessScopeResolver.GetOwnedBusinessIdAsync(ownerId);
        if (businessId == null)
            return ApiResponse<StorefrontRevisionDetailResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var row = await LoadAsync(businessId.Value, revisionId, cancellationToken);

        // Another tenant's revision is "not found", never "forbidden": this endpoint
        // must not confirm that another business's revision exists.
        if (row == null)
            return ApiResponse<StorefrontRevisionDetailResponse>.Fail("NOT_FOUND", "That version could not be found.");

        if (!TryReadConfig(row.ConfigurationJson, out var config))
        {
            _logger.LogWarning("Storefront revision {RevisionId} holds an unreadable snapshot.", revisionId);
            return ApiResponse<StorefrontRevisionDetailResponse>.Fail("VALIDATION_ERROR",
                "That version could not be read.");
        }

        return ApiResponse<StorefrontRevisionDetailResponse>.Ok(new StorefrontRevisionDetailResponse
        {
            Revision = Summary(row),
            Configuration = config,
        });
    }

    private Task<RevisionRow?> LoadAsync(Guid businessId, Guid revisionId, CancellationToken cancellationToken)
        => _context.StorefrontRevisions.AsNoTracking()
            .Where(r => r.Id == revisionId && r.BusinessId == businessId)
            .Select(r => new RevisionRow(
                r.Id,
                r.RevisionNumber,
                r.RevisionType,
                r.Description,
                r.CreatedAt,
                r.CreatedByUserId,
                r.CreatedByUser != null ? r.CreatedByUser.FullName : null,
                r.SourceRevisionId,
                r.SourceRevision != null ? (int?)r.SourceRevision.RevisionNumber : null,
                r.IsPublishedSnapshot,
                r.ConfigurationJson))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<ApiResponse<StorefrontRevisionCompareResponse>> CompareAsync(
        Guid ownerId, Guid fromRevisionId, Guid toRevisionId, CancellationToken cancellationToken = default)
    {
        var businessId = await _businessScopeResolver.GetOwnedBusinessIdAsync(ownerId);
        if (businessId == null)
            return ApiResponse<StorefrontRevisionCompareResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (fromRevisionId == toRevisionId)
            return ApiResponse<StorefrontRevisionCompareResponse>.Fail("VALIDATION_ERROR",
                "Choose two different versions to compare.");

        var fromRow = await LoadAsync(businessId.Value, fromRevisionId, cancellationToken);
        var toRow = await LoadAsync(businessId.Value, toRevisionId, cancellationToken);
        if (fromRow == null || toRow == null)
            return ApiResponse<StorefrontRevisionCompareResponse>.Fail("NOT_FOUND",
                "One of those versions could not be found.");

        if (!TryReadConfig(fromRow.ConfigurationJson, out var fromConfig) ||
            !TryReadConfig(toRow.ConfigurationJson, out var toConfig))
        {
            _logger.LogWarning("A compared storefront revision holds an unreadable snapshot.");
            return ApiResponse<StorefrontRevisionCompareResponse>.Fail("VALIDATION_ERROR",
                "One of those versions could not be read.");
        }

        var changes = StorefrontConfigDiff.Compare(fromConfig, toConfig);

        return ApiResponse<StorefrontRevisionCompareResponse>.Ok(new StorefrontRevisionCompareResponse
        {
            From = Summary(fromRow),
            To = Summary(toRow),
            Identical = changes.Count == 0,
            Counts = new StorefrontChangeCountsResponse
            {
                Added = changes.Count(c => c.Kind == "added"),
                Removed = changes.Count(c => c.Kind == "removed"),
                Modified = changes.Count(c => c.Kind == "modified"),
                Reordered = changes.Count(c => c.Kind == "reordered"),
            },
            Changes = changes,
        });
    }

    public Task<ApiResponse<StorefrontRevisionSummaryResponse>> RestoreAsync(
        Guid ownerId, Guid revisionId, RestoreStorefrontRevisionRequest request, CancellationToken cancellationToken = default)
        => StorefrontBuilderService.RunRevisionSafeAsync(_context, _logger,
            () => RestoreCoreAsync(ownerId, revisionId, request, cancellationToken));

    private async Task<ApiResponse<StorefrontRevisionSummaryResponse>> RestoreCoreAsync(
        Guid ownerId, Guid revisionId, RestoreStorefrontRevisionRequest request, CancellationToken cancellationToken)
    {
        // Rollback replaces the owner's working draft, so it is never implicit.
        if (!request.Confirm)
            return ApiResponse<StorefrontRevisionSummaryResponse>.Fail("VALIDATION_ERROR",
                "Restoring a version must be confirmed.");

        var businessId = await _businessScopeResolver.GetOwnedBusinessIdAsync(ownerId);
        if (businessId == null)
            return ApiResponse<StorefrontRevisionSummaryResponse>.Fail("NOT_FOUND", "No business found for this account.");

        // Tenant narrowing: a request that arrived on a business address may only
        // touch that business's history.
        if (_tenant?.IsActive == true && _tenant.BusinessId is Guid activeTenantId && activeTenantId != businessId.Value)
            return ApiResponse<StorefrontRevisionSummaryResponse>.Fail("NOT_FOUND", "That version could not be found.");

        var source = await LoadAsync(businessId.Value, revisionId, cancellationToken);
        if (source == null)
            return ApiResponse<StorefrontRevisionSummaryResponse>.Fail("NOT_FOUND", "That version could not be found.");

        if (!TryReadConfig(source.ConfigurationJson, out var restored))
        {
            _logger.LogWarning("Storefront revision {RevisionId} holds an unreadable snapshot.", revisionId);
            return ApiResponse<StorefrontRevisionSummaryResponse>.Fail("VALIDATION_ERROR",
                "That version could not be read and cannot be restored.");
        }

        var row = await _context.BusinessLandingPageConfigs
            .FirstOrDefaultAsync(x => x.BusinessId == businessId.Value, cancellationToken);
        if (row == null)
            return ApiResponse<StorefrontRevisionSummaryResponse>.Fail("NOT_FOUND",
                "Save a draft before restoring a version.");

        if (request.ExpectedVersion is int expected && expected != row.Version)
            return ApiResponse<StorefrontRevisionSummaryResponse>.Fail("STALE_VERSION",
                "This storefront changed since you opened it. Reload before restoring a version.");

        var currentDraft = StorefrontBuilderDefaults.Merge(StorefrontBuilderService.ParseConfig(row.DraftConfigJson));
        if (StorefrontBuilderService.SameConfig(currentDraft, restored))
            return ApiResponse<StorefrontRevisionSummaryResponse>.Fail("VALIDATION_ERROR",
                "Your draft already matches that version.");

        // Draft only. The published storefront is deliberately untouched: a rollback
        // must not take a business offline or change what customers see.
        row.DraftConfigJson = JsonSerializer.Serialize(restored, StorefrontBuilderService.JsonOptions);
        row.TemplateId = restored.TemplateId;
        row.Version++;
        row.UpdatedAt = DateTime.UtcNow;

        var nextNumber = (await _context.StorefrontRevisions
            .Where(r => r.BusinessId == businessId.Value)
            .Select(r => (int?)r.RevisionNumber)
            .MaxAsync(cancellationToken) ?? 0) + 1;

        var actorName = await _context.Users.AsNoTracking()
            .Where(u => u.Id == ownerId)
            .Select(u => u.FullName)
            .FirstOrDefaultAsync(cancellationToken);

        var rollback = new StorefrontRevision
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId.Value,
            RevisionNumber = nextNumber,
            ConfigurationJson = JsonSerializer.Serialize(restored, StorefrontBuilderService.JsonOptions),
            RevisionType = StorefrontRevisionType.Rollback,
            Description = $"Restored version #{source.RevisionNumber}",
            CreatedByUserId = ownerId,
            SourceRevisionId = source.Id,
            IsPublishedSnapshot = false,
            CreatedAt = DateTime.UtcNow,
        };
        _context.StorefrontRevisions.Add(rollback);

        // One SaveChanges: the draft write and the rollback revision commit together
        // or not at all.
        await _context.SaveChangesAsync(cancellationToken);

        return ApiResponse<StorefrontRevisionSummaryResponse>.Ok(new StorefrontRevisionSummaryResponse
        {
            Id = rollback.Id,
            RevisionNumber = rollback.RevisionNumber,
            RevisionType = TypeName(StorefrontRevisionType.Rollback),
            Description = rollback.Description,
            CreatedAt = rollback.CreatedAt,
            CreatedByUserId = ownerId,
            CreatedByName = actorName,
            SourceRevisionId = source.Id,
            SourceRevisionNumber = source.RevisionNumber,
            IsPublishedSnapshot = false,
        });
    }

    /// <summary>The wire name of a revision event, mirrored by the frontend.</summary>
    private static string TypeName(StorefrontRevisionType type) => type switch
    {
        StorefrontRevisionType.Publish => "publish",
        StorefrontRevisionType.Rollback => "rollback",
        StorefrontRevisionType.Reset => "reset",
        _ => "draftSave",
    };
}
