using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

public sealed class CreateMediaUploadRequest
{
    [JsonPropertyName("purpose")] public string Purpose { get; set; } = string.Empty;
    [JsonPropertyName("targetType")] public string? TargetType { get; set; }
    [JsonPropertyName("targetId")] public Guid? TargetId { get; set; }
    [JsonPropertyName("fileName")] public string FileName { get; set; } = string.Empty;
    [JsonPropertyName("declaredMimeType")] public string? DeclaredMimeType { get; set; }
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
}

public sealed class AssignMediaRequest
{
    [JsonPropertyName("mediaId")] public Guid MediaId { get; set; }
}

public sealed class AttachGalleryMediaRequest
{
    [JsonPropertyName("mediaId")] public Guid MediaId { get; set; }
    [JsonPropertyName("sortOrder")] public int? SortOrder { get; set; }
    [JsonPropertyName("featured")] public bool Featured { get; set; }
}

public sealed class ReorderGalleryMediaRequest
{
    [JsonPropertyName("mediaIds")] public List<Guid> MediaIds { get; set; } = [];
}

public sealed class AttachRelationshipMediaRequest
{
    [JsonPropertyName("mediaId")] public Guid MediaId { get; set; }
    [JsonPropertyName("sortOrder")] public int SortOrder { get; set; }
}

public sealed class MediaUploadGrantResponse
{
    [JsonPropertyName("mediaId")] public Guid MediaId { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("uploadUrl")] public string UploadUrl { get; set; } = string.Empty;
    [JsonPropertyName("requiredHeaders")] public Dictionary<string, string> RequiredHeaders { get; set; } = new();
    [JsonPropertyName("uploadGrantExpiresAt")] public DateTimeOffset UploadGrantExpiresAt { get; set; }
}

public sealed class MediaResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("purpose")] public string Purpose { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("visibility")] public string Visibility { get; set; } = string.Empty;
    [JsonPropertyName("declaredMimeType")] public string? DeclaredMimeType { get; set; }
    [JsonPropertyName("detectedMimeType")] public string? DetectedMimeType { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("sizeBytes")] public long? SourceSizeBytes { get; set; }
    [JsonPropertyName("variants")] public IReadOnlyList<MediaVariantResponse> Variants { get; set; } = [];
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }
}

public sealed class MediaVariantResponse
{
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("format")] public string Format { get; set; } = string.Empty;
    [JsonPropertyName("transform")] public string Transform { get; set; } = string.Empty;
}

public sealed class BusinessMediaResponse
{
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("mediaId")] public Guid MediaId { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; } = "Gallery";
    [JsonPropertyName("sortOrder")] public int SortOrder { get; set; }
    [JsonPropertyName("featured")] public bool Featured { get; set; }
    public MediaResponse? Media { get; set; }
}

public sealed class ServiceMediaResponse
{
    [JsonPropertyName("serviceId")] public Guid ServiceId { get; set; }
    [JsonPropertyName("mediaId")] public Guid MediaId { get; set; }
    [JsonPropertyName("sortOrder")] public int SortOrder { get; set; }
    public MediaResponse? Media { get; set; }
}

public sealed class LoyaltyProgramMediaResponse
{
    [JsonPropertyName("programId")] public Guid ProgramId { get; set; }
    [JsonPropertyName("mediaId")] public Guid MediaId { get; set; }
    [JsonPropertyName("sortOrder")] public int SortOrder { get; set; }
    public MediaResponse? Media { get; set; }
}

public sealed class ReviewMediaResponse
{
    [JsonPropertyName("reviewId")] public Guid ReviewId { get; set; }
    [JsonPropertyName("mediaId")] public Guid MediaId { get; set; }
    [JsonPropertyName("sortOrder")] public int SortOrder { get; set; }
    public MediaResponse? Media { get; set; }
}
