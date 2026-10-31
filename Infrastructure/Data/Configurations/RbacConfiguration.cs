using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// <c>permissions</c> — the system permission catalogue (code-owned, mirrored
/// into the database so role assignments can reference it and admins can read
/// it without a deploy).
/// </summary>
public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("permissions");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Code).IsRequired().HasMaxLength(80).HasColumnName("code");
        builder.Property(x => x.Name).IsRequired().HasMaxLength(120).HasColumnName("name");
        builder.Property(x => x.Description).HasMaxLength(300).HasColumnName("description");
        builder.Property(x => x.ModuleKey).IsRequired().HasMaxLength(40).HasColumnName("module_key");
        builder.Property(x => x.IsSystem).IsRequired().HasColumnName("is_system");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_permissions_code");
        builder.HasIndex(x => x.ModuleKey).HasDatabaseName("ix_permissions_module");
    }
}

/// <summary>
/// <c>business_roles</c> — business-scoped permission bundles.
/// </summary>
public class BusinessRoleConfiguration : IEntityTypeConfiguration<BusinessRole>
{
    public void Configure(EntityTypeBuilder<BusinessRole> builder)
    {
        builder.ToTable("business_roles");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(x => x.Key).IsRequired().HasMaxLength(40).HasColumnName("key");
        builder.Property(x => x.Name).IsRequired().HasMaxLength(80).HasColumnName("name");
        builder.Property(x => x.Description).HasMaxLength(300).HasColumnName("description");
        builder.Property(x => x.IsOwnerRole).IsRequired().HasColumnName("is_owner_role");
        builder.Property(x => x.IsSystem).IsRequired().HasColumnName("is_system");
        builder.Property(x => x.IsActive).IsRequired().HasColumnName("is_active");
        builder.Property(x => x.SortOrder).IsRequired().HasColumnName("sort_order");
        builder.Property(x => x.UpdatedAt).IsRequired().HasColumnName("updated_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasOne(x => x.Business)
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.BusinessId, x.Key })
            .IsUnique()
            .HasDatabaseName("ux_business_roles_business_key");

        builder.HasIndex(x => new { x.BusinessId, x.Name })
            .IsUnique()
            .HasDatabaseName("ux_business_roles_business_name");

        // Exactly one owner role per business, enforced by the database.
        builder.HasIndex(x => x.BusinessId)
            .IsUnique()
            .HasFilter("is_owner_role = true")
            .HasDatabaseName("ux_business_roles_single_owner");
    }
}

/// <summary>
/// <c>business_role_permissions</c> — one permission granted to one role.
/// </summary>
public class BusinessRolePermissionConfiguration : IEntityTypeConfiguration<BusinessRolePermission>
{
    public void Configure(EntityTypeBuilder<BusinessRolePermission> builder)
    {
        builder.ToTable("business_role_permissions");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessRoleId).IsRequired().HasColumnName("business_role_id");
        builder.Property(x => x.PermissionCode).IsRequired().HasMaxLength(80).HasColumnName("permission_code");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasOne(x => x.Role)
            .WithMany(r => r.Permissions)
            .HasForeignKey(x => x.BusinessRoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.BusinessRoleId, x.PermissionCode })
            .IsUnique()
            .HasDatabaseName("ux_business_role_permissions_role_code");

        builder.HasIndex(x => x.PermissionCode)
            .HasDatabaseName("ix_business_role_permissions_code");
    }
}

/// <summary>
/// <c>business_membership_role_assignments</c> — which roles a membership holds.
/// </summary>
public class BusinessMembershipRoleAssignmentConfiguration
    : IEntityTypeConfiguration<BusinessMembershipRoleAssignment>
{
    public void Configure(EntityTypeBuilder<BusinessMembershipRoleAssignment> builder)
    {
        builder.ToTable("business_membership_role_assignments");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessMembershipId).IsRequired().HasColumnName("business_membership_id");
        builder.Property(x => x.BusinessRoleId).IsRequired().HasColumnName("business_role_id");
        builder.Property(x => x.AssignedByUserId).HasColumnName("assigned_by_user_id");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasOne(x => x.Membership)
            .WithMany()
            .HasForeignKey(x => x.BusinessMembershipId)
            .OnDelete(DeleteBehavior.Cascade);

        // A role that is still assigned to somebody cannot be deleted: the
        // business must unassign it first (deliberate, auditable).
        builder.HasOne(x => x.Role)
            .WithMany()
            .HasForeignKey(x => x.BusinessRoleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.AssignedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(x => new { x.BusinessMembershipId, x.BusinessRoleId })
            .IsUnique()
            .HasDatabaseName("ux_membership_role_assignments_membership_role");

        builder.HasIndex(x => x.BusinessRoleId)
            .HasDatabaseName("ix_membership_role_assignments_role");
    }
}
