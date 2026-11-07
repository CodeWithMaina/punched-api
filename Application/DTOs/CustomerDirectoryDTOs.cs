using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

/// <summary>
/// Owner-side "quick add" of a customer from inside a booking flow (the appointment
/// wizard's Step 1). Deliberately minimal: a name plus at least one way to reach the
/// person (phone or email). No password and no login are created — a walk-in customer
/// is an identity the business can book for, not an account.
/// </summary>
public class QuickAddCustomerRequest
{
    /// <summary>Customer's display name. Required.</summary>
    [JsonPropertyName("fullName")]
    public string? FullName { get; set; }

    /// <summary>Kenyan MSISDN (07XX / 01XX / 2547…). Optional if an email is given.</summary>
    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }

    /// <summary>Optional email. When omitted the server mints a non-deliverable placeholder.</summary>
    [JsonPropertyName("email")]
    public string? Email { get; set; }
}

/// <summary>
/// A compact customer reference used by the booking wizard's searchable selector and
/// returned by quick-add so the freshly-created customer can be selected immediately.
/// Only the identifying fields the selector renders — never loyalty balances.
/// </summary>
public class CustomerOptionResponse
{
    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; set; }

    /// <summary>Persisted enrollment status for this business: active | left | blocked.</summary>
    [JsonPropertyName("enrollmentStatus")]
    public string EnrollmentStatus { get; set; } = "active";
}
