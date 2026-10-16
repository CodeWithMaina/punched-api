using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Management side of the suppression model (Phase 1.6): operators list, upsert and
/// remove per-(business, user, channel) operational pauses. Enforcement reads live in
/// <see cref="INotificationDispatchGuard"/> (dispatch) and the notification pipeline
/// (in-app acceptance) — never here. One active row per triple (unique index):
/// re-suppressing updates the existing row.
/// </summary>
public class NotificationSuppressionService
{
    private readonly ApplicationDbContext _context;

    public NotificationSuppressionService(ApplicationDbContext context) => _context = context;

    public async Task<List<NotificationSuppressionDto>> ListAsync(
        Guid? businessId,
        Guid? userId,
        string? channel,
        bool includeExpired,
        int limit = 100,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var query = _context.NotificationSuppressions.AsNoTracking();
        if (businessId.HasValue) query = query.Where(s => s.BusinessId == businessId.Value);
        if (userId.HasValue) query = query.Where(s => s.UserId == userId.Value);
        if (!string.IsNullOrWhiteSpace(channel)) query = query.Where(s => s.Channel == channel);
        if (!includeExpired)
            query = query.Where(s => s.ExpiresAt == null || s.ExpiresAt > now);

        var rows = await query
            .OrderByDescending(s => s.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);

        return rows.Select(ToDto).ToList();
    }

    public async Task<NotificationSuppressionDto?> UpsertAsync(
        UpsertNotificationSuppressionRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var row = await _context.NotificationSuppressions
            .FirstOrDefaultAsync(s => s.BusinessId == request.BusinessId
                && s.UserId == request.UserId
                && s.Channel == request.Channel, ct);

        if (row == null)
        {
            row = new NotificationSuppression
            {
                Id = Guid.NewGuid(),
                BusinessId = request.BusinessId,
                UserId = request.UserId,
                Channel = request.Channel,
                CreatedAt = now
            };
            await _context.NotificationSuppressions.AddAsync(row);
        }

        row.Reason = request.Reason.Trim();
        row.Source = "admin";
        row.ExpiresAt = request.ExpiresAtUtc;

        await _context.SaveChangesAsync(ct);
        return ToDto(row);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _context.NotificationSuppressions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (row == null) return false;

        _context.NotificationSuppressions.Remove(row);
        await _context.SaveChangesAsync(ct);
        return true;
    }

    private static NotificationSuppressionDto ToDto(NotificationSuppression row) => new()
    {
        Id = row.Id,
        BusinessId = row.BusinessId,
        UserId = row.UserId,
        Channel = row.Channel,
        Reason = row.Reason,
        Source = row.Source,
        ExpiresAtUtc = row.ExpiresAt,
        CreatedAtUtc = row.CreatedAt
    };
}