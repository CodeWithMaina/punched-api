using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PunchedApi.Application.BackgroundProcessing;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data;

/// <summary>
/// Raises a wake-up hint after a unit of work commits durable background work
/// (notification outbox rows, scheduled jobs, domain events, media rows).
///
/// <para>This is the reason deep-idle polling is viable: the producer's commit wakes
/// the worker immediately, so the polling interval can be minutes long without making
/// notifications slow. It runs <b>after</b> the commit — a wake-up can never be
/// delivered for a transaction that rolled back — and it never throws: a failed hint
/// costs one polling interval, never a failed booking.</para>
///
/// <para><see cref="AsyncLocal{T}"/> keeps the "did this unit of work touch outbox
/// state?" flag per async flow, so one interceptor instance is safe across concurrent
/// requests and across pooled DbContexts.</para>
/// </summary>
public sealed class OutboxWakeInterceptor : SaveChangesInterceptor
{
    private static readonly AsyncLocal<bool> TouchedOutbox = new();

    private readonly IPollWakeChannel? _wakeChannel;

    public OutboxWakeInterceptor(IPollWakeChannel? wakeChannel = null)
    {
        _wakeChannel = wakeChannel;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Observe(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Observe(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        PublishIfNeeded(result);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        PublishIfNeeded(result);
        return base.SavedChanges(eventData, result);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        TouchedOutbox.Value = false;
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        TouchedOutbox.Value = false;
        base.SaveChangesFailed(eventData);
    }

    private static void Observe(DbContext? context)
    {
        TouchedOutbox.Value = false;
        if (context is null) return;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;

            if (entry.Entity is NotificationLog or ScheduledJob or DomainEvent or Media)
            {
                TouchedOutbox.Value = true;
                return;
            }
        }
    }

    private void PublishIfNeeded(int result)
    {
        var touched = TouchedOutbox.Value;
        TouchedOutbox.Value = false;

        if (!touched || result <= 0) return;

        try
        {
            _wakeChannel?.Notify("outbox-commit");
        }
        catch
        {
            // A wake-up is an optimisation. Never let it fail a committed request.
        }
    }
}
