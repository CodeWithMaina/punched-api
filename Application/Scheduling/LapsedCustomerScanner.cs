using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Scheduling;

/// <summary>Counters returned by one lapse scan (used for logs and tests).</summary>
public sealed record WinBackScanResult(
    int Evaluated,
    int Lapsed,
    int Created,
    int SkippedCooldown,
    int SkippedDuplicate);

/// <summary>
/// Walks active customer enrolments and materialises one <c>customer.lapsed</c>
/// job per lapsed (customer, business) pair.
/// </summary>
/// <remarks>
/// Extracted from <see cref="ScheduledJobSchedulerWorker"/> so the cycle policy
/// can be tested without a host. The scan is idempotent:
/// <list type="number">
/// <item>a pending/processing job for the pair suppresses any new insert,</item>
/// <item>an existing row with the same cycle key (any status) suppresses the
/// insert, and</item>
/// <item>an active send cooldown suppresses the insert until it elapses.</item>
/// </list>
/// </remarks>
public sealed class LapsedCustomerScanner
{
    private readonly ApplicationDbContext _context;
    private readonly WinBackCooldownService _cooldown;
    private readonly ILogger<LapsedCustomerScanner> _logger;

    public LapsedCustomerScanner(
        ApplicationDbContext context,
        WinBackCooldownService cooldown,
        ILogger<LapsedCustomerScanner> logger)
    {
        _context = context;
        _cooldown = cooldown;
        _logger = logger;
    }

