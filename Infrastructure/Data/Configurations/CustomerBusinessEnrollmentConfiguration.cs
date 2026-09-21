using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>Fluent configuration for CustomerBusinessEnrollment (customer_business_enrollments).</summary>
public class CustomerBusinessEnrollmentConfiguration : IEntityTypeConfiguration<CustomerBusinessEnrollment>
{
    public void Configure(EntityTypeBuilder<CustomerBusinessEnrollment> builder)
    {
        builder.ToTable("customer_business_enrollments");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.CustomerId).IsRequired().HasColumnName("customer_id");
        builder.Property(e => e.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(e => e.Status).IsRequired().HasColumnName("status").HasDefaultValue(CustomerBusinessEnrollmentStatus.Active);
        builder.Property(e => e.Source).IsRequired().HasMaxLength(20).HasColumnName("source").HasDefaultValue("discovery");
        builder.Property(e => e.EnrolledAt).IsRequired().HasColumnName("enrolled_at");
        builder.Property(e => e.LeftAt).HasColumnName("left_at");
        builder.Property(e => e.UpdatedAt).IsRequired().HasColumnName("updated_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(e => new { e.CustomerId, e.BusinessId }).IsUnique();
        builder.HasIndex(e => new { e.BusinessId, e.Status });
        builder.HasIndex(e => new { e.CustomerId, e.Status });

        builder.HasOne(e => e.Customer).WithMany().HasForeignKey(e => e.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Business).WithMany().HasForeignKey(e => e.BusinessId).OnDelete(DeleteBehavior.Restrict);
    }
}
