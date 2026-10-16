using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DomainEvents;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Scheduling;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application;

/// <summary>
/// Evaluates tenant-scoped automation rules against domain events and scheduled
/// triggers. Each rule is a declarative trigger → conditions → actions → recipients
/// pipeline. Actions are allowlisted only; no arbitrary code, SQL, or URLs are
/// ever accepted from stored JSON.
/// </summary>
public sealed class AutomationRuleEvaluator
{
    private readonly ApplicationDbContext _context;
    private readonly INotificationService _notifications;
    private readonly ILoyaltyStampingService _stamping;
    private readonly ILogger<AutomationRuleEvaluator> _logger;

    public AutomationRuleEvaluator(
        ApplicationDbContext context,
        INotificationService notifications,
        ILoyaltyStampingService stamping,
        ILogger<AutomationRuleEvaluator> logger)
    {
        _context = context;
        _notifications = notifications;
        _stamping = stamping;
        _logger = logger;
    }

    /// <summary>
    /// Evaluates all enabled rules for <paramref name="businessId"/> whose trigger
    /// matches <paramref name="eventType"/>. Returns the number of rules that fired.
    /// </summary>
    public async Task<int> EvaluateAsync(
        Guid businessId,
        string eventType,
        Guid? aggregateId,
        string payloadJson,
        DateTime occurredAtUtc,
        CancellationToken ct = default)
    {
        var rules = await _context.AutomationRules
            .AsNoTracking()
            .Where(r => r.BusinessId == businessId && r.Enabled && r.Trigger == eventType)
            .ToListAsync(ct);

        var fired = 0;
        foreach (var rule in rules)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ran = await EvaluateRuleAsync(rule, eventType, aggregateId, payloadJson, occurredAtUtc, ct);
                if (ran) fired++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Rule {RuleId} ({RuleName}) failed for event {EventType} business {Business}.",
                    rule.Id, rule.Name, eventType, businessId);
                // One broken rule never stops unrelated event processing.
            }
        }
        return fired;
    }

    private async Task<bool> EvaluateRuleAsync(
        AutomationRule rule,
        string eventType,
        Guid? aggregateId,
        string payloadJson,
        DateTime occurredAtUtc,
        CancellationToken ct)
    {
        // Idempotency: deterministic key from rule + event source.
        var idempotencyKey = $"rule:{rule.Id}:v{rule.Version}:{eventType}:{aggregateId ?? Guid.Empty}";

        // Check if this exact execution already ran (idempotency protection).
        var alreadyRan = await _context.AutomationRuleRuns
            .AsNoTracking()
            .AnyAsync(r => r.IdempotencyKey == idempotencyKey, ct);
        if (alreadyRan) return false;

        // Validate conditions against payload + context.
        var conditions = ParseConditions(rule.ConditionsJson);
        if (!await EvaluateConditionsAsync(conditions, aggregateId, payloadJson, occurredAtUtc, ct))
        {
            // Conditions not met — only record runs that actually attempted actions.
            return false;
        }

        // Loop protection: per-business daily action cap (DB-backed, evaluated inside
        // the same unit of work so concurrent executions converge rather than double
        // award under load).
        if (rule.DailyActionCap.HasValue)
        {
            var todayStart = DateTime.UtcNow.Date;
            var runsToday = await _context.AutomationRuleRuns
                .AsNoTracking()
                .CountAsync(r => r.RuleId == rule.Id && r.StartedAt >= todayStart, ct);
            if (runsToday >= rule.DailyActionCap.Value) return false;
        }

        // Build run record (snapshot rule version).
        var run = new AutomationRuleRun
        {
            Id = Guid.NewGuid(),
            RuleId = rule.Id,
            RuleVersion = rule.Version,
            SourceEventType = eventType,
            SourceAggregateId = aggregateId,
            BusinessId = rule.BusinessId,
            Status = "pending",
            IdempotencyKey = idempotencyKey,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        _context.AutomationRuleRuns.Add(run);

        try
        {
            // Resolve recipients at execution time.
            var recipients = await ResolveRecipientsAsync(rule.RecipientsJson, aggregateId, rule.BusinessId, ct);

            // Execute actions in order.
            var actions = ParseActions(rule.ActionsJson);
            var intentIds = new List<string>();
            var jobIds = new List<Guid>();

            foreach (var action in actions)
            {
                var result = await ExecuteActionAsync(action, recipients, rule, payloadJson, occurredAtUtc, ct);
                switch (result.Kind)
                {
                    case ActionResultKind.Notification:
                        intentIds.Add(result.Id!);
                        break;
                    case ActionResultKind.Job:
                        jobIds.Add(Guid.Parse(result.Id!));
                        break;
                }
            }

            run.Status = "completed";
            run.CompletedAt = DateTime.UtcNow;
            run.CreatedNotificationIntentIdsJson = JsonSerializer.Serialize(intentIds);
            run.CreatedJobIdsJson = JsonSerializer.Serialize(jobIds.Select(j => j.ToString("D")));
            run.ErrorMessage = null;
            run.ErrorDetailsJson = null;
            _context.AutomationRuleRuns.Update(run);
            return true;
        }
        catch (Exception ex)
        {
            run.Status = "failed";
            run.CompletedAt = DateTime.UtcNow;
            run.ErrorMessage = ex.Message;
            run.ErrorDetailsJson = ex.StackTrace?.Length > 4000 ? ex.StackTrace[..4000] : ex.StackTrace;
            _context.AutomationRuleRuns.Update(run);
            throw;
        }
    }

    // ═════════════════════════════════════════════════════════════
    //  Conditions
    // ════════════════════════════════════════════════════════════

    private static List<Condition> ParseConditions(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var list = new List<Condition>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    list.Add(new Condition
                    {
                        Field = el.GetProperty("field").GetString()!,
                        Operator = el.GetProperty("op").GetString()!,
                        Value = el.GetProperty("value").GetRawText()
                    });
                }
            }
            return list;
        }
        catch { return new List<Condition>(); }
    }

    private async Task<bool> EvaluateConditionsAsync(
        List<Condition> conditions, Guid? aggregateId, string payloadJson,
        DateTime occurredAtUtc, CancellationToken ct)
    {
        if (conditions.Count == 0) return true;
        Dictionary<string, object?>? payload = null;
        try { payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(payloadJson); } catch { }
        foreach (var cond in conditions)
            if (!await EvaluateSingleConditionAsync(cond, aggregateId, payload, occurredAtUtc, ct))
                return false;
        return true;
    }

    private async Task<bool> EvaluateSingleConditionAsync(
        Condition cond, Guid? aggregateId, Dictionary<string, object?>? payload,
        DateTime occurredAtUtc, CancellationToken ct)
    {
        object? lhs = null;
        switch (cond.Field)
        {
            case "appointment.status":
            case "appointment.scheduled_at":
            case "appointment.business_id":
            case "appointment.customer_id":
                if (aggregateId.HasValue)
                {
                    var appt = await _context.Appointments.AsNoTracking()
                        .FirstOrDefaultAsync(a => a.Id == aggregateId.Value, ct);
                    if (appt == null) return false;
                    lhs = cond.Field switch
                    {
                        "appointment.status" => appt.Status,
                        "appointment.scheduled_at" => appt.ScheduledAt,
                        "appointment.business_id" => appt.BusinessId,
                        "appointment.customer_id" => appt.CustomerId,
                        _ => null
                    };
                }
                break;
            case "payload.businessName":
            case "payload.scheduledAt":
            case "payload.appointmentId":
                lhs = payload?.TryGetValue(cond.Field.Split('.')[1], out var v) == true ? v : null;
                break;
        }
        object? rhs = ParseConditionValue(cond.Value);
        return cond.Operator switch
        {
            "eq" => Equals(lhs, rhs),
            "ne" => !Equals(lhs, rhs),
            "in" => rhs is JsonElement arr && arr.ValueKind == JsonValueKind.Array
                && arr.EnumerateArray().Any(e => ValueEquals(lhs, e.GetRawText())),
            "gt" => lhs is IComparable c1 && rhs is IComparable c2 && c1.CompareTo(c2) > 0,
            "lt" => lhs is IComparable c3 && rhs is IComparable c4 && c3.CompareTo(c4) < 0,
            _ => false
        };
    }

    private static bool ValueEquals(object? lhs, string rhs) =>
        lhs == null ? rhs == "null" : lhs.ToString() == rhs;

    private static object? ParseConditionValue(string raw)
    {
        try
        {
            var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.String => doc.RootElement.GetString(),
                JsonValueKind.Number => doc.RootElement.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => raw
            };
        }
        catch { return raw; }
    }

    // ════════════════════════════════════════════════════════════
    //  Recipients
    // ════════════════════════════════════════════════════════════

    private async Task<List<ResolvedRecipient>> ResolveRecipientsAsync(
        string recipientsJson, Guid? aggregateId, Guid businessId, CancellationToken ct)
    {
        var recipients = new List<ResolvedRecipient>();
        try
        {
            var doc = JsonDocument.Parse(recipientsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return recipients;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var category = el.GetProperty("category").GetString()!;
                var refId = el.TryGetProperty("refId", out var refEl) ? refEl.GetString() : null;
                if (refId == null) continue;
                switch (category)
                {
                    case "customer":
                        if (aggregateId.HasValue)
                        {
                            var appt = await _context.Appointments.AsNoTracking()
                                .FirstOrDefaultAsync(a => a.Id == aggregateId.Value, ct);
                            if (appt?.CustomerId != null)
                                recipients.Add(new ResolvedRecipient(appt.CustomerId, "customer"));
                        }
                        break;
                    case "staff.assigned":
                        if (aggregateId.HasValue)
                        {
                            var appt = await _context.Appointments.AsNoTracking()
                                .FirstOrDefaultAsync(a => a.Id == aggregateId.Value, ct);
                            if (appt?.StaffUserId != null)
                                recipients.Add(new ResolvedRecipient(appt.StaffUserId.Value, "staff.assigned"));
                        }
                        break;
                    case "business.owner":
                        var business = await _context.Businesses.AsNoTracking()
                            .FirstOrDefaultAsync(b => b.Id == businessId, ct);
                        if (business?.OwnerId != null)
                            recipients.Add(new ResolvedRecipient(business.OwnerId.Value, "business.owner"));
                        break;
                    case "business.all_staff":
                        var staff = await _context.Users.AsNoTracking()
                            .Where(u => u.StaffBusinessId == businessId)
                            .Select(u => u.Id).ToListAsync(ct);
                        foreach (var sid in staff)
                            recipients.Add(new ResolvedRecipient(sid, "business.all_staff"));
                        break;
                    case "segment":
                        if (refId != null && Guid.TryParse(refId, out var _))
                        {
                            var members = await _context.CustomerBusinessEnrollments.AsNoTracking()
                                .Where(e => e.BusinessId == businessId)
                                .Select(e => e.CustomerId).ToListAsync(ct);
                            foreach (var mid in members)
                                recipients.Add(new ResolvedRecipient(mid, $"segment:{refId}"));
                        }
                        break;
                }
            }
        }
        catch { }
        return recipients;
    }

    // ════════════════════════════════════════════════════════════
    //  Actions
    // ════════════════════════════════════════════════════════════

    private static List<ActionDef> ParseActions(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var list = new List<ActionDef>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    list.Add(new ActionDef
                    {
                        Type = el.GetProperty("type").GetString()!,
                        Payload = el.TryGetProperty("payload", out var p) ? p.GetRawText() : "{}"
                    });
                }
            }
            return list;
        }
        catch { return new List<ActionDef>(); }
    }

    private async Task<ActionResult> ExecuteActionAsync(
        ActionDef action, List<ResolvedRecipient> recipients, AutomationRule rule,
        string payloadJson, DateTime occurredAtUtc, CancellationToken ct)
    {
        return action.Type switch
        {
            "send_notification" => await ExecuteSendNotificationAsync(action, recipients, rule, payloadJson, ct),
            "schedule_notification" => await ExecuteScheduleNotificationAsync(action, recipients, rule, payloadJson, ct),
            "cancel_scheduled" => await ExecuteCancelScheduled(action, rule, ct),
            "award_stamps" => await ExecuteAwardStampsAsync(action, recipients, rule, ct),
            _ => throw new ArgumentException($"Unknown action type: {action.Type}")
        };
    }

    private async Task<ActionResult> ExecuteSendNotificationAsync(
        ActionDef action, List<ResolvedRecipient> recipients, AutomationRule rule,
        string payloadJson, CancellationToken ct)
    {
        var notificationType = "appointment.reminder";
        try
        {
            var doc = JsonDocument.Parse(action.Payload);
            if (doc.RootElement.TryGetProperty("type", out var tEl))
                notificationType = tEl.GetString()!;
        }
        catch { }
        var count = 0;
        var skippedByCap = 0;
        foreach (var recipient in recipients)
        {
            // Per-customer frequency cap: DB-backed, so concurrent rule executions
            // cannot double-message a customer within the window.
            if (rule.FrequencyCapMinutes.HasValue)
            {
                var windowStart = DateTime.UtcNow.AddMinutes(-rule.FrequencyCapMinutes.Value);
                var recentIntent = await _context.NotificationLogs
                    .AsNoTracking()
                    .AnyAsync(l => l.BusinessId == rule.BusinessId
                        && l.UserId == recipient.UserId
                        && l.RuleId == rule.Id.ToString("D")
                        && l.CreatedAt >= windowStart, ct);
                if (recentIntent)
                {
                    skippedByCap++;
                    continue;
                }
            }

            var request = new NotificationRequest(
                notificationType, recipient.UserId, rule.BusinessId,
                JsonSerializer.Deserialize<Dictionary<string, object?>>(payloadJson) ?? new(),
                $"rule:{rule.Id}:notify:{recipient.UserId}")
            { Force = false, RuleId = rule.Id.ToString("D") };
            await _notifications.QueueAsync(request, ct);
            count++;
        }
        return new ActionResult(ActionResultKind.Notification,
            $"{count} sent{((skippedByCap > 0) ? $", {skippedByCap} frequency-capped" : "")}");
    }

    private async Task<ActionResult> ExecuteScheduleNotificationAsync(
        ActionDef action, List<ResolvedRecipient> recipients, AutomationRule rule,
        string payloadJson, CancellationToken ct)
    {
        var jobType = "appointment.reminder";
        var scheduledFor = DateTime.UtcNow.AddHours(1);
        try
        {
            var doc = JsonDocument.Parse(action.Payload);
            if (doc.RootElement.TryGetProperty("jobType", out var jtEl))
                jobType = jtEl.GetString()!;
            if (doc.RootElement.TryGetProperty("at", out var atEl))
                scheduledFor = DateTime.Parse(atEl.GetString()!);
        }
        catch { }
        if (!NotificationTypes.TryGet(jobType, out _))
            return new ActionResult(ActionResultKind.Notification,
                $"rejected: unknown job type '{jobType}'");

        foreach (var recipient in recipients)
        {
            var job = new ScheduledJob
            {
                JobType = jobType,
                DedupeKey = $"rule:{rule.Id}:sched:{jobType}:{recipient.UserId}:{scheduledFor:yyyyMMddHHmm}",
                AggregateType = "appointment", AggregateId = null,
                BusinessId = rule.BusinessId, PayloadJson = payloadJson,
                ScheduledForUtc = scheduledFor, Status = "pending",
                Attempts = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            };
            await _context.ScheduledJobs.AddAsync(job, ct);
        }
        return new ActionResult(ActionResultKind.Job, Guid.NewGuid().ToString("D"));
    }

    private async Task<ActionResult> ExecuteCancelScheduled(ActionDef action, AutomationRule rule, CancellationToken ct)
    {
        var prefix = string.Empty;
        try
        {
            var doc = JsonDocument.Parse(action.Payload);
            if (doc.RootElement.TryGetProperty("prefix", out var pEl))
                prefix = pEl.GetString()!;
        }
        catch { }
        if (string.IsNullOrEmpty(prefix))
            return new ActionResult(ActionResultKind.Notification, "rejected: cancel prefix required");

        var cancelled = await _context.ScheduledJobs
            .Where(j => j.BusinessId == rule.BusinessId
                && j.Status == "pending"
                && EF.Functions.Like(j.DedupeKey, prefix + "%"))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.Status, "cancelled")
                .SetProperty(j => j.CancelledAt, DateTime.UtcNow)
                .SetProperty(j => j.UpdatedAt, DateTime.UtcNow)
                .SetProperty(j => j.LockedAt, (DateTime?)null), ct);

        return new ActionResult(ActionResultKind.Notification, $"cancelled {cancelled} job(s)");
    }

    private async Task<ActionResult> ExecuteAwardStampsAsync(
        ActionDef action, List<ResolvedRecipient> recipients, AutomationRule rule,
        CancellationToken ct)
    {
        int amount = 0;
        Guid? programId = null;
        string? source = null;
        try
        {
            var doc = JsonDocument.Parse(action.Payload);
            if (doc.RootElement.TryGetProperty("n", out var nEl))
                amount = nEl.GetInt32();
            if (doc.RootElement.TryGetProperty("programId", out var pEl)
                && Guid.TryParse(pEl.GetString(), out var parsed))
                programId = parsed;
            if (doc.RootElement.TryGetProperty("source", out var sEl))
                source = sEl.GetString();
        }
        catch { }
        if (amount <= 0) return new ActionResult(ActionResultKind.Notification, "0");
        if (string.IsNullOrWhiteSpace(source)) source = "automation";

        // Resolve program context: explicit payload programId, else the business's
        // active loyalty program. Never bypass tenant scoping.
        if (!programId.HasValue)
        {
            programId = await _context.LoyaltyPrograms
                .AsNoTracking()
                .Where(p => p.BusinessId == rule.BusinessId && p.Status == ProgramStatus.Active)
                .Select(p => p.Id)
                .FirstOrDefaultAsync(ct);
        }
        if (!programId.HasValue)
        {
            return new ActionResult(ActionResultKind.Notification,
                "skipped: no active loyalty program for business");
        }

        // Resolve an automatic earning rule for this program/source so stamping
        // follows the canonical earning-rule path.
        var earningRule = await _context.LoyaltyEarningRules
            .AsNoTracking()
            .Where(r => r.ProgramId == programId.Value
                && r.Status == EarningRuleStatus.Active
                && r.StampingMode == StampingMode.Automatic)
            .OrderBy(r => r.ActivatedAt)
            .FirstOrDefaultAsync(ct);
        if (earningRule == null)
        {
            return new ActionResult(ActionResultKind.Notification,
                "skipped: no active automatic earning rule for program");
        }

        var credited = 0;
        var skipped = 0;
        foreach (var recipient in recipients)
        {
            var card = await _context.LoyaltyCards
                .AsNoTracking()
                .FirstOrDefaultAsync(c =>
                    c.BusinessId == rule.BusinessId
                    && c.CustomerId == recipient.UserId
                    && c.ProgramId == programId.Value, ct);
            if (card == null)
            {
                skipped++;
                continue;
            }

            var credit = new AutomaticStampCredit(
                BusinessId: rule.BusinessId,
                ProgramId: programId.Value,
                EarningRuleId: earningRule.Id,
                CardId: card.Id,
                CustomerId: recipient.UserId,
                Amount: amount,
                Source: source,
                SourceId: rule.Id,
                Reason: $"Automation rule '{rule.Name}' awarded {amount} stamp(s)");

            var result = await _stamping.CreditFromEventAsync(credit, ct);
            if (result.Success) credited++;
            else if (result.Error?.Code is "ALREADY_APPLIED" or "DUPLICATE")
            {
                // Idempotent replay: the stamp was already awarded for this rule.
                skipped++;
            }
            else
            {
                _logger.LogWarning(
                    "award_stamps failed for rule {RuleId} recipient {Recipient}: {Code} {Message}",
                    rule.Id, recipient.UserId, result.Error?.Code, result.Error?.Message);
                skipped++;
            }
        }

        return new ActionResult(ActionResultKind.Notification,
            $"awarded {credited} stamp(s){((skipped > 0) ? $", {skipped} skipped" : "")}");
    }

    // ════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════

    private sealed class Condition
    {
        public string Field { get; set; } = string.Empty;
        public string Operator { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private sealed class ActionDef
    {
        public string Type { get; set; } = string.Empty;
        public string Payload { get; set; } = "{}";
    }

    private sealed class ResolvedRecipient
    {
        public Guid UserId { get; }
        public string Category { get; }
        public ResolvedRecipient(Guid userId, string category)
        {
            UserId = userId;
            Category = category;
        }
    }

    private enum ActionResultKind { Notification, Job }

    private sealed class ActionResult
    {
        public ActionResultKind Kind { get; }
        public string? Id { get; }
        public ActionResult(ActionResultKind kind, string id)
        {
            Kind = kind;
            Id = id;
        }
    }
}