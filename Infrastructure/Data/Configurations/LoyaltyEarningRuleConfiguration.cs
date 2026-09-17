using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for <see cref="LoyaltyEarningRule"/>.
/// </summary>
public class LoyaltyEarningRuleConfiguration : IEntityTypeConfiguration<LoyaltyEarningRule>
{
    public void Configure(EntityTypeBuilder<LoyaltyEarningRule> builder)
    {
        builder.ToTable("loyalty_earning_rules");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.ProgramId).IsRequired().HasColumnName("program_id");
        builder.Property(e => e.BusinessId).IsRequired().HasColumnName("business_id");

        builder.Property(e => e.Source).IsRequired().HasColumnName("source");
        builder.Property(e => e.StampAmount).IsRequired().HasColumnName("stamp_amount");
        builder.Property(e => e.StampingMode).IsRequired().HasColumnName("stamping_mode");
        builder.Property(e => e.Status).IsRequired().HasColumnName("status");

        builder.Property(e => e.Description).HasMaxLength(200).HasColumnName("description");
        builder.Property(e => e.QualifyingServiceId).HasColumnName("qualifying_service_id");
        builder.Property(e => e.ActivatedAt).HasColumnName("activated_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");

        // A rule that awards nothing is a configuration error.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("chk_loyalty_earning_rule_stamp_amount_positive", "\"stamp_amount\" >= 1");
        });

        // At most one rule per (program, source): the event pipeline must never
        // be ambiguous about which rule fires for a given qualifying event.
        builder.HasIndex(e => new { e.ProgramId, e.Source })
            .IsUnique()
            .HasDatabaseName("IX_loyalty_earning_rules_ProgramId_Source");

        builder.HasIndex(e => new { e.BusinessId, e.Status })
            .HasDatabaseName("IX_loyalty_earning_rules_BusinessId_Status");

        builder.HasOne(e => e.Program)
            .WithMany(p => p.EarningRules)
            .HasForeignKey(e => e.ProgramId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Business)
            .WithMany()
            .HasForeignKey(e => e.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
