using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  GUEST BOOKING
//
//  A visitor books without an account. They prove control of an inbox
//  with a one-time code, and the platform then resolves or creates the
//  customer record behind that identity and hands back a short-lived
//  customer token — which is the *same* token a signed-in customer uses,
//  so the booking itself goes through the existing appointment rules
//  rather than a parallel path.
//
//  No internal identifier is ever returned: the visitor gets their own
//  name and number back, never a user id.
// ═══════════════════════════════════════════════════════════════

/// <summary>Step one: who the visitor is, and where to send the code.</summary>
public sealed class StartGuestBookingRequest
{
    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    /// <summary>Kenyan mobile number in any of the shapes people type.</summary>
    [JsonPropertyName("phoneNumber")]
    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>The inbox the one-time code is sent to.</summary>
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;
}

/// <summary>What the visitor is told after the code has been sent.</summary>
public sealed class StartGuestBookingResponse
{
    /// <summary>Masked (`p•••@example.com`) — the full address is never echoed back.</summary>
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;

    /// <summary>The normalised number the code is tied to.</summary>
    [JsonPropertyName("phoneNumber")] public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>When the code stops being accepted.</summary>
    [JsonPropertyName("expiresAt")] public DateTime ExpiresAt { get; set; }

    /// <summary>Earliest moment a resend is accepted (a cooldown, not an error).</summary>
    [JsonPropertyName("resendAvailableAt")] public DateTime ResendAvailableAt { get; set; }

    /// <summary>How many digits the code has, so the form can size its inputs.</summary>
    [JsonPropertyName("codeLength")] public int CodeLength { get; set; } = 6;

    /// <summary>`email` — the only channel the platform verifies by today.</summary>
    [JsonPropertyName("deliveryChannel")] public string DeliveryChannel { get; set; } = "email";
}

/// <summary>Step two: the code the visitor received.</summary>
public sealed class VerifyGuestBookingRequest
{
    [JsonPropertyName("phoneNumber")] public string PhoneNumber { get; set; } = string.Empty;

    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
}

/// <summary>
/// A verified guest session.
///
/// <para>The token is the platform's standard short-lived customer access token,
/// scoped to the business the visitor booked at. It is the only credential the
/// guest receives: no refresh token is issued, so the session cannot be silently
/// extended, and it expires with the booking window it exists to cover.</para>
/// </summary>
public sealed class VerifyGuestBookingResponse
{
    [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = string.Empty;

    /// <summary>Token lifetime in seconds.</summary>
    [JsonPropertyName("expiresIn")] public int ExpiresIn { get; set; }

    [JsonPropertyName("tokenType")] public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }

    [JsonPropertyName("customer")] public GuestCustomerResponse Customer { get; set; } = new();
}

/// <summary>The visitor's own details, as they supplied them. No identifiers.</summary>
public sealed class GuestCustomerResponse
{
    [JsonPropertyName("fullName")] public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("phoneNumber")] public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>Masked.</summary>
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
}
