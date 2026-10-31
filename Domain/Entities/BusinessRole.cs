using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// A system-defined permission: one action a user may perform inside a
/// business. Rows mirror <c>PunchedApi.Application.Authorization.PermissionCatalog</c>
/// and are upserted idempotently at startup, so the catalogue is code-owned
/// while role assignments stay data-owned.
/// </summary>
public class Permission : BaseEntity
{
    [Required]
    [MaxLength(80)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Description { get; set; }

    /// <summary>Owning module key (mirrors <c>ModuleCatalog</c>).</summary>
    [Required]
    [MaxLength(40)]
    public string ModuleKey { get; set; } = string.Empty;

    /// <summary>True for platform-defined permissions (never business-editable).</summary>
    public bool IsSystem { get; set; } = true;
}

/// <summary>
/// A business-scoped role: a named bundle of permissions. Businesses start
/// with the platform's default templates (Owner, Manager, Receptionist,
/// Service Professional) and may add custom roles.
///
/// <para><b>Owner role.</b> Exactly one role per business has
/// <see cref="IsOwnerRole"/> = true. It implicitly holds <b>every</b>
/// permission and cannot be deleted, renamed, deactivated or stripped of
/// authority — the business owner always retains full authority over their own
/// business. It is never assignable to a non-owner membership.</para>
/// </summary>
public class BusinessRole : BaseEntity
{
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>Stable machine key (unique per business), e.g. <c>manager</c>.</summary>
    [Required]
    [MaxLength(40)]
    public string Key { get; set; } = string.Empty;

    [Required]
    [MaxLength(80)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Description { get; set; }

    /// <summary>True only for the single owner role of the business.</summary>
    public bool IsOwnerRole { get; set; }

    /// <summary>True for platform-seeded templates (still editable, but not deletable as a set).</summary>
    public bool IsSystem { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual Business Business { get; set; } = null!;

    public virtual ICollection<BusinessRolePermission> Permissions { get; set; } = new List<BusinessRolePermission>();
}

/// <summary>One permission granted to one business role.</summary>
public class BusinessRolePermission : BaseEntity
{
    [Required]
    public Guid BusinessRoleId { get; set; }

    [Required]
    [MaxLength(80)]
    public string PermissionCode { get; set; } = string.Empty;

    public virtual BusinessRole Role { get; set; } = null!;
}

/// <summary>
/// Assignment of a business role to a membership. A membership may hold
/// several roles; its effective permissions are the union of theirs. The
/// membership's <see cref="BusinessMembership.Role"/> enum remains the coarse
/// persona used by the tenant policies; this table is the fine-grained
/// authority.
/// </summary>
public class BusinessMembershipRoleAssignment : BaseEntity
{
    [Required]
    public Guid BusinessMembershipId { get; set; }

    [Required]
    public Guid BusinessRoleId { get; set; }

    public Guid? AssignedByUserId { get; set; }

    public virtual BusinessMembership Membership { get; set; } = null!;
    public virtual BusinessRole Role { get; set; } = null!;
}
