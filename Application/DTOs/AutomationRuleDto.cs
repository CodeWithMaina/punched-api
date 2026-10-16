using System.Text.Json.Serialization;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.DTOs;

/// <summary>Summary of a per-business automation rule for list/get responses.</summary>
public sealed class AutomationRuleDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Trigger { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int Version { get; set; }
    public int? DailyActionCap { get; set; }
    public int? FrequencyCapMinutes { get; set; }
    public TimeSpan? QuietHoursStart { get; set; }
    public TimeSpan? QuietHoursEnd { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string ConditionsJson { get; set; } = "[]";
    public string ActionsJson { get; set; } = "[]";
    public string RecipientsJson { get; set; } = "[]";

    public static AutomationRuleDto FromEntity(AutomationRule rule) => new()
    {
        Id = rule.Id,
        Name = rule.Name,
        Description = rule.Description,
        Trigger = rule.Trigger,
        Enabled = rule.Enabled,
        Version = rule.Version,
        DailyActionCap = rule.DailyActionCap,
        FrequencyCapMinutes = rule.FrequencyCapMinutes,
        QuietHoursStart = rule.QuietHoursStart,
        QuietHoursEnd = rule.QuietHoursEnd,
        CreatedAt = rule.CreatedAt,
        UpdatedAt = rule.UpdatedAt,
        ConditionsJson = rule.ConditionsJson,
        ActionsJson = rule.ActionsJson,
        RecipientsJson = rule.RecipientsJson
    };
}

/// <summary>Request to create or update an automation rule.</summary>
public sealed class UpsertAutomationRuleRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.MaxLength(1000)]
    public string? Description { get; set; }

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MaxLength(100)]
    public string Trigger { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required]
    public string ConditionsJson { get; set; } = "[]";

    [System.ComponentModel.DataAnnotations.Required]
    public string ActionsJson { get; set; } = "[]";

    [System.ComponentModel.DataAnnotations.Required]
    public string RecipientsJson { get; set; } = "[]";

    public bool Enabled { get; set; } = false;

    public int? DailyActionCap { get; set; }
    public int? FrequencyCapMinutes { get; set; }
    public TimeSpan? QuietHoursStart { get; set; }
    public TimeSpan? QuietHoursEnd { get; set; }
}

/// <summary>One automation rule execution for the run-history view.</summary>
public sealed class AutomationRuleRunDto
{
    public Guid Id { get; set; }
    public Guid RuleId { get; set; }
    public int RuleVersion { get; set; }
    public string SourceEventType { get; set; } = string.Empty;
    public Guid? SourceAggregateId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public string? ErrorDetailsJson { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<string> CreatedNotificationIntentIds { get; set; } = new();
    public List<Guid> CreatedJobIds { get; set; } = new();

    public static AutomationRuleRunDto FromEntity(AutomationRuleRun run)
    {
        var intentIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(run.CreatedNotificationIntentIdsJson ?? "[]") ?? new();
        var jobIds = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(run.CreatedJobIdsJson ?? "[]") ?? new();
        return new AutomationRuleRunDto
        {
            Id = run.Id,
            RuleId = run.RuleId,
            RuleVersion = run.RuleVersion,
            SourceEventType = run.SourceEventType,
            SourceAggregateId = run.SourceAggregateId,
            Status = run.Status,
            ErrorMessage = run.ErrorMessage,
            ErrorDetailsJson = run.ErrorDetailsJson,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            CreatedAt = run.CreatedAt,
            CreatedNotificationIntentIds = intentIds,
            CreatedJobIds = jobIds
        };
    }
}

/// <summary>Request for the guarded preview-send endpoint.</summary>
public sealed class PreviewSendRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MaxLength(100)]
    public string Type { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required]
    public Guid RecipientUserId { get; set; }

    public Dictionary<string, object?> Data { get; set; } = new();

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MaxLength(20)]
    public string Channel { get; set; } = NotificationChannel.InApp;
}

/// <summary>Response from preview-send.</summary>
public sealed class PreviewSendResponse
{
    public bool Accepted { get; set; }
    public string? SuppressReason { get; set; }
    public List<string> Channels { get; set; } = new();
    public Guid? InboxId { get; set; }
}

/// <summary>Paged query for rule runs.</summary>
public sealed class AutomationRuleRunQuery
{
    public string Range { get; set; } = "24h";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public string? Status { get; set; }
    public string? Trigger { get; set; }
    public string? Search { get; set; }
}

/// <summary>Paged response of rule runs.</summary>
public sealed class PaginatedAutomationRuleRuns
{
    public List<AutomationRuleRunDto> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}