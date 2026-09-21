using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for <see cref="Reward"/>.
/// </summary>
public class RewardConfiguration : IEntityTypeConfiguration<Reward>
{
    public void Configure(EntityTypeBuilder<Reward> builder)
    {
        builder.ToTable("rewards");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.ProgramId).IsRequired().HasColumnName("program_id");
        builder.Property(e => e.BusinessId).IsRequired().HasColumnName("business_id");

        builder.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(200)
            .HasColumnName("name");

        builder.Property(e => e.Description).HasMaxLength(500).HasColumnName("description");
        builder.Property(e => e.RequiredStamps).IsRequired().HasColumnName("required_stamps");
        builder.Property(e => e.Type).IsRequired().HasColumnName("type");

        builder.Property(e => e.Value)
            .IsRequired()
            .HasColumnType("decimal(18,2)")
            .HasColumnName("value");

        builder.Property(e => e.Percentage).HasColumnName("percentage");
        builder.Property(e => e.Status).IsRequired().HasColumnName("status");
        builder.Property(e => e.ExpirationHours).IsRequired().HasColumnName("expiration_hours");
        builder.Property(e => e.StampsToConsume).IsRequired().HasColumnName("stamps_to_consume");
        builder.Property(e => e.ServiceCatalogItemId).HasColumnName("service_catalog_item_id");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");

        // A reward a customer can never unlock is a configuration error.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("chk_rewards_required_stamps_positive", "\"required_stamps\" >= 1");
            t.HasCheckConstraint("chk_rewards_stamps_to_consume_non_negative", "\"stamps_to_consume\" >= 0");
        });

        builder.HasIndex(e => new { e.ProgramId, e.Status })
            .HasDatabaseName("IX_rewards_ProgramId_Status");

        builder.HasIndex(e => e.BusinessId)
            .HasDatabaseName("IX_rewards_BusinessId");

        builder.HasOne(e => e.Program)
            .WithMany(p => p.Rewards)
            .HasForeignKey(e => e.ProgramId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Business)
            .WithMany()
            .HasForeignKey(e => e.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
