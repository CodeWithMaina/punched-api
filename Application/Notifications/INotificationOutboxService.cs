using System.ComponentModel.DataAnnotations;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Notifications;

/// <summary>
/// Transactional notification-intent queue. Producers call <see cref="QueueAsync"/>
/// inside their own unit of work; the intent row is persisted atomically with the
/// domain change on the caller's <see cref="IUnitOfWork.SaveChangesAsync"/>.
/// </summary>
public interface INotificationOutboxService
{
    /// <summary>Queue an intent to be delivered. <paramref name="deliverAfterUtc"/> may be null (now).</summary>
    Task<NotificationLog> QueueAsync(
        Guid userId,
        Guid? businessId,
        string templateType,
        string channel,
        string templateKey,
        IReadOnlyDictionary<string, object?> payload,
        DateTime? deliverAfterUtc = null,
        string? ruleId = null,
        string? sourceJobId = null,
        CancellationToken ct = default);

    /// <summary>Mark an intent as sent (terminal) after successful external dispatch.</summary>
    Task MarkSentAsync(NotificationLog row, DateTime deliveredAtUtc, CancellationToken ct = default);

    /// <summary>Mark a row as processing (transitioning out of pending).</summary>
    Task StartProcessingAsync(NotificationLog row, CancellationToken ct = default);
}
