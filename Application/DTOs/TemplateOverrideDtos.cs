namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════════
//  Notification template override DTOs (Phase 2)
//  Mirror the backend.md contract: tenant-scoped, paginated, versioned.
// ═══════════════════════════════════════════════════════════════════

using System.Text.Json.Serialization;

/// <summary>Summary of one override (list view).</summary>
public class NotificationTemplateOverrideDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("subjectTemplate")]
    public string? SubjectTemplate { get; set; }

    [JsonPropertyName("bodyTemplateHtml")]
    public string BodyTemplateHtml { get; set; } = string.Empty;

    [JsonPropertyName("bodyTemplateText")]
    public string BodyTemplateText { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("updatedBy")]
    public string UpdatedBy { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = string.Empty;

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }
}

/// <summary>Create or update request for a single override.</summary>
public class UpsertNotificationTemplateOverrideRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("subjectTemplate")]
    public string? SubjectTemplate { get; set; }

    [JsonPropertyName("bodyTemplateHtml")]
    public string BodyTemplateHtml { get; set; } = string.Empty;

    [JsonPropertyName("bodyTemplateText")]
    public string BodyTemplateText { get; set; } = string.Empty;

    [JsonPropertyName("updatedBy")]
    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>Effective template response (override or platform default).</summary>
public class EffectiveTemplateDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty; // "override" | "default"

    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("bodyHtml")]
    public string BodyHtml { get; set; } = string.Empty;

    [JsonPropertyName("overrideId")]
    public Guid? OverrideId { get; set; }

    [JsonPropertyName("overrideVersion")]
    public int? OverrideVersion { get; set; }
}

/// <summary>Preview request: render a template with a sample payload.</summary>
public class PreviewTemplateRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("subjectTemplate")]
    public string? SubjectTemplate { get; set; }

    [JsonPropertyName("bodyTemplateHtml")]
    public string BodyTemplateHtml { get; set; } = string.Empty;

    [JsonPropertyName("bodyTemplateText")]
    public string BodyTemplateText { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public Dictionary<string, object?> Payload { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Preview response: rendered output plus any unknown tokens flagged.</summary>
public class PreviewTemplateResponse
{
    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("bodyHtml")]
    public string BodyHtml { get; set; } = string.Empty;

    [JsonPropertyName("unknownTokens")]
    public List<string> UnknownTokens { get; set; } = new();

    [JsonPropertyName("hasUnknownTokens")]
    public bool HasUnknownTokens => UnknownTokens.Count > 0;
}

/// <summary>One version in the history list.</summary>
public class TemplateOverrideVersionDto
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("subjectTemplate")]
    public string? SubjectTemplate { get; set; }

    [JsonPropertyName("bodyTemplateHtml")]
    public string BodyTemplateHtml { get; set; } = string.Empty;

    [JsonPropertyName("bodyTemplateText")]
    public string BodyTemplateText { get; set; } = string.Empty;

    [JsonPropertyName("updatedBy")]
    public string UpdatedBy { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>Paginated list of overrides.</summary>
public class PaginatedNotificationTemplateOverridesDto
{
    [JsonPropertyName("items")]
    public List<NotificationTemplateOverrideDto> Items { get; set; } = new();

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }
}
