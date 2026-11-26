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

    /// <summary>
    /// One filtered, sorted, paged page of the caller's own inbox.
    ///
    /// <para>Returns <c>FORBIDDEN</c> when the query names a business the caller is not an active
    /// member of, so a client-supplied business id narrows the inbox and never widens it.</para>
    /// </summary>
    Task<ApiResponse<NotificationInboxPageDto>> GetInboxAsync(
        Guid userId, NotificationInboxQuery query, CancellationToken ct = default);

    /// <summary>Paged archived rows for the authenticated user (Phase 1.8).</summary>
    Task<PaginatedResponse<NotificationDto>> GetArchivedAsync(
        Guid userId, int page, int pageSize, CancellationToken ct = default);

    /// <summary>Archives one owned row; false when it does not exist for the user.</summary>
    Task<bool> ArchiveByIdAsync(Guid userId, Guid notificationId, CancellationToken ct = default);

    /// <summary>Restores one archived owned row; false when it does not exist for the user.</summary>
    Task<bool> UnarchiveByIdAsync(Guid userId, Guid notificationId, CancellationToken ct = default);
}