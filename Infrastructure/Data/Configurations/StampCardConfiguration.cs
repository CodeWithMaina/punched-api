using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for <see cref="StampCard"/>.
/// One campaign (LoyaltyProgram) owns many stamp cards.
/// </summary>
public class StampCardConfiguration : IEntityTypeConfiguration<StampCard>
{
    public void Configure(EntityTypeBuilder<StampCard> builder)
    {
        builder.ToTable("stamp_cards");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.ProgramId).IsRequired().HasColumnName("program_id");
        builder.Property(e => e.BusinessId).IsRequired().HasColumnName("business_id");

        builder.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(100)
            .HasColumnName("name")
            .HasDefaultValue("Stamp Card");

        builder.Property(e => e.Description).HasMaxLength(500).HasColumnName("description");

        builder.Property(e => e.StampsRequired).IsRequired().HasColumnName("stamps_required");
        builder.Property(e => e.RewardDescription).IsRequired().HasMaxLength(200).HasColumnName("reward_description");
        builder.Property(e => e.RewardValue).HasPrecision(10, 2).HasColumnName("reward_value");

        builder.Property(e => e.Status)
            .IsRequired()
            .HasColumnName("status")
            .HasDefaultValue(StampCardStatus.Draft);

        builder.Property(e => e.CardDesignId).HasColumnName("card_design_id");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("chk_stamp_card_stamps_required_positive", "\"stamps_required\" > 0");
        });

        builder.HasIndex(e => e.ProgramId);
        builder.HasIndex(e => e.BusinessId);
        builder.HasIndex(e => e.CardDesignId);

        builder.HasOne(e => e.Program)
            .WithMany()
            .HasForeignKey(e => e.ProgramId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Business)
            .WithMany()
            .HasForeignKey(e => e.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.CardDesign)
            .WithMany(d => d.StampCards)
            .HasForeignKey(e => e.CardDesignId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
