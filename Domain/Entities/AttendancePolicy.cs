namespace PunchedApi.Domain.Entities;

/// <summary>
/// Per-business attendance configuration. This is NOT a second module
/// on/off switch — module entitlement remains the on/off authority.
/// A missing row means the implicit Standard default
/// (<c>Standard</c> + <c>["AUTHENTICATED_USER","QR"]</c>); the row is
/// created lazily on the first settings write (Phase 4).
/// </summary>
public class AttendancePolicy : BaseEntity
{
    /// <summary>
    /// FK to the business this policy belongs to. Unique — one policy per
    /// business in V1.
    /// </summary>
    public Guid BusinessId { get; set; }

    /// <summary>
    /// Attendance operating mode (V1: Standard).
    /// </summary>
    public AttendanceMode Mode { get; set; } = AttendanceMode.Standard;

    /// <summary>
    /// JSON string array of required verification method keys, e.g.
    /// <c>["AUTHENTICATED_USER","QR"]</c>. Stored as a plain string parsed
    /// with <c>System.Text.Json</c>, exactly like
    /// <c>Module.DependenciesJson</c> (no jsonb dependency).
    /// </summary>
    public string RequiredVerificationsJson { get; set; } = "[\"AUTHENTICATED_USER\",\"QR\"]";

    /// <summary>
    /// Pause clock-ins without deleting credentials or disabling the module.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// UTC timestamp of the last settings write. Null until first written.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}
