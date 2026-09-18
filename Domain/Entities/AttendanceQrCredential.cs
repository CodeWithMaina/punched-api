using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// The printed, business-owned, opaque credential. Identifies a location,
/// never a staff member. The raw token is never stored — only its SHA-256
/// hex hash (mirrors <c>qr_tokens.token_hash</c> and
/// <c>staff_invitations.token_hash</c>).
/// </summary>
public class AttendanceQrCredential : BaseEntity
{
    /// <summary>
    /// FK to the business this credential belongs to.
    /// </summary>
    public Guid BusinessId { get; set; }

    /// <summary>
    /// FK to the location this credential identifies.
    /// </summary>
    public Guid AttendanceLocationId { get; set; }

    /// <summary>
    /// SHA-256 hex of the raw token. The raw value lives only on the
    /// printed sheet / in the mint response (returned once).
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// Credential lifecycle (Active / Revoked).
    /// </summary>
    public AttendanceCredentialStatus Status { get; set; } = AttendanceCredentialStatus.Active;

    /// <summary>
    /// FK to the user who minted this credential.
    /// </summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>
    /// UTC timestamp of revocation. Null while active.
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// FK to the user who revoked this credential. Null while active.
    /// </summary>
    public Guid? RevokedByUserId { get; set; }

    /// <summary>
    /// UTC timestamp of the last accepted scan. Operator visibility only.
    /// </summary>
    public DateTime? LastUsedAt { get; set; }
}
