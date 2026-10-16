using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Plain service that flips overdue subscriptions to expired. Extracted from
/// the worker so it can be unit-tested deterministically against a fixed
/// "as of" timestamp.
/// </summary>
public class SubscriptionExpiryService
{
    private readonly ApplicationDbContext _context;
    private readonly IModuleEntitlementService _entitlements;
    private readonly ILogger<SubscriptionExpiryService> _logger;

    public SubscriptionExpiryService(
        ApplicationDbContext context,
        IModuleEntitlementService entitlements,
        ILogger<SubscriptionExpiryService> logger)
    {
        _context = context;
        _entitlements = entitlements;
        _logger = logger;
    }

    /// <summary>
    /// Expires every subscription with Status ∈ {active, trial, past_due}
    /// whose EndsAt is before <paramref name="asOf"/>. Subscriptions without
    /// a fixed EndsAt never expire. Invalidates the entitlement cache for
    /// each affected business.
    /// </summary>
    /// <returns>The number of businesses whose subscriptions were expired.</returns>
    public async Task<int> ExpireOverdueAsync(DateTime asOf, CancellationToken ct)
    {
        var overdue = await _context.BusinessSubscriptions
            .Where(s => (s.Status == "active" || s.Status == "trial" || s.Status == "past_due") &&
                s.EndsAt != null && s.EndsAt < asOf)
            .ToListAsync(ct);

        if (overdue.Count == 0) return 0;

        foreach (var row in overdue)
        {
            row.Status = "expired";
        }

        await _context.SaveChangesAsync(ct);

        var businessIds = overdue.Select(s => s.BusinessId).Distinct().ToList();
        foreach (var businessId in businessIds)
        {
            _entitlements.Invalidate(businessId);

            var job = new ScheduledJob
            {
                JobType = "subscription.expiring",
                // Same dedupe key every cycle → re-runs are idempotently skipped.
                DedupeKey = $"subexp:{businessId}",
                AggregateType = "business",
                AggregateId = businessId,
                BusinessId = businessId,
                PayloadJson = "{}",
                ScheduledForUtc = DateTime.UtcNow.AddHours(24),
                Status = "pending",
                Attempts = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await _context.ScheduledJobs.AddAsync(job, ct);
        }

        _logger.LogInformation(
            "Subscription expiry sweep: {Count} subscription(s) across {Businesses} business(es) expired.",
            overdue.Count, businessIds.Count);

        return businessIds.Count;
    }
}
