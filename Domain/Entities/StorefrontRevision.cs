using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// The revision event a <see cref="StorefrontRevision"/> represents.
///
/// <para>Stored as a string so the value is readable in the database and stable
/// across schema changes (a new member is additive, never a re-numbering).</para>
/// </summary>
public enum StorefrontRevisionType
{
    /// <summary>A meaningful draft save.</summary>
    DraftSave = 0,

    /// <summary>The exact configuration that was pushed live.</summary>
    Publish = 1,

    /// <summary>A previous revision restored into the working draft.</summary>
    Rollback = 2,

    /// <summary>The draft replaced with the published configuration.</summary>
    Reset = 3,
}

/// <summary>
/// An immutable, append-only snapshot of a business's storefront configuration.
///
/// <para>Why this exists: <see cref="BusinessLandingPageConfig"/> holds only the
/// <i>current</i> draft and published payloads, so every edit overwrote the
/// previous state. There was no way to see what the storefront looked like last
/// week, no way to undo a publish, and no way to reason about "who changed the
/// hero headline". Each revision here is a complete configuration snapshot that
/// is never updated or deleted by ordinary builder operations.</para>
///
/// <para>Guarantees:</para>
/// <list type="bullet">
/// <item><b>Append-only.</b> Rows are created by draft save, publish, rollback and
/// reset and are never rewritten. <c>(business_id, revision_number)</c> is unique,
/// which is what makes concurrent writers converge on a single winner instead of
/// forking the sequence.</item>
/// <item><b>Independent.</b> A revision carries its own configuration text; a later
/// draft edit cannot mutate it.</item>
/// <item><b>Presentation only.</b> It holds the same payload the builder already
/// persists — never preview tokens, secrets, owner ids beyond the acting user, or
/// transient editor state.</item>
/// </list>
/// </summary>
public class StorefrontRevision : BaseEntity
{
    /// <summary>Owning business. The tenant boundary for every revision read.</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>Monotonic, per-business revision number starting at 1.</summary>
    [Range(1, int.MaxValue)]
    public int RevisionNumber { get; set; }

    /// <summary>
    /// The complete storefront configuration (<c>StorefrontConfigDto</c>) as of
    /// this revision. Never a fragment.
    /// </summary>
    [Required]
    public string ConfigurationJson { get; set; } = string.Empty;

    /// <summary>What produced this revision.</summary>
    [Required]
    public StorefrontRevisionType RevisionType { get; set; }

    /// <summary>Optional human-readable note (never contains sensitive data).</summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>The user responsible for the change; null for system writes.</summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>
    /// When this revision was produced through a rollback, the revision it was
    /// restored from. Null otherwise.
    /// </summary>
    public Guid? SourceRevisionId { get; set; }

    /// <summary>True when this revision is a configuration that was pushed live.</summary>
    public bool IsPublishedSnapshot { get; set; }

    // ── Navigation ──────────────────────────────────────────
    /// <summary>The business that owns the revision.</summary>
    public virtual Business Business { get; set; } = null!;

    /// <summary>The user who produced the revision.</summary>
    public virtual User? CreatedByUser { get; set; }

    /// <summary>The revision this one restored, when created by a rollback.</summary>
    public virtual StorefrontRevision? SourceRevision { get; set; }
}
