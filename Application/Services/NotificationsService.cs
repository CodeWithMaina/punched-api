using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

    public NotificationsService(
        IUnitOfWork unitOfWork,
        ApplicationDbContext context,
        ILogger<NotificationsService> logger)
    {
        _unitOfWork = unitOfWork;
        _context = context;
        _logger = logger;
    }

    public async Task CreateGoalReachedAsync(Guid userId, Guid? businessId, int stampsCount)
    {
        await _unitOfWork.Notifications.AddAsync(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BusinessId = businessId,
            Type = "GoalReached",
            StampsCount = stampsCount,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        });
        await _unitOfWork.SaveChangesAsync();
    }

        public async Task CreateRewardReadyAsync(Guid userId, Guid? businessId)
    {
        await _unitOfWork.Notifications.AddAsync(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BusinessId = businessId,
            Type = "RewardReady",
            StampsCount = 1,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        });
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task CreateAsync(Guid userId, Guid? businessId, string type, int stampsCount = 0)
    {
        await _unitOfWork.Notifications.AddAsync(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BusinessId = businessId,
            Type = type,
            StampsCount = stampsCount,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        });
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task CreateAsync(Guid userId, Guid? businessId, string type, Guid appointmentId, int stampsCount = 0)
    {
        await _unitOfWork.Notifications.AddAsync(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BusinessId = businessId,
            Type = type,
            AppointmentId = appointmentId,
            StampsCount = stampsCount,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        });
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task MarkReadAsync(Guid userId, Guid? notificationId = null)
    {
        var query = _context.Notifications.Where(n => n.UserId == userId);
        if (notificationId.HasValue)
            query = query.Where(n => n.Id == notificationId.Value);

        var toUpdate = await query.Where(n => !n.IsRead).ToListAsync();
        foreach (var n in toUpdate)
        {
            n.IsRead = true;
        }

        if (toUpdate.Count > 0)
            await _unitOfWork.SaveChangesAsync();
    }

    /// <inheritdoc />
    public Task<int> GetUnreadCountAsync(Guid userId) =>
        _context.Notifications
            .Where(n => n.UserId == userId && !n.IsRead && n.ArchivedAt == null)
            .CountAsync();

    /// <inheritdoc />
    public async Task<bool> MarkReadByIdAsync(Guid userId, Guid notificationId)
    {
        // The user filter is the authorization check: another user's row is
        // indistinguishable from a missing one.
        var notification = await _context.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId);

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

        if (unreadOnly)
            query = query.Where(n => !n.IsRead);

        return await query
            .OrderByDescending(n => n.CreatedAt)
            .Take(limit)
            .Select(n => new NotificationDto
            {
                Id = n.Id,
                Type = n.Type,
                BusinessId = n.BusinessId,
                AppointmentId = n.AppointmentId,
                StampsCount = n.StampsCount,
                IsRead = n.IsRead,
                CreatedAt = n.CreatedAt
            })
            .ToListAsync();
    }
}