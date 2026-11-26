using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Append-only staff lifecycle audit trail. No cascade delete from users or
/// businesses: history must survive both, and a deleted business's trail is
/// retained for retention/audit purposes.
/// </summary>
public class StaffAuditEventConfiguration : IEntityTypeConfiguration<StaffAuditEvent>
{
    public void Configure(EntityTypeBuilder<StaffAuditEvent> builder)
    {
        builder.ToTable("staff_audit_events");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessId).HasColumnName("business_id").IsRequired();
        builder.Property(x => x.StaffUserId).HasColumnName("staff_user_id");
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(40).IsRequired();
        builder.Property(x => x.Summary).HasColumnName("summary").HasMaxLength(300).IsRequired();
        builder.Property(x => x.DetailsJson).HasColumnName("details_json").HasMaxLength(4000);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        // The owner's activity feed: newest first, optionally narrowed to one member.
        builder.HasIndex(x => new { x.BusinessId, x.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_staff_audit_events_business_created");
        builder.HasIndex(x => new { x.BusinessId, x.StaffUserId, x.CreatedAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_staff_audit_events_business_staff_created");
    }
}
