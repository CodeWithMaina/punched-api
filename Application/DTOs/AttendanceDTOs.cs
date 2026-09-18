using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

/// <summary>
/// Attendance location + policy DTOs (plan §13.3–§13.4). Clock-in/out request
/// and status/history DTOs are added by Phase 3.
/// The raw QR token appears in exactly ONE DTO
/// (<see cref="AttendanceQrCredentialResponse"/>) and only as the return value
/// of mint/rotate — it is never retrievable again (§8.6).
/// </summary>
public sealed class AttendanceLocationSummaryResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    /// <summary>Is there a live QR for this location? The token itself is never returned (§13.3).</summary>
    [JsonPropertyName("hasActiveCredential")]
    public bool HasActiveCredential { get; set; }

    /// <summary>Last accepted scan of this location's credentials — operator visibility only.</summary>
    [JsonPropertyName("lastUsedAt")]
    public DateTime? LastUsedAt { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

public sealed class AttendanceLocationDetailResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    [JsonPropertyName("hasActiveCredential")]
    public bool HasActiveCredential { get; set; }

    [JsonPropertyName("lastUsedAt")]
    public DateTime? LastUsedAt { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

public sealed class CreateAttendanceLocationRequest
{
    [JsonPropertyName("name")]
    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [MaxLength(300)]
    public string? Description { get; set; }
}

public sealed class UpdateAttendanceLocationRequest
{
    [JsonPropertyName("name")]
    [MaxLength(120)]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    [MaxLength(300)]
    public string? Description { get; set; }

    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }
}

/// <summary>
/// Result of a QR mint/rotate. <see cref="Token"/> is the raw printable payload
/// (<c>punched:attendance:v1:&lt;43-char token&gt;</c>) and is returned ONCE —
/// only its SHA-256 hex is stored (plan §8.3).
/// </summary>
public sealed class AttendanceQrCredentialResponse
{
    [JsonPropertyName("credentialId")]
    public Guid CredentialId { get; set; }

    [JsonPropertyName("locationId")]
    public Guid LocationId { get; set; }

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>SCREAMING_SNAKE credential lifecycle value (<c>ACTIVE</c>).</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Effective attendance policy. Values are wire-shaped SCREAMING_SNAKE strings
/// so the API contract does not shift if C# member names are refactored (§6.6).
/// </summary>
public sealed class AttendancePolicyResponse
{
    /// <summary><c>STANDARD</c> in V1 (the enum stays open for future modes).</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("requiredVerifications")]
    public string[] RequiredVerifications { get; set; } = Array.Empty<string>();

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    /// <summary>
    /// V1 default (16 h). The stale-open-session rule of plan §9.2 is capped by
    /// this; the persisted column arrives with the Phase 4 settings write.
    /// </summary>
    [JsonPropertyName("maxOpenSessionHours")]
    public int MaxOpenSessionHours { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }
}