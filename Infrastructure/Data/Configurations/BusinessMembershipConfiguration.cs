using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// <c>business_memberships</c> — the authoritative user ↔ business relationship.
/// Role and status are stored as text so the database itself can enforce the
/// allowed values and the partial "one active owner per business" invariant.
/// </summary>
public class BusinessMembershipConfiguration : IEntityTypeConfiguration<BusinessMembership>
{
    public void Configure(EntityTypeBuilder<BusinessMembership> builder)
    {
        builder.ToTable("business_memberships", t =>
        {
            t.HasCheckConstraint(
                "ck_business_memberships_role",
                "role IN ('Owner', 'Staff', 'Customer')");
            t.HasCheckConstraint(
                "ck_business_memberships_status",
                "status IN ('Active', 'Invited', 'Suspended', 'Left')");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.UserId).IsRequired().HasColumnName("user_id");
        builder.Property(e => e.BusinessId).IsRequired().HasColumnName("business_id");

        // Stored as text: readable in the database and comparable by the CHECK
        // constraints / partial indexes above and below.
        builder.Property(e => e.Role)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(16)
            .HasColumnName("role");

        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(16)
            .HasColumnName("status");

        builder.Property(e => e.InvitedByUserId).HasColumnName("invited_by_user_id");
        builder.Property(e => e.JoinedAt).IsRequired().HasColumnName("joined_at");
        builder.Property(e => e.EndedAt).HasColumnName("ended_at");
        builder.Property(e => e.UpdatedAt).IsRequired().HasColumnName("updated_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");

        // One membership per person per business — the race-safe guarantee that
        // "join twice" can never create two rows (the loser gets a unique
        // violation, which the service answers idempotently).
        builder.HasIndex(e => new { e.UserId, e.BusinessId })
            .IsUnique()
            .HasDatabaseName("ux_business_memberships_user_business");

        // Authorization lookups: "does this identity have an active membership
        // with role R?" and "who are the active members of this business?"
        builder.HasIndex(e => new { e.UserId, e.Status, e.Role })
            .HasDatabaseName("ix_business_memberships_user_status_role");

        builder.HasIndex(e => new { e.BusinessId, e.Role, e.Status })
            .HasDatabaseName("ix_business_memberships_business_role_status");

        // Exactly one active Owner per business, enforced by the database.
        builder.HasIndex(e => e.BusinessId)
            .IsUnique()
            .HasFilter("role = 'Owner' AND status = 'Active'")
            .HasDatabaseName("ux_business_memberships_single_owner");

        builder.HasOne(e => e.User)
            .WithMany(u => u.BusinessMemberships)
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Business)
            .WithMany(b => b.Memberships)
            .HasForeignKey(e => e.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(e => e.InvitedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
