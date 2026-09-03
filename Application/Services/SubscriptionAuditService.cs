using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// <see cref="ISubscriptionAuditService"/> implementation backed by the
/// append-only <c>subscription_audit_logs</c> table. All writes are fire-safe:
/// an audit failure is logged and does not break the primary mutation.
/// </summary>
public class SubscriptionAuditService : ISubscriptionAuditService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<SubscriptionAuditService> _logger;

    public SubscriptionAuditService(
        ApplicationDbContext context,
        ILogger<SubscriptionAuditService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RecordAsync(
        string action,
        Guid? actorUserId,
        Guid? targetBusinessId,
        Guid? targetPlanId,
        string? payloadJson = null,
        string? reason = null)
    {
        try
        {
            var entry = new SubscriptionAuditLog
            {
                Id = Guid.NewGuid(),
                Action = action,
                ActorUserId = actorUserId,
                TargetBusinessId = targetBusinessId,
                TargetPlanId = targetPlanId,
                PayloadJson = payloadJson,
                Reason = reason,
                CreatedAt = DateTime.UtcNow
            };

            _context.SubscriptionAuditLogs.Add(entry);
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Subscription audit recorded: action={Action} actor={ActorUserId} business={TargetBusinessId} plan={TargetPlanId}",
                entry.Action, actorUserId, targetBusinessId, targetPlanId);
        }
        catch (Exception ex)
        {
            // Audit writes must never break the primary mutation. Log and swallow.
            _logger.LogError(
                ex,
                "Subscription audit write FAILED (action={Action} actor={ActorUserId} business={TargetBusinessId} plan={TargetPlanId}). " +
                "Audit failure does not fail the primary operation.",
                action, actorUserId, targetBusinessId, targetPlanId);
        }
    }
}