    public async Task<WinBackScanResult> ScanAsync(
        DateTime nowUtc,
        int lapseInactivityDays,
        int cooldownDays,
        CancellationToken ct = default)
    {
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var lapseDays = Math.Max(1, lapseInactivityDays);
        var cooldown = Math.Max(1, cooldownDays);

        // Group in SQL so a customer with duplicate enrolments is evaluated once.
        var enrollments = await _context.CustomerBusinessEnrollments
            .AsNoTracking()
            .Where(e => e.Status == CustomerBusinessEnrollmentStatus.Active && e.LeftAt == null)
            .GroupBy(e => new { e.CustomerId, e.BusinessId })
            .Select(g => new
            {
                g.Key.CustomerId,
                g.Key.BusinessId,
                EnrolledAt = g.Min(x => x.EnrolledAt)
            })
            .ToListAsync(ct);

        var candidates = new List<Candidate>();
        var lapsed = 0;
        var skippedCooldown = 0;
        var skippedDuplicate = 0;

        foreach (var enrollment in enrollments)
        {
            ct.ThrowIfCancellationRequested();

            var anchor = await ResolveAnchorAsync(
                enrollment.CustomerId, enrollment.BusinessId, enrollment.EnrolledAt, ct);

            if (!WinBackCyclePolicy.IsLapsed(anchor, nowUtc, lapseDays))
                continue;
            lapsed++;

            var prefix = WinBackCyclePolicy.DedupeKeyPrefix(
                enrollment.CustomerId, enrollment.BusinessId);

            // One outstanding nudge per pair — a deferred job must not be
            // duplicated when the next cycle boundary passes.
            var hasOutstanding = await _context.ScheduledJobs
                .AsNoTracking()
                .AnyAsync(j => (j.Status == "pending" || j.Status == "processing")
                    && j.DedupeKey.StartsWith(prefix), ct);
            if (hasOutstanding)
            {
                skippedDuplicate++;
                continue;
            }

            var cycleIndex = WinBackCyclePolicy.ComputeCycleIndex(anchor, nowUtc, cooldown);
            var dedupeKey = WinBackCyclePolicy.BuildDedupeKey(
                enrollment.CustomerId, enrollment.BusinessId, anchor, cycleIndex);

            // The unique index spans every status: a completed nudge in this
            // exact cycle must never be re-inserted.
            var alreadyExists = await _context.ScheduledJobs
                .AsNoTracking()
                .AnyAsync(j => j.DedupeKey == dedupeKey, ct);
            if (alreadyExists)
            {
                skippedDuplicate++;
                continue;
            }

            var cooldownEndsAt = await _cooldown.GetCooldownEndsAtAsync(
                enrollment.CustomerId, enrollment.BusinessId, cooldown, ct);
            if (cooldownEndsAt.HasValue && cooldownEndsAt.Value > nowUtc)
            {
                skippedCooldown++;
                continue;
            }

            candidates.Add(new Candidate(
                enrollment.CustomerId,
                enrollment.BusinessId,
                anchor,
                cycleIndex,
                dedupeKey,
                (int)(nowUtc - anchor).TotalDays));
        }


        if (candidates.Count == 0)
        {
            return new WinBackScanResult(
                enrollments.Count, lapsed, 0, skippedCooldown, skippedDuplicate);
        }

        var (businessNames, customerNames) = await LoadNamesAsync(candidates, ct);

        var created = 0;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var job = new ScheduledJob
            {
                JobType = WinBackCyclePolicy.JobType,
                DedupeKey = candidate.DedupeKey,
                AggregateType = null,
                AggregateId = null,
                BusinessId = candidate.BusinessId,
                PayloadJson = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["customerId"] = candidate.CustomerId,
                    ["businessId"] = candidate.BusinessId,
                    ["daysInactive"] = candidate.DaysInactive,
                    ["cycleIndex"] = candidate.CycleIndex,
                    ["cycleAnchorUtc"] = candidate.AnchorUtc.ToString("O"),
                    ["businessName"] = businessNames.GetValueOrDefault(
                        candidate.BusinessId, "Your business"),
                    ["customerName"] = customerNames.GetValueOrDefault(
                        candidate.CustomerId, "Customer"),
                }),
                ScheduledForUtc = nowUtc,
                Status = "pending",
                Attempts = 0,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            };

            _context.ScheduledJobs.Add(job);
            try
            {
                // Save per row so one concurrent duplicate cannot roll back the
                // whole scan on the unique index.
                await _context.SaveChangesAsync(ct);
                created++;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                _context.Entry(job).State = EntityState.Detached;
                skippedDuplicate++;
                _logger.LogDebug(ex,
                    "Win-back job for customer {Customer} business {Business} was inserted concurrently.",
                    candidate.CustomerId, candidate.BusinessId);
            }
        }

        return new WinBackScanResult(
            enrollments.Count, lapsed, created, skippedCooldown, skippedDuplicate);
    }

    /// <summary>
    /// Last qualifying appointment time, or the enrolment time when the
    /// customer has never booked. Future appointments anchor the customer as
    /// active (elapsed is negative), which also suppresses the nudge.
    /// </summary>
    private async Task<DateTime> ResolveAnchorAsync(
        Guid customerId, Guid businessId, DateTime enrolledAt, CancellationToken ct)
    {
        var lastActivity = await _context.Appointments
            .AsNoTracking()
            .Where(a => a.CustomerId == customerId
                && a.BusinessId == businessId
                && a.Status != "cancelled"
                && a.Status != "no_show")
            .MaxAsync(a => (DateTime?)a.ScheduledAt, ct);

        return lastActivity ?? enrolledAt;
    }

    private async Task<(Dictionary<Guid, string> Businesses, Dictionary<Guid, string> Customers)>
        LoadNamesAsync(List<Candidate> candidates, CancellationToken ct)
    {
        var businessIds = candidates.Select(c => c.BusinessId).Distinct().ToList();
        var customerIds = candidates.Select(c => c.CustomerId).Distinct().ToList();

        var businesses = await _context.Businesses
            .AsNoTracking()
            .Where(b => businessIds.Contains(b.Id))
            .Select(b => new { b.Id, b.Name })
            .ToDictionaryAsync(b => b.Id, b => b.Name, ct);

        var customers = await _context.Users
            .AsNoTracking()
            .Where(u => customerIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, u.Email })
            .ToListAsync(ct);

        return (businesses,
            customers.ToDictionary(
                u => u.Id, u => u.FullName ?? u.Email ?? "Customer"));
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation }
        || (ex.InnerException?.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) ?? false);

    private sealed record Candidate(
        Guid CustomerId,
        Guid BusinessId,
        DateTime AnchorUtc,
        int CycleIndex,
        string DedupeKey,
        int DaysInactive);
}
