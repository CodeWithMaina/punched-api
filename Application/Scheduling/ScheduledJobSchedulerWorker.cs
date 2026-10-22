using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PunchedApi.Application.DomainEvents;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Scheduling;

/// <summary>
/// Claims due <c>scheduled_jobs</c> rows (10 s cadence, <c>FOR UPDATE SKIP LOCKED</c>,
/// 1-minute leases with stale reclaim) and dispatches them into the shared
/// notification pipeline. Dispatch is authoritative-state rechecked so a job can
/// never send a reminder for a cancelled or completed appointment, and per-
/// business reminder policies / opt-out toggles are respected.
/// </summary>
/// <remarks>
/// Behind the <c>WORKERS_ENABLED</c> gate.
/// </remarks>
public sealed class ScheduledJobSchedulerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduledJobSchedulerWorker> _logger;
    private readonly int _claimSize;
    private readonly int _leaseMinutes;
    private readonly int _pollSeconds;
    private readonly int _maxAttempts;
    private readonly int _lapseInactivityDays;
    private readonly int _winBackCooldownDays;
    private readonly bool _winBackFallbackNudgeEnabled;
    private DateTime _lastLapseScan = DateTime.MinValue;

    public ScheduledJobSchedulerWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<ScheduledJobSchedulerWorker> logger,
        IOptions<NotificationWorkerOptions>? options = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var o = options?.Value ?? new NotificationWorkerOptions();
        _claimSize = Math.Max(1, o.BatchSize);
        _leaseMinutes = Math.Max(1, o.StaleProcessingMinutes);
        _pollSeconds = Math.Max(1, o.PollIntervalSeconds);
        _maxAttempts = Math.Max(1, o.MaxJobAttempts);
        _lapseInactivityDays = Math.Max(1, o.LapseInactivityDays);
        _winBackCooldownDays = Math.Max(1, o.WinBackCooldownDays);
        _winBackFallbackNudgeEnabled = o.WinBackFallbackNudgeEnabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromSeconds(_pollSeconds);
        _logger.LogInformation("Scheduled-job scheduler started. Claim: {_ClaimSize} every {_PollSeconds}s.", _claimSize, _pollSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled-job batch failed.");
            }

            await Task.Delay(pollInterval, stoppingToken);
        }

        _logger.LogInformation("Scheduled-job scheduler stopped.");
    }

    public async Task ProcessBatchAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var context = provider.GetRequiredService<ApplicationDbContext>();

        await ReclaimStaleAsync(context, ct);
        await ScanForLapsedCustomersAsync(provider, context, ct);
        var claimed = await ClaimAsync(context, _claimSize, ct);
        foreach (var row in claimed)
        {
            ct.ThrowIfCancellationRequested();
            await new Dispatcher(
                row, provider, _logger, _winBackCooldownDays, _winBackFallbackNudgeEnabled)
                .RunAsync(ct);
        }
    }

    private async Task ReclaimStaleAsync(ApplicationDbContext context, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-Math.Max(1, _leaseMinutes));
        await context.ScheduledJobs
            .Where(j => j.Status == "processing" && j.LockedAt != null && j.LockedAt < cutoff)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.Status, "pending")
                .SetProperty(j => j.LockedAt, (DateTime?)null)
                .SetProperty(j => j.UpdatedAt, DateTime.UtcNow), ct);
    }

    // ===============================================================
    //  Lapsed-customer detection (customer.lapsed trigger source)
    //  Gated to at most once per hour so the enrollment scan does not
    //  repeat on every claim cycle.
    // ===============================================================

    private async Task ScanForLapsedCustomersAsync(
        IServiceProvider provider, ApplicationDbContext context, CancellationToken ct)
    {
        // Only scan once per hour to avoid repeated full-table scans. Cycle
        // idempotency does not depend on this throttle (the cycle dedupe key
        // does), so a restart merely scans once more.
        if (DateTime.UtcNow - _lastLapseScan < TimeSpan.FromHours(1)) return;

        var scanner = provider.GetRequiredService<LapsedCustomerScanner>();
        var result = await scanner.ScanAsync(
            DateTime.UtcNow, _lapseInactivityDays, _winBackCooldownDays, ct);

        // Only throttle after a successful pass: a transient failure should be
        // retried on the next claim cycle instead of silently skipping an hour.
        _lastLapseScan = DateTime.UtcNow;

        // The inserted lapse jobs stay tracked after each per-row SaveChanges.
        // ClaimAsync returns AsNoTracking copies of those same rows, so
        // Dispatcher.Complete (which re-attaches its row to write the terminal
        // state) would hit "another instance with the same key value is
        // already being tracked". The inserts are committed, so evicting them
        // here is safe and keeps the rest of the batch claimable in this scope.
        context.ChangeTracker.Clear();

        _logger.LogInformation(
            "Lapsed-customer scan complete: evaluated {Evaluated} enrolments, {Created} jobs created, " +
            "{Cooldown} in cooldown, {Duplicate} already scheduled.",
            result.Evaluated, result.Created, result.SkippedCooldown, result.SkippedDuplicate);
    }

    private static async Task<IReadOnlyList<ScheduledJob>> ClaimAsync(
        ApplicationDbContext context, int take, CancellationToken ct)
    {
        if (take <= 0) return Array.Empty<ScheduledJob>();

        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var ids = await ClaimPostgresIdsAsync(context, take, ct);
        if (ids.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return Array.Empty<ScheduledJob>();
        }

        var now = DateTime.UtcNow;
        await context.ScheduledJobs
            .Where(row => ids.Contains(row.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, "processing")
                .SetProperty(row => row.UpdatedAt, now)
                .SetProperty(row => row.LockedAt, now), ct);

        var claimed = await context.ScheduledJobs
            .AsNoTracking()
            .Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.ScheduledForUtc)
            .ToListAsync(ct);

        await transaction.CommitAsync(ct);
        return claimed;
    }

    private static async Task<List<Guid>> ClaimPostgresIdsAsync(
        ApplicationDbContext context, int take, CancellationToken ct)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id
            FROM scheduled_jobs
            WHERE status = 'pending'
              AND scheduled_for_utc <= now()
            ORDER BY scheduled_for_utc
            LIMIT @take
            FOR UPDATE SKIP LOCKED;
            """;
        command.Parameters.Add(new NpgsqlParameter("take", take));

        var ids = new List<Guid>(take);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetGuid(0));

        return ids;
    }

    sealed class Dispatcher
    {
        private readonly ScheduledJob _row;
        private readonly ApplicationDbContext _context;
        private readonly ILogger _logger;
        private readonly INotificationService _notifications;
        private readonly WinBackCooldownService _cooldown;
        private readonly IServiceProvider _provider;
        private readonly int _winBackCooldownDays;
        private readonly bool _winBackFallbackNudgeEnabled;

        public Dispatcher(
            ScheduledJob row,
            IServiceProvider provider,
            ILogger logger,
            int winBackCooldownDays,
            bool winBackFallbackNudgeEnabled)
        {
            _row = row;
            _context = provider.GetRequiredService<ApplicationDbContext>();
            _logger = logger;
            _notifications = provider.GetRequiredService<INotificationService>();
            _cooldown = provider.GetRequiredService<WinBackCooldownService>();
            _provider = provider;
            _winBackCooldownDays = Math.Max(1, winBackCooldownDays);
            _winBackFallbackNudgeEnabled = winBackFallbackNudgeEnabled;
        }

        public async Task RunAsync(CancellationToken ct = default)
        {
            if (!NotificationTypes.TryGet(_row.JobType, out _))
            {
                Complete("unknown_job_type", "skipped");
                await _context.SaveChangesAsync(ct);
                return;
            }

            var gate = await EvaluateGateAsync(_row, ct);
            if (!gate)
            {
                Complete("reminder_policy_disabled", "skipped");
                await _context.SaveChangesAsync(ct);
                return;
            }

            try
            {
                switch (_row.JobType)
                {
                    case "appointment.reminder":
                        await DispatchReminderAsync(ct);
                        break;
                    case "appointment.follow_up":
                        await DispatchFollowUpAsync(ct);
                        break;
                    case "subscription.expiring":
                        await DispatchSubscriptionExpiringAsync(ct);
                        break;
                    case "customer.lapsed":
                        await DispatchCustomerLapsedAsync(ct);
                        break;
                    default:
                        Complete("unsupported_job_type", "skipped");
                        break;
                }

                await _context.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled job {JobId} ({JobType}) failed.", _row.Id, _row.JobType);

                // A failed dispatch may have been rolled back mid-transaction,
                // leaving Added outbox rows tracked. Saving them now would push
                // a half-written notification outside the transaction, so drop
                // every tracked change first (the job row is re-attached below).
                _context.ChangeTracker.Clear();

                _row.Attempts++;
                var message = ex.Message ?? "scheduled job failed";
                _row.Error = message.Length > 500 ? message[..500] : message;
                _row.UpdatedAt = DateTime.UtcNow;
                _row.LockedAt = null;
                _row.Status = "pending";
                _context.ScheduledJobs.Update(_row);
                await _context.SaveChangesAsync(ct);
            }
        }

        private async Task DispatchReminderAsync(CancellationToken ct)
        {
            var appointmentId = _row.AggregateId;
            if (appointmentId == null)
            {
                Complete("appointment_not_found", "job cancelled");
                return;
            }

            var appointment = await _context.Appointments.FindAsync(new object?[] { appointmentId.Value }, ct);
            if (appointment == null)
            {
                Complete("appointment_not_found", "job cancelled");
                return;
            }

            if (!await AppointmentIsActiveAsync(appointment.Id, ct))
            {
                Complete("appointment_no_longer_active", "skipped");
                return;
            }

            if (appointment.ReminderOptOut)
            {
                Complete("reminder_opted_out", "skipped");
                _logger.LogDebug("Reminder skipped: customer opted out (appointment {Appointment}).", appointment.Id);
                return;
            }

            var businessName = await _context.Businesses
                .AsNoTracking()
                .Where(b => b.Id == appointment.BusinessId)
                .Select(b => b.Name)
                .FirstOrDefaultAsync(ct) ?? "Your business";

            // This job IS the due reminder: dispatch exactly this slot (authoritative-state
            // rechecked above), not the whole ladder. ScheduledForUtc carries when the
            // slot was due; the payload carries the real appointment time.
            var payload = new Dictionary<string, object?>
            {
                ["appointmentId"] = appointment.Id,
                ["businessName"] = businessName,
                ["scheduledAt"] = appointment.ScheduledAt.ToString("O")
            };
            var request = new NotificationRequest(
                "appointment.reminder",
                appointment.CustomerId,
                appointment.BusinessId,
                payload,
                _row.DedupeKey) { Force = true };

            try
            {
                await _notifications.QueueAsync(request, ct);
                Complete("dispatched", "sent");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reminder intent could not be queued for appointment {Appointment}.", appointment.Id);
                throw;
            }
        }

        private async Task DispatchFollowUpAsync(CancellationToken ct)
        {
            var appointmentId = _row.AggregateId;
            if (appointmentId == null)
            {
                Complete("appointment_not_found", "job cancelled");
                return;
            }

            var appointment = await _context.Appointments.FindAsync(new object?[] { appointmentId.Value }, ct);
            if (appointment == null)
            {
                Complete("appointment_not_found", "job cancelled");
                return;
            }

            if (appointment.Status != "completed")
            {
                Complete("appointment_not_completed", "skipped");
                return;
            }

            var businessName = await _context.Businesses
                .AsNoTracking()
                .Where(b => b.Id == appointment.BusinessId)
                .Select(b => b.Name)
                .FirstOrDefaultAsync(ct) ?? "Your business";

            var payload = new Dictionary<string, object?>
            {
                ["appointmentId"] = appointment.Id,
                ["businessName"] = businessName,
                ["scheduledAt"] = appointment.ScheduledAt.ToString("O")
            };
            var request = new NotificationRequest(
                "appointment.follow_up",
                appointment.CustomerId,
                appointment.BusinessId,
                payload,
                _row.DedupeKey) { Force = true };

            try
            {
                await _notifications.QueueAsync(request, ct);
                Complete("dispatched", "sent");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Follow-up intent could not be queued for appointment {Appointment}.", appointment.Id);
                throw;
            }
        }

        private async Task<bool> EvaluateGateAsync(ScheduledJob row, CancellationToken ct)
        {
            if (row.JobType == "subscription.expiring") return true;
            if (row.BusinessId == null) return true;
            var policy = await _context.BusinessReminderPolicies
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.BusinessId == row.BusinessId.Value, ct);
            if (policy is null || policy.Enabled) return true;
            _logger.LogDebug("Scheduled job {JobId} skipped: reminder policy disabled for {Business}.", row.Id, row.BusinessId);
            return false;
        }

        private async Task<bool> AppointmentIsActiveAsync(Guid appointmentId, CancellationToken ct)
        {
            var appointment = await _context.Appointments
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == appointmentId, ct);
            return appointment != null && appointment.Status is "booked" or "confirmed";
        }


        private async Task DispatchSubscriptionExpiringAsync(CancellationToken ct)
        {
            if (_row.BusinessId == null)
            {
                Complete("business_not_found", "job cancelled");
                return;
            }

            var business = await _context.Businesses.FindAsync(new object?[] { _row.BusinessId.Value }, ct);
            if (business == null)
            {
                Complete("business_not_found", "job cancelled");
                return;
            }

            if (business.OwnerId == null)
            {
                Complete("owner_not_found", "job cancelled");
                return;
            }

            // Recheck authoritative state: a renewed subscription must not be nagged.
            var subscription = await _context.BusinessSubscriptions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.BusinessId == business.Id, ct);
            if (subscription == null || subscription.Status is "expired" or "canceled")
            {
                Complete("subscription_no_longer_expiring", "skipped");
                return;
            }

            var payload = new Dictionary<string, object?>
            {
                ["businessName"] = business.Name,
                ["renewalDate"] = _row.ScheduledForUtc.ToString("O"),
                ["billingUrl"] = "https://billing.punched.app"
            };
            var request = new NotificationRequest(
                "subscription.expiring",
                business.OwnerId.Value,
                business.Id,
                payload,
                _row.DedupeKey) { Force = true };

            try
            {
                await _notifications.QueueAsync(request, ct);
                Complete("dispatched", "sent");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Subscription-expiring intent could not be queued for business {Business}.", business.Id);
                throw;
            }
        }

        private async Task DispatchCustomerLapsedAsync(CancellationToken ct)
        {
            if (_row.BusinessId == null)
            {
                Complete("business_not_found", "job cancelled");
                return;
            }

            // Payload carries the lapsed customer: { customerId, businessId, daysInactive }.
            var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(_row.PayloadJson) ?? new();
            if (!payload.TryGetValue("customerId", out var customerIdRaw) || customerIdRaw == null)
            {
                Complete("missing_customer_id", "job cancelled");
                return;
            }
            if (!Guid.TryParse(customerIdRaw.ToString(), out var customerId))
            {
                Complete("invalid_customer_id", "job cancelled");
                return;
            }

            var daysInactive = 0;
            if (payload.TryGetValue("daysInactive", out var daysRaw) && daysRaw != null)
            {
                int.TryParse(daysRaw.ToString(), out daysInactive);
            }

            // Serialise "cooldown check + write intent" per (business, customer).
            // PostgreSQL advisory locks are transaction-scoped, so two API
            // instances cannot both pass the cooldown check and double-send.
            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(ct)
                : null;

            if (_context.Database.IsNpgsql())
            {
                var lockKey = WinBackCyclePolicy.LockKey(customerId, _row.BusinessId.Value);
                await _context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
            }

            // Send-time cooldown: authoritative. The scan-time check is only an
            // optimisation to avoid noisy pending rows.
            var cooldownEndsAt = await _cooldown.GetCooldownEndsAtAsync(
                customerId, _row.BusinessId.Value, _winBackCooldownDays, ct);
            if (cooldownEndsAt.HasValue && cooldownEndsAt.Value > DateTime.UtcNow)
            {
                Defer(cooldownEndsAt.Value, "winback_cooldown");
                await _context.SaveChangesAsync(ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
                _logger.LogInformation(
                    "Win-back for customer {Customer} business {Business} deferred until {RetryAt:o}: cooldown still active.",
                    customerId, _row.BusinessId.Value, cooldownEndsAt.Value);
                return;
            }

            // Authoritative-state recheck: if the customer has had any qualifying
            // appointment since the lapse window opened, they are no longer
            // lapsed — cancel the job (reactivation).
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, daysInactive));
            var recentAppointment = await _context.Appointments
                .AsNoTracking()
                .AnyAsync(a =>
                    a.CustomerId == customerId
                    && a.BusinessId == _row.BusinessId.Value
                    && a.Status != "cancelled"
                    && a.Status != "no_show"
                    && a.ScheduledAt >= cutoff, ct);
            if (recentAppointment)
            {
                Complete("customer_reactivated", "skipped");
                await _context.SaveChangesAsync(ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
                return;
            }

            var businessName = await _context.Businesses
                .AsNoTracking()
                .Where(b => b.Id == _row.BusinessId.Value)
                .Select(b => b.Name)
                .FirstOrDefaultAsync(ct) ?? "Your business";

            var customerName = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == customerId)
                .Select(u => u.FullName ?? u.Email ?? "Customer")
                .FirstOrDefaultAsync(ct) ?? "Customer";

            var lapsePayload = new Dictionary<string, object?>
            {
                ["customerId"] = customerId,
                ["businessId"] = _row.BusinessId.Value,
                ["daysInactive"] = daysInactive,
                ["businessName"] = businessName,
                ["customerName"] = customerName,
            };

            // Evaluate automation rules matching customer.lapsed for this business.
            var evaluator = _provider.GetRequiredService<AutomationRuleEvaluator>();
            var fired = await evaluator.EvaluateAsync(
                _row.BusinessId.Value,
                "customer.lapsed",
                null, // no appointment aggregate for lapse events
                JsonSerializer.Serialize(lapsePayload),
                DateTime.UtcNow,
                ct);

            // No rule configured for the trigger (the common case): fall back to
            // the registered default nudge so win-back actually sends something.
            // Preferences and quiet hours still apply — never forced.
            if (fired == 0 && _winBackFallbackNudgeEnabled)
            {
                await _notifications.QueueAsync(new NotificationRequest(
                    WinBackCyclePolicy.JobType,
                    customerId,
                    _row.BusinessId.Value,
                    lapsePayload,
                    _row.DedupeKey), ct);
                _logger.LogInformation(
                    "Customer-lapsed fallback nudge queued for customer {Customer} business {Business}.",
                    customerId, _row.BusinessId.Value);
            }

            _logger.LogInformation(
                "Customer-lapsed rule evaluation for customer {Customer} business {Business}: {Fired} rule(s) fired.",
                customerId, _row.BusinessId.Value, fired);

            Complete("dispatched", "sent");
            await _context.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }

        private void Complete(string reason, string outcome)
        {
            _row.Status = "completed";
            _row.CompletedAt = DateTime.UtcNow;
            _row.UpdatedAt = DateTime.UtcNow;
            _row.LockedAt = null;
            _row.Error = outcome == "skipped" ? (string?)null : reason;
            _context.ScheduledJobs.Update(_row);
        }

        /// <summary>
        /// Returns the job to the queue for a later attempt without counting it
        /// as a failure. Used when a win-back must wait out the send cooldown.
        /// </summary>
        private void Defer(DateTime scheduledForUtc, string reason)
        {
            _row.Status = "pending";
            _row.ScheduledForUtc = scheduledForUtc;
            _row.CompletedAt = null;
            _row.LockedAt = null;
            _row.UpdatedAt = DateTime.UtcNow;
            _row.Error = reason;
            _context.ScheduledJobs.Update(_row);
        }

        // ══════════════════════════════════════════════════════════
        //  Scheduled-job payload helpers
        // ══════════════════════════════════════════════════════════

        private static List<int> ParseOffsets(string json)
        {
            var values = JsonSerializer.Deserialize<List<int>>(json) ?? new();
            return values.Where(m => m > 0).Distinct().OrderBy(m => m).ToList();
        }

        private static List<string> ParseChannels(string json)
        {
            var values = JsonSerializer.Deserialize<List<string>>(json) ?? new();
            return values.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
        }
    }
}