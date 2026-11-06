using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// <c>service_categories</c> — business-scoped service groupings.
/// </summary>
public class ServiceCategoryConfiguration : IEntityTypeConfiguration<ServiceCategory>
{
    public void Configure(EntityTypeBuilder<ServiceCategory> builder)
    {
        builder.ToTable("service_categories");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(x => x.Name).IsRequired().HasMaxLength(80).HasColumnName("name");
        builder.Property(x => x.Description).HasMaxLength(300).HasColumnName("description");
        builder.Property(x => x.SortOrder).IsRequired().HasColumnName("sort_order");
        builder.Property(x => x.IsActive).IsRequired().HasColumnName("is_active");
        builder.Property(x => x.UpdatedAt).IsRequired().HasColumnName("updated_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasOne(x => x.Business)
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);

        // One label per business: prevents the same category being created twice.
        builder.HasIndex(x => new { x.BusinessId, x.Name })
            .IsUnique()
            .HasDatabaseName("ux_service_categories_business_name");

        // Catalogue grouping queries (active categories in display order).
        builder.HasIndex(x => new { x.BusinessId, x.IsActive, x.SortOrder })
            .HasDatabaseName("ix_service_categories_business_active_order");
    }
}

/// <summary>
/// <c>staff_service_categories</c> — the provider ↔ service-category
/// specialisation link. The primary source of provider eligibility: a provider
/// assigned to a category is eligible for every active service in it.
/// </summary>
public class StaffServiceCategoryConfiguration : IEntityTypeConfiguration<StaffServiceCategory>
{
    public void Configure(EntityTypeBuilder<StaffServiceCategory> builder)
    {
        builder.ToTable("staff_service_categories");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.StaffUserId).IsRequired().HasColumnName("staff_user_id");
        builder.Property(x => x.ServiceCategoryId).IsRequired().HasColumnName("service_category_id");
        builder.Property(x => x.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.StaffUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Removing a category clears its provider assignments rather than being
        // blocked: deletion is already gated in the service to categories with no
        // SERVICES, and orphan provider links must not become undeletable rows.
        builder.HasOne<ServiceCategory>()
            .WithMany()
            .HasForeignKey(x => x.ServiceCategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);

        // One provider/category pair per tenant (idempotent assignment).
        builder.HasIndex(x => new { x.BusinessId, x.StaffUserId, x.ServiceCategoryId })
            .IsUnique()
            .HasDatabaseName("ux_staff_service_categories_business_staff_category");

        // "which staff are in this category" / "which categories is this staff in".
        builder.HasIndex(x => new { x.BusinessId, x.ServiceCategoryId })
            .HasDatabaseName("ix_staff_service_categories_business_category");
        builder.HasIndex(x => new { x.BusinessId, x.StaffUserId })
            .HasDatabaseName("ix_staff_service_categories_business_staff");
    }
}

/// <summary>
/// <c>staff_positions</c> — business-managed job titles.
/// </summary>
public class StaffPositionConfiguration : IEntityTypeConfiguration<StaffPosition>
{
    public void Configure(EntityTypeBuilder<StaffPosition> builder)
    {
        builder.ToTable("staff_positions");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(x => x.Name).IsRequired().HasMaxLength(80).HasColumnName("name");
        builder.Property(x => x.Description).HasMaxLength(300).HasColumnName("description");
        builder.Property(x => x.SortOrder).IsRequired().HasColumnName("sort_order");
        builder.Property(x => x.IsActive).IsRequired().HasColumnName("is_active");
        builder.Property(x => x.UpdatedAt).IsRequired().HasColumnName("updated_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasOne(x => x.Business)
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.BusinessId, x.Name })
            .IsUnique()
            .HasDatabaseName("ux_staff_positions_business_name");

        builder.HasIndex(x => new { x.BusinessId, x.IsActive, x.SortOrder })
            .HasDatabaseName("ix_staff_positions_business_active_order");
    }
}

/// <summary>
/// <c>staff_position_history</c> — append-only record of every title change.
/// </summary>
public class StaffPositionHistoryConfiguration : IEntityTypeConfiguration<StaffPositionHistory>
{
    public void Configure(EntityTypeBuilder<StaffPositionHistory> builder)
    {
        builder.ToTable("staff_position_history");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(x => x.StaffUserId).IsRequired().HasColumnName("staff_user_id");
        builder.Property(x => x.PositionId).HasColumnName("position_id");
        builder.Property(x => x.PositionName).HasMaxLength(80).HasColumnName("position_name");
        builder.Property(x => x.ChangedByUserId).HasColumnName("changed_by_user_id");
        builder.Property(x => x.ChangedAt).IsRequired().HasColumnName("changed_at");
        builder.Property(x => x.Note).HasMaxLength(300).HasColumnName("note");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.StaffUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Deleting a position must never delete the history that explains it:
        // the snapshot label survives the FK being cleared.
        builder.HasOne<StaffPosition>()
            .WithMany()
            .HasForeignKey(x => x.PositionId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.ChangedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(x => new { x.BusinessId, x.StaffUserId, x.ChangedAt })
            .HasDatabaseName("ix_staff_position_history_business_staff_changed");
    }
}
