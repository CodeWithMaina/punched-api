using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

public sealed class CreateReviewRequest
{
    [JsonPropertyName("appointmentId")]
    public Guid AppointmentId { get; set; }

    [JsonPropertyName("rating")]
    public int Rating { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

public sealed class UpdateReviewRequest
{
    [JsonPropertyName("rating")]
    public int Rating { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

public sealed class ReviewResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }
    [JsonPropertyName("appointmentId")]
    public Guid AppointmentId { get; set; }
    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }
    [JsonPropertyName("rating")]
    public int Rating { get; set; }
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Public contract. Customer and appointment identifiers are intentionally excluded.</summary>
public sealed class PublicReviewResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("rating")] public int Rating { get; set; }
    [JsonPropertyName("comment")] public string? Comment { get; set; }
    [JsonPropertyName("reviewerDisplayName")] public string? ReviewerDisplayName { get; set; }
    [JsonPropertyName("reviewerAvatar")] public string? ReviewerAvatar { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }
}

public sealed class BusinessReviewResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("rating")] public int Rating { get; set; }
    [JsonPropertyName("comment")] public string? Comment { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("reviewerDisplayName")] public string? ReviewerDisplayName { get; set; }
    [JsonPropertyName("reviewerAvatar")] public string? ReviewerAvatar { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }
}


public sealed class ReviewEligibilityResponse
{
    [JsonPropertyName("appointmentId")]
    public Guid AppointmentId { get; set; }
    [JsonPropertyName("eligible")]
    public bool Eligible { get; set; }
    [JsonPropertyName("alreadyReviewed")]
    public bool AlreadyReviewed { get; set; }
    [JsonPropertyName("submissionDeadline")]
    public DateTime? SubmissionDeadline { get; set; }
    [JsonPropertyName("editDeadline")]
    public DateTime? EditDeadline { get; set; }
    [JsonPropertyName("review")]
    public ReviewResponse? Review { get; set; }
}

public sealed class ReviewSummaryResponse
{
    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }
    [JsonPropertyName("averageRating")]
    public decimal AverageRating { get; set; }
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
    [JsonPropertyName("ratingCounts")]
    public Dictionary<int, int> RatingCounts { get; set; } = new();
}
