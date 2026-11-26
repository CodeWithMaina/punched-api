using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

/// <summary>One row of the staff lifecycle trail. Read-only by construction.</summary>
public class StaffAuditEventResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("staffUserId")] public Guid? StaffUserId { get; set; }
    [JsonPropertyName("staffName")] public string? StaffName { get; set; }
    [JsonPropertyName("actorUserId")] public Guid? ActorUserId { get; set; }
    [JsonPropertyName("actorName")] public string? ActorName { get; set; }
    [JsonPropertyName("eventType")] public string EventType { get; set; } = string.Empty;
    [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
    /// <summary>Structured change details as a JSON string (may be null).</summary>
    [JsonPropertyName("detailsJson")] public string? DetailsJson { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
}
