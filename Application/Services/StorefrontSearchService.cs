using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Public, business-scoped search — see <see cref="IStorefrontSearchService"/>.
///
/// <para><b>Scope.</b> Every query is anchored to the business the request
/// named (and, on a tenant host, to the active tenant), so a result can never
/// come from another business. Services are filtered in the database to the
/// published set (active, showcased, not archived); staff are read from the
/// business's own public roster, which is bounded and small, then matched in
/// memory against name, biography, job title and the services they perform.</para>
///
/// <para><b>Why not a single SQL LIKE over everything:</b> matching a staff
/// member's job title needs their *current* position, which is the newest row of
/// an append-only history table, and matching "who does this service" needs the
/// service assignments. Resolving those per row in SQL would be a fan-out; the
/// business's own roster is small enough to resolve once.</para>
/// </summary>
public sealed class StorefrontSearchService : IStorefrontSearchService
{
    private const string ServiceModuleKey = "serviceCatalog";
    private const string StaffModuleKey = "staff";

    /// <summary>Bounded reads: a storefront search never materialises a whole tenant's catalogue.</summary>
    private const int MaxServiceHits = 200;
    private const int MaxRosterSize = 200;

    private const int MaxPageSize = 24;
    private const int DefaultPageSize = 12;

    private readonly ApplicationDbContext _db;
    private readonly IModuleEntitlementService _moduleEntitlements;
    private readonly ILogger<StorefrontSearchService> _logger;
    private readonly ITenantContext? _tenant;

    public StorefrontSearchService(
        ApplicationDbContext db,
        IModuleEntitlementService moduleEntitlements,
        ILogger<StorefrontSearchService> logger,
        ITenantContext? tenant = null)
    {
        _db = db;
        _moduleEntitlements = moduleEntitlements;
        _logger = logger;
        _tenant = tenant;
    }

    private Guid? TenantBusinessId => _tenant?.IsActive == true ? _tenant.BusinessId : null;

    /// <summary>Trimmed, lower-cased needle used for every comparison.</summary>
    private static string Normalise(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    public async Task<ApiResponse<StorefrontSearchResponse>> SearchAsync(
        Guid businessId,
        string? query,
        string? type,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (businessId == Guid.Empty)
            return ApiResponse<StorefrontSearchResponse>.Fail("NOT_FOUND", "Business not found.");

        if (TenantBusinessId is Guid activeTenantId && businessId != activeTenantId)
            return ApiResponse<StorefrontSearchResponse>.Fail("NOT_FOUND", "Business not found.");

        try
        {
            var business = await _db.Businesses.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == businessId && !b.IsDeleted, cancellationToken);
            if (business == null)
                return ApiResponse<StorefrontSearchResponse>.Fail("NOT_FOUND", "Business not found.");

            var scope = StorefrontSearchTypes.Normalise(type);
            var pageNumber = Math.Max(page, 1);
            var size = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
            var needle = Normalise(query);

            // Category facets come from the business's own active categories and
            // are returned even for a blank query, so the search page can render
            // its scope chips without a second call.
            var categoryRows = await _db.ServiceCategories.AsNoTracking()
                .Where(c => c.BusinessId == businessId && c.IsActive)
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                .Select(c => new CategoryRow(c.Id, c.Name))
                .ToListAsync(cancellationToken);
            var categories = categoryRows.Select(c => c.Name).ToList();

            if (needle.Length == 0)
                return ApiResponse<StorefrontSearchResponse>.Ok(new StorefrontSearchResponse
                {
                    Query = string.Empty,
                    Type = scope,
                    Page = pageNumber,
                    PageSize = size,
                    Categories = categories
                });

            var results = new List<StorefrontSearchResultResponse>();

            if (scope is StorefrontSearchTypes.All or StorefrontSearchTypes.Services
                && await _moduleEntitlements.IsModuleEnabledAsync(businessId, ServiceModuleKey))
            {
                results.AddRange(await SearchServicesAsync(businessId, needle, categoryRows, cancellationToken));
            }

            if (scope is StorefrontSearchTypes.All or StorefrontSearchTypes.Staff
                && await _moduleEntitlements.IsModuleEnabledAsync(businessId, StaffModuleKey))
            {
                results.AddRange(await SearchStaffAsync(businessId, needle, cancellationToken));
            }

            var total = results.Count;
            var pageItems = results.Skip((pageNumber - 1) * size).Take(size).ToList();

            return ApiResponse<StorefrontSearchResponse>.Ok(new StorefrontSearchResponse
            {
                Query = needle,
                Type = scope,
                Items = pageItems,
                TotalCount = total,
                Page = pageNumber,
                PageSize = size,
                TotalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)size),
                Categories = categories
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error searching business {BusinessId}", businessId);
            return ApiResponse<StorefrontSearchResponse>.Fail("SEARCH_FAILED", "Search is unavailable right now.");
        }
    }

