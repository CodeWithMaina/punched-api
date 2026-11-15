using Microsoft.Extensions.Logging;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// The single way a financial audit entry is written.
///
/// <para><b>Why it is static and shared.</b> Income, expenses, refunds and reconciliation all
/// change the books, and every one of them must leave the same quality of trail — who, what,
/// when, before and after. Keeping the write in one helper means a new financial operation
/// cannot quietly ship without one.</para>
///
/// <para><b>Failure is logged, never fatal.</b> The audit entry is written after the change it
/// describes has committed; failing the caller because the trail could not be appended would
/// punish the business for the platform's problem.</para>
/// </summary>
internal static class FinancialAudit
{
    public static async Task WriteAsync(
        ApplicationDbContext context,
        IUnitOfWork uow,
        ILogger logger,
        Guid businessId,
        string entityType,
        Guid entityId,
        string action,
        Guid? actorUserId,
        string? reference,
        string? note,
        string? changesJson)
    {
        try
        {
            await context.FinancialAuditLogs.AddAsync(new FinancialAuditLog
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                EntityType = entityType,
                EntityId = entityId,
                Action = action,
                ActorUserId = actorUserId,
                ActorName = await FinancialAccess.ActorNameAsync(context, actorUserId),
                Reference = reference,
                Note = FinancialLedgerService.Truncate(note, 500),
                ChangesJson = changesJson,
                CreatedAt = DateTime.UtcNow
            });

            await uow.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Financial audit entry {Action} for {EntityType} {EntityId} could not be written.",
                action, entityType, entityId);
        }
    }
}
