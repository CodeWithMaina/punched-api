using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

public class NotificationsService : INotificationsService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ApplicationDbContext _context;
    private readonly ILogger<NotificationsService> _logger;

    /// <summary>Active tenant (null in unit tests / root host) — scoping only, never a grant.</summary>
    private readonly ITenantContext? _tenant;

    /// <summary>
    /// Renders the readable title and body from the registry's in-app template, so the inbox and
    /// the email describe one event the same way. Optional so a unit test can build the service
    /// without one — an absent renderer simply leaves the body empty.
    /// </summary>
    private readonly ITemplateRenderer? _renderer;

    public NotificationsService(
        IUnitOfWork unitOfWork,
        ApplicationDbContext context,
        ILogger<NotificationsService> logger,
        ITenantContext? tenant = null,
        ITemplateRenderer? renderer = null)
    {
        _unitOfWork = unitOfWork;
        _context = context;
        _logger = logger;
        _tenant = tenant;
        _renderer = renderer;
    }

    /// <summary>Active tenant business id, or null on the platform root (legacy scoping).</summary>
    private Guid? TenantBusinessId => _tenant?.IsActive == true ? _tenant.BusinessId : null;

    /// <summary>
    /// The one place the caller's inbox is scoped: their own rows, and — on a tenant host — only
    /// platform-wide rows plus that tenant's. Every read and write below starts here, so no code
    /// path can forget the ownership predicate.
    /// </summary>
    private IQueryable<Notification> OwnedBy(Guid userId)
    {
        var query = _context.Notifications.Where(n => n.UserId == userId);
        if (TenantBusinessId is Guid tenantId)
            query = query.Where(n => n.BusinessId == null || n.BusinessId == tenantId);
        return query;
    }

    /// <inheritdoc />
    public Task<int> GetUnreadCountAsync(Guid userId) =>
        OwnedBy(userId).Where(n => !n.IsRead && n.ArchivedAt == null).CountAsync();

    /// <inheritdoc />
    public async Task<bool> MarkReadByIdAsync(Guid userId, Guid notificationId)
    {
        // The user filter is the authorization check: another user's row is
        // indistinguishable from a missing one.
        var notification = await OwnedBy(userId).FirstOrDefaultAsync(n => n.Id == notificationId);
        if (notification == null) return false;

        if (!notification.IsRead)
        {
            notification.IsRead = true;
            await _unitOfWork.SaveChangesAsync();
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<int> MarkAllReadAsync(Guid userId)
    {
        // Archived rows are already out of the inbox, so they are left alone: "mark all read"
        // means "clear my badge", and the badge never counted them.
        var toUpdate = await OwnedBy(userId)
            .Where(n => !n.IsRead && n.ArchivedAt == null)
            .ToListAsync();

        foreach (var notification in toUpdate)
        {
            notification.IsRead = true;
        }

        if (toUpdate.Count > 0)
            await _unitOfWork.SaveChangesAsync();

        return toUpdate.Count;
    }

    public async Task<List<NotificationDto>> GetAsync(Guid userId, bool unreadOnly, int limit = 50)
    {
        var query = OwnedBy(userId)
            .Where(n => n.ArchivedAt == null) // archived rows leave the default list
            .AsNoTracking();

        if (unreadOnly)
            query = query.Where(n => !n.IsRead);

        var notifications = await query
            .OrderByDescending(n => n.CreatedAt)
            .Take(limit)
            .ToListAsync();

        return notifications.Select(MapDto).ToList();
    }

    /// <inheritdoc />
    public async Task<ApiResponse<NotificationInboxPageDto>> GetInboxAsync(
        Guid userId, NotificationInboxQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        // A business id from the client narrows the inbox only if the caller is really in that
        // business. Anything else is refused rather than silently ignored, because dropping the
        // filter would answer a narrower question with a wider list.
        if (query.BusinessId is Guid requested)
        {
            var isMember = await _context.BusinessMemberships.AsNoTracking().AnyAsync(m =>
                m.UserId == userId &&
                m.BusinessId == requested &&
                m.Status == BusinessMembershipStatus.Active, ct);

            if (!isMember)
                return ApiResponse<NotificationInboxPageDto>.Fail(
                    "FORBIDDEN", "You do not have access to that business's notifications.");
        }

        var scope = OwnedBy(userId).Where(n => n.ArchivedAt == null).AsNoTracking();
        if (query.BusinessId is Guid scoped)
            scope = scope.Where(n => n.BusinessId == null || n.BusinessId == scoped);

        var unreadCount = await scope.CountAsync(n => !n.IsRead, ct);

        // Facets come from the caller's own scope, so a filter can only offer a value that exists.
        var storedTypes = await scope.Select(n => n.Type).Distinct().ToListAsync(ct);
        var availableTypes = storedTypes
            .Select(NotificationTypeAliases.Resolve)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToList();

        var availableCategories = storedTypes
            .Select(ResolveCategory)
            .Where(category => category.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(category => category, StringComparer.Ordinal)
            .ToList();

        var filtered = scope;

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            filtered = query.Status.Trim().ToLowerInvariant() switch
            {
                "unread" => filtered.Where(n => !n.IsRead),
                "read" => filtered.Where(n => n.IsRead),
                _ => filtered,
            };
        }

        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            // Match either the canonical type or the legacy alias that produced it, so a filter
            // written as `loyalty.goal_reached` also finds the rows stored as `GoalReached`.
            var wantedType = query.Type.Trim();
            var types = storedTypes
                .Where(type => string.Equals(
                    NotificationTypeAliases.Resolve(type), wantedType, StringComparison.Ordinal))
                .ToArray();
            filtered = filtered.Where(n => types.Contains(n.Type));
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            var category = query.Category.Trim();
            var types = storedTypes
                .Where(type => string.Equals(ResolveCategory(type), category, StringComparison.Ordinal))
                .ToArray();
            filtered = filtered.Where(n => types.Contains(n.Type));
        }

        if (query.FromUtc is DateTime from)
        {
            var fromUtc = from.ToUniversalTime();
            filtered = filtered.Where(n => n.CreatedAt >= fromUtc);
        }

        if (query.ToUtc is DateTime to)
        {
            var toUtc = to.ToUniversalTime();
            filtered = filtered.Where(n => n.CreatedAt <= toUtc);
        }

        var totalCount = await filtered.CountAsync(ct);

        var descending = !string.Equals(query.Direction, "asc", StringComparison.OrdinalIgnoreCase);
        var ordered = (query.Sort?.Trim().ToLowerInvariant()) switch
        {
            "type" => descending
                ? filtered.OrderByDescending(n => n.Type).ThenByDescending(n => n.CreatedAt)
                : filtered.OrderBy(n => n.Type).ThenBy(n => n.CreatedAt),
            "status" => descending
                ? filtered.OrderByDescending(n => n.IsRead).ThenByDescending(n => n.CreatedAt)
                : filtered.OrderBy(n => n.IsRead).ThenBy(n => n.CreatedAt),
            _ => descending
                ? filtered.OrderByDescending(n => n.CreatedAt)
                : filtered.OrderBy(n => n.CreatedAt),
        };

        var rows = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return ApiResponse<NotificationInboxPageDto>.Ok(new NotificationInboxPageDto
        {
            Items = rows.Select(MapDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize),
            UnreadCount = unreadCount,
            AvailableTypes = availableTypes,
            AvailableCategories = availableCategories,
        });
    }

    /// <inheritdoc />
    public async Task<PaginatedResponse<NotificationDto>> GetArchivedAsync(
        Guid userId, int page, int pageSize, CancellationToken ct = default)
    {
        var query = OwnedBy(userId)
            .Where(n => n.ArchivedAt != null)
            .AsNoTracking();

        var totalCount = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(n => n.ArchivedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PaginatedResponse<NotificationDto>
        {
            Items = rows.Select(MapDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <inheritdoc />
    public Task<bool> ArchiveByIdAsync(Guid userId, Guid notificationId, CancellationToken ct = default)
        => SetArchivedAsync(userId, notificationId, DateTime.UtcNow, ct);

    /// <inheritdoc />
    public Task<bool> UnarchiveByIdAsync(Guid userId, Guid notificationId, CancellationToken ct = default)
        => SetArchivedAsync(userId, notificationId, null, ct);

    /// <summary>
    /// Sets or clears ArchivedAt for one owned row. The user filter is the
    /// authorization check: another user's row is indistinguishable from a missing one.
    /// </summary>
    private async Task<bool> SetArchivedAsync(
        Guid userId, Guid notificationId, DateTime? archivedAt, CancellationToken ct)
    {
        var notification = await OwnedBy(userId).FirstOrDefaultAsync(n => n.Id == notificationId, ct);
        if (notification == null) return false;

        if (notification.ArchivedAt != archivedAt)
        {
            notification.ArchivedAt = archivedAt;
            await _unitOfWork.SaveChangesAsync();
        }

        return true;
    }

    /// <summary>
    /// The preference category a stored type belongs to, or an empty string when the registry does
    /// not know it. Unknown types are tolerated on purpose: an inbox must still render a row written
    /// by an older build rather than failing the whole page for it.
    /// </summary>
    private static string ResolveCategory(string type) =>
        TryResolveDefinition(type)?.Category ?? string.Empty;

    /// <summary>The registry entry for a stored type, or null when this build no longer knows it.</summary>
    private static NotificationTypeDef? TryResolveDefinition(string type)
    {
        try
        {
            return NotificationTypes.Get(NotificationTypeAliases.Resolve(type));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Projects a stored row to the wire shape, rendering the readable title and body from the
    /// in-app template and deciding which resource the notification links to.
    /// </summary>
    private NotificationDto MapDto(Notification notification)
    {
        var payload = ParsePayload(notification.PayloadJson);
        var canonical = NotificationTypeAliases.Resolve(notification.Type);
        var definition = TryResolveDefinition(notification.Type);
        // A type this build no longer registers still renders — with an empty title and body, which
        // the client fills from the type itself — instead of failing the whole page.
        NotificationTemplates.TryGet(canonical, NotificationChannel.InApp, out var template);

        var dto = new NotificationDto
        {
            Id = notification.Id,
            Type = canonical,
            Category = definition?.Category ?? string.Empty,
            Title = template?.Title ?? string.Empty,
            Body = template != null && _renderer != null
                ? _renderer.RenderText(template.Body, payload)
                : string.Empty,
            BusinessId = notification.BusinessId,
            AppointmentId = notification.AppointmentId,
            StampsCount = notification.StampsCount,
            IsRead = notification.IsRead,
            CreatedAt = notification.CreatedAt,
            ArchivedAt = notification.ArchivedAt,
            Payload = payload,
        };

        // The appointment id is a first-class column; a payment id lives in the payload. Resolving
        // it here means every client opens the same page for the same notification.
        if (notification.AppointmentId is Guid appointmentId)
        {
            dto.ResourceType = "appointment";
            dto.ResourceId = appointmentId;
        }
        else if (TryReadGuid(payload, "paymentId") is Guid paymentId)
        {
            dto.ResourceType = "payment";
            dto.ResourceId = paymentId;
        }

        return dto;
    }

    /// <summary>A payload value that holds a guid, as a guid — or null when it is absent or malformed.</summary>
    private static Guid? TryReadGuid(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value == null) return null;

        var text = value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString(),
        };

        return Guid.TryParse(text, out var parsed) ? parsed : null;
    }

    private static Dictionary<string, object?> ParsePayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(payload) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}