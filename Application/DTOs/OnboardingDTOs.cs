using System.Text.Json.Serialization;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  ONBOARDING REQUEST DTOs
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// POST /v1/businesses/me/staff/invitations — request body.
///
/// <para>The email is the onboarding identifier. The other fields are optional so
/// clients written before prefilled onboarding keep working; when supplied they are
/// validated and shown back to the invitee as a prefilled form.</para>
/// </summary>
public class CreateStaffInvitationRequest
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    /// <summary>Staff full name the invitee will see prefilled (max 150).</summary>
    [JsonPropertyName("fullName")]
    public string? FullName { get; set; }

    /// <summary>Contact phone (digits, spaces, + - and parentheses; 7–20 characters).</summary>
    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }

    /// <summary>Job title to assign on acceptance. Must belong to the inviting business.</summary>
    [JsonPropertyName("positionId")]
    public Guid? PositionId { get; set; }

    /// <summary>Service categories to assign on acceptance. Each must belong to this business.</summary>
    [JsonPropertyName("serviceCategoryIds")]
    public List<Guid>? ServiceCategoryIds { get; set; }
}

/// <summary>
/// Response describing a single staff invitation for the business management UI.
/// Never includes the plaintext token — only the lifecycle fields.
/// </summary>
public class StaffInvitationResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    /// <summary>Prefilled staff name (null for invitations created before prefill).</summary>
    [JsonPropertyName("fullName")]
    public string? FullName { get; set; }

    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("positionId")]
    public Guid? PositionId { get; set; }

    [JsonPropertyName("positionName")]
    public string? PositionName { get; set; }

    /// <summary>Categories assigned at invitation time, resolved to names for display.</summary>
    [JsonPropertyName("categories")]
    public List<ProviderCategoryResponse> Categories { get; set; } = [];

    [JsonPropertyName("invitedByUserId")]
    public Guid InvitedByUserId { get; set; }

    [JsonPropertyName("status")]
    public InvitationStatus Status { get; set; }

    [JsonPropertyName("statusLabel")]
    public string StatusLabel { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; set; }

    [JsonPropertyName("acceptedAt")]
    public DateTime? AcceptedAt { get; set; }

    [JsonPropertyName("revokedAt")]
    public DateTime? RevokedAt { get; set; }

    [JsonPropertyName("resendCount")]
    public int ResendCount { get; set; }

    [JsonPropertyName("isExpired")]
    public bool IsExpired { get; set; }
}

/// <summary>
/// GET /v1/invitations/{token} — safe public validation result shown on
/// the invitation acceptance page before the invitee submits any credentials.
/// </summary>
public class StaffInvitationValidationResponse
{
    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("businessName")]
    public string BusinessName { get; set; } = string.Empty;

    [JsonPropertyName("businessLogoUrl")]
    public string? BusinessLogoUrl { get; set; }

    /// <summary>
    /// The email this invitation was sent to (lowercased). Acceptance is locked to it.
    /// </summary>
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    /// <summary>Prefilled name, when the owner supplied one.</summary>
    [JsonPropertyName("fullName")]
    public string? FullName { get; set; }

    /// <summary>Prefilled phone, when the owner supplied one.</summary>
    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("positionName")]
    public string? PositionName { get; set; }

    /// <summary>Categories that will be assigned on acceptance (names only).</summary>
    [JsonPropertyName("categories")]
    public List<ProviderCategoryResponse> Categories { get; set; } = [];

    /// <summary>
    /// True when an account already exists for the invited email. The page then offers
    /// the sign-in path instead of account creation; the token still cannot attach an
    /// existing identity by itself.
    /// </summary>
    [JsonPropertyName("alreadyRegistered")]
    public bool AlreadyRegistered { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// POST /v1/invitations/{token}/accept — body supplied by the invitee.
/// Critical values (business, role, email) are derived server-side from the token, not the client.
/// </summary>
public class AcceptStaffInvitationRequest
{
    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    /// <summary>
    /// Optional phone number. When the invitation was prefilled, the owner's value wins;
    /// otherwise this value (when supplied) is stored on the new account.
    /// </summary>
    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The invitee confirms they are the person invited by typing/confirming the invited email.
    /// Must match the email stored on the invitation.
    /// </summary>
    [JsonPropertyName("emailConfirmation")]
    public string EmailConfirmation { get; set; } = string.Empty;
}