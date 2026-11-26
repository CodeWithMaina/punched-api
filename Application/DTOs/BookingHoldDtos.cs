using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════════
//  Temporary slot holds
//
//  The claim a customer takes on a slot while they pay the booking
//  fee. It is deliberately separate from the appointment it may become:
//  a hold expires on its own and never appears as a booking.
// ═══════════════════════════════════════════════════════════════════

/// <summary>Asks for a slot to be held while the customer pays.</summary>
public sealed class CreateBookingHoldRequest
{
    [JsonPropertyName("serviceIds")]
    public Guid[] ServiceIds { get; set; } = Array.Empty<Guid>();

    [JsonPropertyName("staffUserId")]
    public Guid? StaffUserId { get; set; }

    /// <summary>The slot's UTC start, exactly as the availability grid returned it.</summary>
    [JsonPropertyName("scheduledAt")]
    public DateTime ScheduledAt { get; set; }

    /// <summary>
    /// The client's booking intent. Re-sending the same key resumes the same hold instead of
    /// stacking a second claim on the interval, which is what makes a page refresh safe.
    /// </summary>
    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }

    [JsonPropertyName("paymentOption")]
    public string? PaymentOption { get; set; }

    [JsonPropertyName("fullName")]
    public string? FullName { get; set; }

    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>
/// A hold, as the booking page needs it: the interval it claims, its deadline, and how long is
/// left. <see cref="SecondsRemaining"/> is computed by the server on every read, so the countdown
/// a customer sees is always the backend's own deadline rather than a browser guess.
/// </summary>
public sealed class BookingHoldResponse
{
    [JsonPropertyName("holdId")]
    public Guid HoldId { get; set; }

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("staffUserId")]
    public Guid StaffUserId { get; set; }

    [JsonPropertyName("serviceIds")]
    public Guid[] ServiceIds { get; set; } = Array.Empty<Guid>();

    [JsonPropertyName("scheduledAt")]
    public DateTime ScheduledAt { get; set; }

    [JsonPropertyName("endAt")]
    public DateTime EndAt { get; set; }

    [JsonPropertyName("durationMinutes")]
    public int DurationMinutes { get; set; }

    /// <summary>One of the appointment slot-hold statuses.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("expiresAtUtc")]
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Whole seconds left before the hold lapses; 0 once it has.</summary>
    [JsonPropertyName("secondsRemaining")]
    public int SecondsRemaining { get; set; }

    /// <summary>True while the hold still blocks the slot and can become a booking.</summary>
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}
