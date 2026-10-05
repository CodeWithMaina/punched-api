using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Service for creating and querying in-app staff notifications.
/// </summary>
public interface INotificationsService
{
    Task<List<NotificationDto>> GetAsync(Guid userId, bool unreadOnly, int limit = 50);
    Task<int> GetUnreadCountAsync(Guid userId);
    Task<bool> MarkReadByIdAsync(Guid userId, Guid notificationId);
    Task<int> MarkAllReadAsync(Guid userId);
}