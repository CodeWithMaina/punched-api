using Microsoft.EntityFrameworkCore;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Scheduling;

/// <summary>
/// Reads the win-back send history from the <c>notifications</c> outbox.
/// </summary>
/// <remarks>
/// <see cref="PunchedApi.Domain.Entities.NotificationLog"/> maps to the
/// <c>notifications</c> table (see <c>NotificationLogConfiguration</c>); a
/// successful external send is a row with <c>status = 'sent'</c>. In-app
/// deliveries are also recorded there, so a nudge on any channel starts the
/// cooldown. Only rows whose <c>template_type</c> is a win-back type count;
/// transactional messages (receipts, reminders) never block a nudge.
/// </remarks>
public sealed class WinBackCooldownService
{
    private readonly ApplicationDbContext _context;

    public WinBackCooldownService(ApplicationDbContext context)
    {
        _context = context;
    }

    /// <summary>Newest successful win-back send for the pair, or null.</summary>
    public async Task<DateTime?> GetLastSentAtAsync(
        Guid customerId, Guid businessId, CancellationToken ct = default)
    {
        return await _context.NotificationLogs
            .AsNoTracking()
            .Where(n => n.UserId == customerId
                && n.BusinessId == businessId
                && n.Status == "sent"
                && WinBackCyclePolicy.WinBackTemplateTypes.Contains(n.TemplateType))
            .MaxAsync(n => (DateTime?)n.SentAt, ct);
    }

    /// <summary>
    /// When the cooldown ends (null when the customer has never been nudged).
    /// Callers compare the result with UTC now; a future value means "wait".
    /// </summary>
    public async Task<DateTime?> GetCooldownEndsAtAsync(
        Guid customerId, Guid businessId, int cooldownDays, CancellationToken ct = default)
    {
        var lastSentAt = await GetLastSentAtAsync(customerId, businessId, ct);
        return lastSentAt?.AddDays(Math.Max(1, cooldownDays));
    }
}
