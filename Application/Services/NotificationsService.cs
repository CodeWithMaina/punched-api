using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using PunchedApi.Application.DTOs;
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

    public NotificationsService(
        IUnitOfWork unitOfWork,
        ApplicationDbContext context,
        ILogger<NotificationsService> logger,
        ITenantContext? tenant = null)
    {
        _unitOfWork = unitOfWork;
        _context = context;
        _logger = logger;
        _tenant = tenant;
    }

    /// <summary>Active tenant business id, or null on the platform root (legacy scoping).</summary>
    private Guid? TenantBusinessId => _tenant?.IsActive == true ? _tenant.BusinessId : null;

    /// <inheritdoc />
    public Task<int> GetUnreadCountAsync(Guid userId)
    {
        var query = _context.Notifications
            .Where(n => n.UserId == userId && !n.IsRead && n.ArchivedAt == null);
        if (TenantBusinessId is Guid tenantId)
            query = query.Where(n => n.BusinessId == null || n.BusinessId == tenantId);
        return query.CountAsync();
    }

    /// <inheritdoc />
    public async Task<bool> MarkReadByIdAsync(Guid userId, Guid notificationId)
    {
        // The user filter is the authorization check: another user's row is
        // indistinguishable from a missing one.
        var notifQuery = _context.Notifications
            .Where(n => n.Id == notificationId && n.UserId == userId);
        if (TenantBusinessId is Guid tenantId)
            notifQuery = notifQuery.Where(n => n.BusinessId == null || n.BusinessId == tenantId);

        var notification = await notifQuery.FirstOrDefaultAsync();

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
        var toUpdate = await _context.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
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
        var query = _context.Notifications
            .Where(n => n.UserId == userId)
            .Where(n => n.ArchivedAt == null) // archived rows leave the default list
            .AsNoTracking();
        if (TenantBusinessId is Guid tenantId)
            query = query.Where(n => n.BusinessId == null || n.BusinessId == tenantId);

        if (unreadOnly)
            query = query.Where(n => !n.IsRead);

        var notifications = await query
            .OrderByDescending(n => n.CreatedAt)
            .Take(limit)
            .ToListAsync();

        return notifications.Select(notification => new NotificationDto
            {
                Id = notification.Id,
                Type = notification.Type,
                BusinessId = notification.BusinessId,
                AppointmentId = notification.AppointmentId,
                StampsCount = notification.StampsCount,
                IsRead = notification.IsRead,
                CreatedAt = notification.CreatedAt,
                Payload = ParsePayload(notification.PayloadJson)
            })
            .ToList();
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