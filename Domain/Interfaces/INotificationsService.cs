using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Service for creating and querying in-app staff notifications.
/// </summary>
public interface INotificationsService
{
        Task CreateGoalReachedAsync(Guid userId, Guid? businessId, int stampsCount);
    Task CreateRewardReadyAsync(Guid userId, Guid? businessId);
    Task MarkReadAsync(Guid userId, Guid? notificationId = null);
    Task<List<NotificationDto>> GetAsync(Guid userId, bool unreadOnly, int limit = 50);

    /// <summary>Generic notification creation — used for customer-facing events (e.g. card adjustments).</summary>
    Task CreateAsync(Guid userId, Guid? businessId, string type, int stampsCount = 0);

    /// <summary>
    /// Generic notification creation with an appointment context so clients can
    /// deep-link to the affected appointment (reschedule requests/decisions).
    /// </summary>
    Task CreateAsync(Guid userId, Guid? businessId, string type, Guid appointmentId, int stampsCount = 0);

    // ── Inbox read model (Phase 1 of the notification module) ────────────────
    // These are additive: the creation methods above stay byte-for-byte
    // compatible and now delegate to INotificationService.SendAsync.

    /// <summary>
    /// Unread count for the caller's inbox — a badge no longer needs to fetch up
    /// to 50 rows to compute it.
    /// </summary>
    Task<int> GetUnreadCountAsync(Guid userId);

    /// <summary>
    /// Marks one notification read. Returns <c>false</c> when the row does not
    /// exist or belongs to another user, so the caller can answer 404 instead of
    /// leaking existence.
    /// </summary>
    Task<bool> MarkReadByIdAsync(Guid userId, Guid notificationId);

    /// <summary>Marks every unread notification of the caller read; returns how many changed.</summary>
    Task<int> MarkAllReadAsync(Guid userId);
}