    /// <summary>A category row reduced to what search needs.</summary>
    private readonly record struct CategoryRow(Guid Id, string Name);

    /// <summary>
    /// Published services matching the needle by name, description or category.
    /// The filter is applied in the database, so an unadvertised service is never
    /// even read.
    /// </summary>
    private async Task<List<StorefrontSearchResultResponse>> SearchServicesAsync(
        Guid businessId, string needle, List<CategoryRow> categories, CancellationToken cancellationToken)
    {
        var matchedCategoryIds = categories
            .Where(category => category.Name.ToLowerInvariant().Contains(needle))
            .Select(category => category.Id)
            .ToList();
        var categoryNameById = categories.ToDictionary(category => category.Id, category => category.Name);

        var services = await _db.ServiceCatalogItems.AsNoTracking()
            .Where(service => service.BusinessId == businessId
                              && service.IsActive && service.Showcase && !service.IsArchived
                              && (service.Name.ToLower().Contains(needle)
                                  || (service.Description != null && service.Description.ToLower().Contains(needle))
                                  || (service.CategoryId != null && matchedCategoryIds.Contains(service.CategoryId.Value))))
            .OrderBy(service => service.CreatedAt)
            .Take(MaxServiceHits)
            .Select(service => new
            {
                service.Id,
                service.Name,
                service.Description,
                service.DurationMinutes,
                service.Price,
                service.CategoryId
            })
            .ToListAsync(cancellationToken);

        if (services.Count == 0) return [];

        var serviceIds = services.Select(service => service.Id).ToList();
        var coverRows = await (from relationship in _db.ServiceMedia.AsNoTracking()
                               join media in _db.Media.AsNoTracking() on relationship.MediaId equals media.Id
                               where serviceIds.Contains(relationship.ServiceCatalogItemId)
                                     && media.Status == MediaStatus.Ready
                               orderby relationship.SortOrder
                               select new { relationship.ServiceCatalogItemId, media.VariantsJson })
            .ToListAsync(cancellationToken);
        var coverByService = coverRows
            .GroupBy(row => row.ServiceCatalogItemId)
            .ToDictionary(group => group.Key, group => ParsePublicVariants(group.First().VariantsJson));

        return services.Select(service =>
        {
            string? categoryName = null;
            if (service.CategoryId is Guid categoryId)
                categoryNameById.TryGetValue(categoryId, out categoryName);

            return new StorefrontSearchResultResponse
            {
                Type = "service",
                Id = service.Id,
                Title = service.Name,
                Subtitle = categoryName,
                Description = service.Description,
                CategoryName = categoryName,
                Price = service.Price,
                DurationMinutes = service.DurationMinutes,
                ImageVariants = coverByService.TryGetValue(service.Id, out var variants) ? variants : []
            };
        }).ToList();
    }

