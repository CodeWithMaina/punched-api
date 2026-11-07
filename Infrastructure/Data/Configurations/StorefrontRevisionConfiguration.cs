using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for the append-only <see cref="StorefrontRevision"/>
/// history.
///
/// <para>The unique <c>(business_id, revision_number)</c> index is the concurrency
/// guard: two writers racing for the same next number cannot both commit, so the
/// sequence stays gapless-per-writer instead of forking. The composite
/// <c>(business_id, created_at)</c> index serves the newest-first history feed
/// without touching the configuration payload.</para>
/// </summary>
public class StorefrontRevisionConfiguration : IEntityTypeConfiguration<StorefrontRevision>
{
    public void Configure(EntityTypeBuilder<StorefrontRevision> builder)
    {
        builder.ToTable("storefront_revisions", t =>
        {
            // length() — equivalent to char_length() on PostgreSQL and also
            // supported by SQLite, which the test suite uses via EnsureCreated().
            t.HasCheckConstraint("ck_storefront_revisions_number_positive", "\"revision_number\" >= 1");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(e => e.RevisionNumber).IsRequired().HasColumnName("revision_number");
        builder.Property(e => e.ConfigurationJson).IsRequired().HasColumnName("configuration_json");
        builder.Property(e => e.RevisionType)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>()
            .HasColumnName("revision_type");
        builder.Property(e => e.Description).HasMaxLength(500).HasColumnName("description");
        builder.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(e => e.SourceRevisionId).HasColumnName("source_revision_id");
        builder.Property(e => e.IsPublishedSnapshot)
            .IsRequired()
            .HasDefaultValue(false)
            .HasColumnName("is_published_snapshot");
        builder.Property(e => e.CreatedAt).IsRequired().HasColumnName("created_at");

        // Append-only history: one row per (business, revision number).
        builder.HasIndex(e => new { e.BusinessId, e.RevisionNumber })
            .IsUnique()
            .HasDatabaseName("ux_storefront_revisions_business_number");

        // The history feed: newest-first, per business.
        builder.HasIndex(e => new { e.BusinessId, e.CreatedAt })
            .HasDatabaseName("ix_storefront_revisions_business_created");

        // Filtering the feed by event type ("show me only publishes").
        builder.HasIndex(e => new { e.BusinessId, e.RevisionType })
            .HasDatabaseName("ix_storefront_revisions_business_type");

        builder.HasIndex(e => e.CreatedByUserId)
            .HasDatabaseName("ix_storefront_revisions_created_by");

        builder.HasIndex(e => e.SourceRevisionId)
            .HasDatabaseName("ix_storefront_revisions_source");

        builder.HasOne(e => e.Business)
            .WithMany()
            .HasForeignKey(e => e.BusinessId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        // Audit trails never orphan: the actor and the restored-from revision are
        // protected by restrictive deletes.
        builder.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.SourceRevision)
            .WithMany()
            .HasForeignKey(e => e.SourceRevisionId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
