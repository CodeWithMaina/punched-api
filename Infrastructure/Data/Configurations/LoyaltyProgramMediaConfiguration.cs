using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

public sealed class LoyaltyProgramMediaConfiguration : IEntityTypeConfiguration<LoyaltyProgramMedia>
{
    public void Configure(EntityTypeBuilder<LoyaltyProgramMedia> b)
    {
        b.ToTable("loyalty_program_media", t => t.HasCheckConstraint("ck_loyalty_program_media_order", "\"sort_order\" >= 0"));
        b.HasKey(x => new { x.LoyaltyProgramId, x.MediaId });
        b.Property(x => x.LoyaltyProgramId).HasColumnName("loyalty_program_id");
        b.Property(x => x.MediaId).HasColumnName("media_id");
        b.Property(x => x.Role).HasColumnName("role").HasMaxLength(20);
        b.Property(x => x.SortOrder).HasColumnName("sort_order");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasIndex(x => new { x.LoyaltyProgramId, x.SortOrder, x.CreatedAt }).HasDatabaseName("ix_loyalty_program_media_order");
        b.HasOne(x => x.LoyaltyProgram).WithMany().HasForeignKey(x => x.LoyaltyProgramId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Media).WithMany().HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.Restrict);
    }
}