    /// <summary>
    /// Publicly visible staff of this business matching the needle by name,
    /// biography, current job title or the services they perform.
    ///
    /// <para>Only <c>Staff</c> memberships that are active AND public are read;
    /// private employee information never enters this path. The roster read is
    /// bounded, and the match runs over the business's own people only.</para>
    /// </summary>
    private async Task<List<StorefrontSearchResultResponse>> SearchStaffAsync(
        Guid businessId, string needle, CancellationToken cancellationToken)
    {
        var roster = await (from membership in _db.BusinessMemberships.AsNoTracking()
                            join user in _db.Users.AsNoTracking() on membership.UserId equals user.Id
                            where membership.BusinessId == businessId
                                  && membership.Role == BusinessMembershipRole.Staff
                                  && membership.Status == BusinessMembershipStatus.Active
                                  && membership.IsPublic
                            orderby user.FullName
                            select new { membership.UserId, user.FullName, user.AvatarUrl, membership.Bio })
            .Take(MaxRosterSize)
            .ToListAsync(cancellationToken);
        if (roster.Count == 0) return [];

        var staffIds = roster.Select(member => member.UserId).ToList();

        // The current job title is the newest history row for the member (a null
        // label is a clearing, so the latest row decides even when empty).
        var positionRows = await _db.StaffPositionHistory.AsNoTracking()
            .Where(history => history.BusinessId == businessId && staffIds.Contains(history.StaffUserId))
            .Select(history => new { history.StaffUserId, history.PositionName, history.ChangedAt })
            .ToListAsync(cancellationToken);
        var positionByStaff = positionRows
            .GroupBy(row => row.StaffUserId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.ChangedAt).First().PositionName);

        // Only publicly visible services are attributed, matching the roster
        // endpoint exactly, so search never reveals an unadvertised service.
        var publicServices = await _db.ServiceCatalogItems.AsNoTracking()
            .Where(service => service.BusinessId == businessId && service.IsActive && service.Showcase && !service.IsArchived)
            .Select(service => new { service.Id, service.Name })
            .ToListAsync(cancellationToken);
        var publicServiceIds = publicServices.Select(service => service.Id).ToHashSet();
        var serviceNameById = publicServices.ToDictionary(service => service.Id, service => service.Name);

        var assignments = publicServiceIds.Count == 0
            ? []
            : (await _db.StaffServiceAssignments.AsNoTracking()
                .Where(assignment => assignment.BusinessId == businessId && staffIds.Contains(assignment.StaffUserId))
                .ToListAsync(cancellationToken));
        var servicesByStaff = assignments
            .Where(assignment => publicServiceIds.Contains(assignment.ServiceCatalogItemId))
            .GroupBy(assignment => assignment.StaffUserId)
            .ToDictionary(group => group.Key,
                group => group.Select(assignment => assignment.ServiceCatalogItemId).Distinct().ToList());

        var results = new List<StorefrontSearchResultResponse>();
        foreach (var member in roster)
        {
            var position = positionByStaff.TryGetValue(member.UserId, out var title) ? title : null;
            var serviceIds = servicesByStaff.TryGetValue(member.UserId, out var ids) ? ids : [];
            var performedServices = serviceIds
                .Where(serviceNameById.ContainsKey)
                .Select(id => serviceNameById[id])
                .ToList();

            if (!Matches(needle, member.FullName, member.Bio, position, string.Join(' ', performedServices)))
                continue;

            results.Add(new StorefrontSearchResultResponse
            {
                Type = "staff",
                Id = member.UserId,
                Title = member.FullName,
                Subtitle = position,
                Description = member.Bio,
                AvatarUrl = member.AvatarUrl,
                ServiceIds = serviceIds
            });
        }

        return results;
    }

    /// <summary>Case-insensitive containment across every supplied haystack.</summary>
    private static bool Matches(string needle, params string?[] haystacks) =>
        haystacks.Any(haystack => !string.IsNullOrEmpty(haystack)
                                  && haystack.ToLowerInvariant().Contains(needle));

    /// <summary>Ready, renderable variants only (webp/jpeg with a URL).</summary>
    private static IReadOnlyList<MediaVariantResponse> ParsePublicVariants(string? variantsJson)
    {
        if (string.IsNullOrWhiteSpace(variantsJson)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<MediaVariantResponse>>(variantsJson) ?? [])
                .Where(variant => !string.IsNullOrWhiteSpace(variant.Url)
                                  && variant.Width > 0
                                  && variant.Format is "webp" or "jpeg")
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
