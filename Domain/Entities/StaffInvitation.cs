using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Lifecycle state of a staff invitation.
/// Invitations start Pending and move to Accepted or Revoked.
/// An invitation is considered Expired once its ExpiresAt passes while still Pending.
/// </summary>
public enum InvitationStatus
{
    /// <summary>Created and awaiting the invitee.</summary>
    Pending = 0,

    /// <summary>Accepted by the invitee (a staff account was created/linked).</summary>
    Accepted = 1,

    /// <summary>Explicitly revoked by an authorized business user.</summary>
    Revoked = 2,
}

/// <summary>
/// A staff invitation issued by a business owner/authorized manager.
/// Enables invitation-only staff onboarding — there is no public staff registration flow.
/// Only a hashed representation of the secret token is stored.
/// </summary>
public class StaffInvitation : BaseEntity
{
    /// <summary>
    /// FK to the Business that issued this invitation.
    /// All authorization resolves the business from the authenticated user, never from the client.
    /// </summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>
    /// Lowercased email of the invited staff member. This is the primary onboarding identifier,
    /// and acceptance is restricted to this address.
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string InvitedEmail { get; set; } = string.Empty;

    /// <summary>
    /// FK to the User who created the invitation (the inviter).
    /// </summary>
    [Required]
    public Guid InvitingUserId { get; set; }

    /// <summary>
    /// SHA-256 hash of the secure random invitation token. The plaintext token is only ever
    /// present in the invitation link sent by email and never stored.
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// Current lifecycle status.
    ///
    /// <para><c>[ConcurrencyCheck]</c> makes EF append <c>WHERE status = @original</c>
    /// to the UPDATE, so two simultaneous acceptance requests can never both
    /// win: the loser gets a <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
    /// and is answered with <c>ALREADY_USED</c>. That is the single-use
    /// guarantee — it is enforced by the database, not by a read-then-write
    /// window in application code.</para>
    /// </summary>
    [Required]
    [ConcurrencyCheck]
    public InvitationStatus Status { get; set; } = InvitationStatus.Pending;

    /// <summary>
    /// UTC time after which this invitation can no longer be accepted.
    /// </summary>
    [Required]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// UTC time the invitation was accepted, if any.
    /// </summary>
    public DateTime? AcceptedAt { get; set; }

    /// <summary>
    /// UTC time the invitation was revoked, if any.
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// How many times the invitation email has been re-sent.
    /// </summary>
    public int ResendCount { get; set; }

    /// <summary>
    /// Prefilled staff name captured at invitation time. Null for invitations created
    /// before prefilled onboarding existed; acceptance then falls back to the name the
    /// invitee submits.
    /// </summary>
    [MaxLength(150)]
    public string? InvitedFullName { get; set; }

    /// <summary>
    /// Prefilled phone number. Applied to the account only when it is new, so an
    /// existing identity's phone is never overwritten by an invitation.
    /// </summary>
    [MaxLength(30)]
    public string? InvitedPhoneNumber { get; set; }

    /// <summary>
    /// Optional job title the owner assigned. Validated against this business at
    /// invitation time AND re-validated on acceptance (a position can be deleted while
    /// the invitation is pending). Purely descriptive — it grants no permission.
    /// </summary>
    public Guid? PositionId { get; set; }

    /// <summary>
    /// JSON array of service-category ids the owner assigned. Stored as a JSON string
    /// because these are invitation intent, not live assignments: the real
    /// <c>staff_service_categories</c> rows are written only when the invitation is
    /// accepted, and are re-validated against the business at that moment.
    /// </summary>
    [MaxLength(2000)]
    public string? ServiceCategoryIdsJson { get; set; }

    // ── Navigation ──────────────────────────────────────────
    /// <summary>
    /// The business this invitation grants access to.
    /// </summary>
    public virtual Business? Business { get; set; }

    /// <summary>
    /// The user who created the invitation (the inviter / business owner).
    /// Enforces referential integrity: an invitation can only be created
    /// by an existing user in the system.
    /// </summary>
    public virtual User? InvitingUser { get; set; }
}