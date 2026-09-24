using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for the immutable <see cref="CardDesignVersion"/>
/// presentation history.
/// </summary>
public class CardDesignVersionConfiguration : IEntityTypeConfiguration<CardDesignVersion>
{
    public void Configure(EntityTypeBuilder<CardDesignVersion> builder)
    {
        builder.ToTable("card_design_versions", t =>
        {
            t.HasCheckConstraint("ck_card_design_versions_number_positive", "\"version_number\" >= 1");
            // NOTE: use length() — equivalent to char_length() on PostgreSQL and also
            // supported by SQLite, which the test suite uses via EnsureCreated().
            // char_length() is PostgreSQL-only and would break provider portability.
            t.HasCheckConstraint("ck_card_design_versions_html_length", "length(\"html_template\") <= 50000");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.CardDesignId).IsRequired().HasColumnName("card_design_id");
        builder.Property(e => e.VersionNumber).IsRequired().HasColumnName("version_number");
        builder.Property(e => e.Name).IsRequired().HasMaxLength(100).HasColumnName("name");
        builder.Property(e => e.HtmlTemplate).IsRequired().HasColumnName("html_template");
        builder.Property(e => e.ConfigJson).HasColumnName("config_json");
        builder.Property(e => e.PublishedAt).IsRequired().HasColumnName("published_at");
        builder.Property(e => e.PublishedByUserId).HasColumnName("published_by_user_id");
        builder.Property(e => e.ChangeNote).HasMaxLength(500).HasColumnName("change_note");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");

        // Append-only history: one row per (design, version). This is what makes a
        // concurrent publish converge on a single winner rather than forking history.
        builder.HasIndex(e => new { e.CardDesignId, e.VersionNumber })
            .IsUnique()
            .HasDatabaseName("ux_card_design_versions_design_number");

        builder.HasOne(e => e.CardDesign)
            .WithMany(d => d.Versions)
            .HasForeignKey(e => e.CardDesignId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
