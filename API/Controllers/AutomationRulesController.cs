using System.Linq;
using System.Text.Json;
using PunchedApi.Application.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Business-scoped automation rule management (Phase 3).
/// Base route: /v1/automation/rules
///
/// Rules are owned by exactly one business (tenant scope enforced by ITenantContext
/// and by server-side ownership checks on every endpoint). Preview-send is
/// rate-limited and cannot become a bulk-send mechanism.
/// </summary>
[ApiController]
[Route("v1/automation/rules")]
[Produces("application/json")]
[Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
[EnableRateLimiting("general")]
public sealed class AutomationRulesController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly AutomationRuleEvaluator _evaluator;
    private readonly INotificationService _notifications;
    private readonly ITenantContext _tenant;
    private readonly ILogger<AutomationRulesController> _logger;

    public AutomationRulesController(
        ApplicationDbContext context,
        AutomationRuleEvaluator evaluator,
        INotificationService notifications,
        ITenantContext tenant,
        ILogger<AutomationRulesController> logger)
    {
        _context = context;
        _evaluator = evaluator;
        _notifications = notifications;
        _tenant = tenant;
        _logger = logger;
    }

    private Guid? BusinessId => _tenant.IsActive ? _tenant.BusinessId : null;

    private Guid? GetUserId()
    {
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    /// <summary>List automation rules for the caller's business.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<List<AutomationRuleDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListRules(CancellationToken ct = default)
    {
        var businessId = BusinessId;
        if (!businessId.HasValue)
            return BadRequest(ApiResponse<List<AutomationRuleDto>>.Fail(
                "NO_TENANT", "No business tenant context for this request."));

        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (!await IsMemberOfBusinessAsync(userId.Value, businessId.Value, ct))
            return Forbid();

        var rules = await _context.AutomationRules
            .AsNoTracking()
            .Where(r => r.BusinessId == businessId.Value)
            .OrderByDescending(r => r.UpdatedAt)
            .Select(r => AutomationRuleDto.FromEntity(r))
            .ToListAsync(ct);

        return Ok(ApiResponse<List<AutomationRuleDto>>.Ok(rules));
    }

    /// <summary>Get one automation rule by id (business-scoped).</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AutomationRuleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRule(Guid id, CancellationToken ct = default)
    {
        var businessId = BusinessId;
        if (!businessId.HasValue)
            return BadRequest(ApiResponse<AutomationRuleDto>.Fail("NO_TENANT", "No business tenant context."));

        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        if (!await IsMemberOfBusinessAsync(userId.Value, businessId.Value, ct))
            return Forbid();

        var rule = await _context.AutomationRules
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.BusinessId == businessId.Value, ct);

        if (rule == null) return NotFound(ApiResponse<AutomationRuleDto>.Fail("NOT_FOUND", "Rule not found."));

        return Ok(ApiResponse<AutomationRuleDto>.Ok(AutomationRuleDto.FromEntity(rule)));
    }

    /// <summary>Create an automation rule (disabled-safe by default).</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<AutomationRuleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateRule(
        [FromBody] UpsertAutomationRuleRequest request,
        CancellationToken ct = default)
    {
        var businessId = BusinessId;
        if (!businessId.HasValue)
            return BadRequest(ApiResponse<AutomationRuleDto>.Fail("NO_TENANT", "No business tenant context."));

        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        if (!await IsMemberOfBusinessAsync(userId.Value, businessId.Value, ct))
            return Forbid();

        if (!ModelState.IsValid)
            return BadRequest(ApiResponse<AutomationRuleDto>.Fail("VALIDATION_ERROR", "Invalid rule definition."));

        if (!IsKnownTrigger(request.Trigger))
            return BadRequest(ApiResponse<AutomationRuleDto>.Fail("VALIDATION_ERROR", $"Unknown trigger '{request.Trigger}'."));

        var definitionError = ValidateRuleDefinition(request.ConditionsJson, request.ActionsJson, request.RecipientsJson);
        if (definitionError != null)
            return BadRequest(ApiResponse<AutomationRuleDto>.Fail("INVALID_RULE_DEFINITION", definitionError));

        var exists = await _context.AutomationRules
            .AsNoTracking()
            .AnyAsync(r => r.BusinessId == businessId.Value && r.Name == request.Name, ct);
        if (exists)
            return BadRequest(ApiResponse<AutomationRuleDto>.Fail("DUPLICATE_NAME", "A rule with this name already exists."));

        var rule = new AutomationRule
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId.Value,
            Name = request.Name,
            Description = request.Description,
            Trigger = request.Trigger,
            ConditionsJson = request.ConditionsJson,
            ActionsJson = request.ActionsJson,
            RecipientsJson = request.RecipientsJson,
            Enabled = request.Enabled,
            Version = 1,
            DailyActionCap = request.DailyActionCap,
            FrequencyCapMinutes = request.FrequencyCapMinutes,
            QuietHoursStart = request.QuietHoursStart,
            QuietHoursEnd = request.QuietHoursEnd,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.AutomationRules.Add(rule);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Automation rule {RuleId} ({RuleName}) created for business {Business}.", rule.Id, rule.Name, businessId.Value);

        return Ok(ApiResponse<AutomationRuleDto>.Ok(AutomationRuleDto.FromEntity(rule)));
    }

    /// <summary>Update an automation rule (business-scoped).</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AutomationRuleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateRule(
        Guid id,
        [FromBody] UpsertAutomationRuleRequest request,
        CancellationToken ct = default)
    {
        var businessId = BusinessId;
        if (!businessId.HasValue)
            return BadRequest(ApiResponse<AutomationRuleDto>.Fail("NO_TENANT", "No business tenant context."));

        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        if (!await IsMemberOfBusinessAsync(userId.Value, businessId.Value, ct))
            return Forbid();

        if (!ModelState.IsValid)
            return BadRequest(ApiResponse<AutomationRuleDto>.Fail("VALIDATION_ERROR", "Invalid rule definition."));

        if (!IsKnownTrigger(request.Trigger))
            return BadRequest(ApiResponse<AutomationRuleDto>.Fail("VALIDATION_ERROR", $"Unknown trigger '{request.Trigger}'."));

        var definitionError = ValidateRuleDefinition(request.ConditionsJson, request.ActionsJson, request.RecipientsJson);
        if (definitionError != null)
            return BadRequest(ApiResponse<AutomationRuleDto>.Fail("INVALID_RULE_DEFINITION", definitionError));

        var rule = await _context.AutomationRules
            .FirstOrDefaultAsync(r => r.Id == id && r.BusinessId == businessId.Value, ct);
        if (rule == null) return NotFound(ApiResponse<AutomationRuleDto>.Fail("NOT_FOUND", "Rule not found."));

        var changed = rule.Name != request.Name || rule.Trigger != request.Trigger
            || rule.ConditionsJson != request.ConditionsJson || rule.ActionsJson != request.ActionsJson
            || rule.RecipientsJson != request.RecipientsJson;

        rule.Name = request.Name;
        rule.Description = request.Description;
        rule.Trigger = request.Trigger;
        rule.ConditionsJson = request.ConditionsJson;
        rule.ActionsJson = request.ActionsJson;
        rule.RecipientsJson = request.RecipientsJson;
        rule.Enabled = request.Enabled;
        rule.DailyActionCap = request.DailyActionCap;
        rule.FrequencyCapMinutes = request.FrequencyCapMinutes;
        rule.QuietHoursStart = request.QuietHoursStart;
        rule.QuietHoursEnd = request.QuietHoursEnd;
        rule.UpdatedAt = DateTime.UtcNow;
        if (changed) rule.Version++;

        await _context.SaveChangesAsync(ct);

        return Ok(ApiResponse<AutomationRuleDto>.Ok(AutomationRuleDto.FromEntity(rule)));
    }

    /// <summary>Enable or disable an automation rule.</summary>
    [HttpPost("{id:guid}/toggle")]
    [ProducesResponseType(typeof(ApiResponse<AutomationRuleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleRule(
        Guid id,
        [FromQuery] bool enable,
        CancellationToken ct = default)
    {
        var businessId = BusinessId;
        if (!businessId.HasValue)
            return BadRequest(ApiResponse<AutomationRuleDto>.Fail("NO_TENANT", "No business tenant context."));

        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        if (!await IsMemberOfBusinessAsync(userId.Value, businessId.Value, ct))
            return Forbid();

        var rule = await _context.AutomationRules
            .FirstOrDefaultAsync(r => r.Id == id && r.BusinessId == businessId.Value, ct);
        if (rule == null) return NotFound(ApiResponse<AutomationRuleDto>.Fail("NOT_FOUND", "Rule not found."));

        rule.Enabled = enable;
        rule.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        return Ok(ApiResponse<AutomationRuleDto>.Ok(AutomationRuleDto.FromEntity(rule)));
    }

    /// <summary>Archive (soft-delete) an automation rule.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ArchiveRule(Guid id, CancellationToken ct = default)
    {
        var businessId = BusinessId;
        if (!businessId.HasValue)
            return BadRequest(ApiResponse<string>.Fail("NO_TENANT", "No business tenant context."));

        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        if (!await IsMemberOfBusinessAsync(userId.Value, businessId.Value, ct))
            return Forbid();

        var rule = await _context.AutomationRules
            .FirstOrDefaultAsync(r => r.Id == id && r.BusinessId == businessId.Value, ct);
        if (rule == null) return NotFound(ApiResponse<string>.Fail("NOT_FOUND", "Rule not found."));

        rule.Enabled = false;
        rule.Name = $"[archived] {rule.Name}";
        rule.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        return Ok(ApiResponse<string>.Ok("Rule archived."));
    }

    /// <summary>Paged run history for one rule.</summary>
    [HttpGet("{id:guid}/runs")]
    [ProducesResponseType(typeof(ApiResponse<PaginatedAutomationRuleRuns>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRuleRuns(
        Guid id,
        [FromQuery] AutomationRuleRunQuery query,
        CancellationToken ct = default)
    {
        var businessId = BusinessId;
        if (!businessId.HasValue)
            return BadRequest(ApiResponse<PaginatedAutomationRuleRuns>.Fail("NO_TENANT", "No business tenant context."));

        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        if (!await IsMemberOfBusinessAsync(userId.Value, businessId.Value, ct))
            return Forbid();

        var ruleExists = await _context.AutomationRules
            .AsNoTracking()
            .AnyAsync(r => r.Id == id && r.BusinessId == businessId.Value, ct);
        if (!ruleExists) return NotFound(ApiResponse<PaginatedAutomationRuleRuns>.Fail("NOT_FOUND", "Rule not found."));

        var page = Math.Clamp(query.Page, 1, 1000);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var dbQuery = _context.AutomationRuleRuns
            .AsNoTracking()
            .Where(r => r.RuleId == id);

        if (!string.IsNullOrWhiteSpace(query.Status))
            dbQuery = dbQuery.Where(r => r.Status == query.Status);

        var totalCount = await dbQuery.CountAsync(ct);

        var runs = await dbQuery
            .OrderByDescending(r => r.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => AutomationRuleRunDto.FromEntity(r))
            .ToListAsync(ct);

        return Ok(ApiResponse<PaginatedAutomationRuleRuns>.Ok(new PaginatedAutomationRuleRuns
        {
            Items = runs,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        }));
    }

    /// <summary>
    /// Rate-limited preview-send: renders a notification for one recipient without
    /// creating a recurring rule. Explicitly NOT a bulk-send mechanism — one
    /// recipient per call, rate-limited by the "general" policy.
    /// </summary>
    [HttpPost("preview-send")]
    [ProducesResponseType(typeof(ApiResponse<PreviewSendResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> PreviewSend(
        [FromBody] PreviewSendRequest request,
        CancellationToken ct = default)
    {
        var businessId = BusinessId;
        if (!businessId.HasValue)
            return BadRequest(ApiResponse<PreviewSendResponse>.Fail("NO_TENANT", "No business tenant context."));

        var userId = GetUserId();
        if (userId == null) return Unauthorized();
        if (!await IsMemberOfBusinessAsync(userId.Value, businessId.Value, ct))
            return Forbid();

        if (!ModelState.IsValid)
            return BadRequest(ApiResponse<PreviewSendResponse>.Fail("VALIDATION_ERROR", "Invalid preview request."));

        if (!await IsMemberOfBusinessAsync(request.RecipientUserId, businessId.Value, ct))
            return BadRequest(ApiResponse<PreviewSendResponse>.Fail("RECIPIENT_NOT_ALLOWED", "Recipient is not associated with this business."));

        var result = await _notifications.QueueAsync(new NotificationRequest(
            request.Type,
            request.RecipientUserId,
            businessId.Value,
            request.Data,
            $"preview:{businessId.Value}:{request.RecipientUserId}:{Guid.NewGuid():N}"),
            ct);

        return Ok(ApiResponse<PreviewSendResponse>.Ok(new PreviewSendResponse
        {
            Accepted = result.Accepted,
            SuppressReason = result.SuppressReason,
            Channels = result.Channels.ToList(),
            InboxId = result.InboxId
        }));
    }

    private async Task<bool> IsMemberOfBusinessAsync(Guid userId, Guid businessId, CancellationToken ct)
    {
        // One indexed membership lookup decides every role (Owner/Staff/Customer).
        return await _context.BusinessMemberships
            .AsNoTracking()
            .AnyAsync(m => m.UserId == userId &&
                           m.BusinessId == businessId &&
                           m.Status == BusinessMembershipStatus.Active, ct);
    }

    /// <summary>
    /// Validates the JSON rule definition against the Phase 3 security schema.
    /// Returns null when valid, or an error message describing the first problem.
    /// Conditions: each item must have field (allowlist), op (eq/ne/in/gt/lt), value.
    /// Actions: each item must have type from the action allowlist.
    /// Recipients: each item must have category from the recipient allowlist.
    /// </summary>
    private static string? ValidateRuleDefinition(string? conditionsJson, string? actionsJson, string? recipientsJson)
    {
        var allowedConditionFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "appointment.status", "appointment.scheduled_at", "appointment.payment_status",
            "customer.segment", "loyalty.balance", "payment.amount", "channel"
        };
        var allowedConditionOps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "eq", "ne", "in", "gt", "lt" };

        var allowedActionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "send_notification", "schedule_notification", "cancel_scheduled", "award_stamps" };

        var allowedRecipientCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "customer", "staff.assigned", "business.owner", "business.all_staff", "segment" };

        // ── Conditions ──────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(conditionsJson) && conditionsJson.Trim() != "[]")
        {
            JsonElement conditions;
            try { conditions = JsonDocument.Parse(conditionsJson).RootElement; }
            catch (JsonException) { return "ConditionsJson is not valid JSON."; }

            if (conditions.ValueKind != JsonValueKind.Array)
                return "ConditionsJson must be a JSON array.";

            foreach (var cond in conditions.EnumerateArray())
            {
                if (cond.ValueKind != JsonValueKind.Object)
                    return "Each condition must be a JSON object.";

                if (!cond.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.String)
                    return "Condition missing required string property 'field'.";
                if (!allowedConditionFields.Contains(field.GetString()!))
                    return $"Condition field '{field.GetString()}' is not allowed.";

                if (!cond.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String)
                    return "Condition missing required string property 'op'.";
                if (!allowedConditionOps.Contains(op.GetString()!))
                    return $"Condition op '{op.GetString()}' is not allowed.";

                if (!cond.TryGetProperty("value", out var _))
                    return "Condition missing required property 'value'.";
            }
        }

        // ── Actions ─────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(actionsJson) && actionsJson.Trim() != "[]")
        {
            JsonElement actions;
            try { actions = JsonDocument.Parse(actionsJson).RootElement; }
            catch (JsonException) { return "ActionsJson is not valid JSON."; }

            if (actions.ValueKind != JsonValueKind.Array)
                return "ActionsJson must be a JSON array.";

            foreach (var action in actions.EnumerateArray())
            {
                if (action.ValueKind != JsonValueKind.Object)
                    return "Each action must be a JSON object.";

                if (!action.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                    return "Action missing required string property 'type'.";
                if (!allowedActionTypes.Contains(type.GetString()!))
                    return $"Action type '{type.GetString()}' is not allowed.";
            }
        }

        // ── Recipients ──────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(recipientsJson) && recipientsJson.Trim() != "[]")
        {
            JsonElement recipients;
            try { recipients = JsonDocument.Parse(recipientsJson).RootElement; }
            catch (JsonException) { return "RecipientsJson is not valid JSON."; }

            if (recipients.ValueKind != JsonValueKind.Array)
                return "RecipientsJson must be a JSON array.";

            foreach (var recipient in recipients.EnumerateArray())
            {
                if (recipient.ValueKind != JsonValueKind.Object)
                    return "Each recipient must be a JSON object.";

                if (!recipient.TryGetProperty("category", out var category) || category.ValueKind != JsonValueKind.String)
                    return "Recipient missing required string property 'category'.";
                var cat = category.GetString()!;
                if (!allowedRecipientCategories.Contains(cat) && !cat.StartsWith("segment:", StringComparison.OrdinalIgnoreCase))
                    return $"Recipient category '{cat}' is not allowed.";
            }
        }

        return null;
    }

    private static bool IsKnownTrigger(string trigger)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "appointment.created", "appointment.rescheduled", "appointment.cancelled",
            "appointment.completed", "appointment.no_show", "appointment.reminder",
            "appointment.follow_up", "payment.failed", "payment.reversed",
            "loyalty.stamp_received", "loyalty.reward_ready",
            "customer.lapsed", "subscription.expiring", "time.before_appointment",
            "time.after_appointment"
        };
        return known.Contains(trigger);
    }
}