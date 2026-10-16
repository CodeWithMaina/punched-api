using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Maps <see cref="AutomationRule"/> onto the <c>automation_rules</c> table.
/// Tenant-scoped by <c>business_id</c>; unique name per business.
/// </summary>
public class AutomationRuleConfiguration : IEntityTypeConfiguration<AutomationRule>
{
    public void Configure(EntityTypeBuilder<AutomationRule> builder)
    {
        builder.ToTable("automation_rules");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.BusinessId).HasColumnName("business_id").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(1000);
        builder.Property(x => x.Trigger).HasColumnName("trigger").HasMaxLength(100).IsRequired();
        builder.Property(x => x.ConditionsJson).HasColumnName("conditions_json").HasColumnType("jsonb").HasDefaultValue("{}").IsRequired();
        builder.Property(x => x.ActionsJson).HasColumnName("actions_json").HasColumnType("jsonb").HasDefaultValue("[]").IsRequired();
        builder.Property(x => x.RecipientsJson).HasColumnName("recipients_json").HasColumnType("jsonb").HasDefaultValue("[]").IsRequired();
        builder.Property(x => x.Enabled).HasColumnName("enabled").HasDefaultValue(false);
        builder.Property(x => x.Version).HasColumnName("version").HasDefaultValue(1);
        builder.Property(x => x.DailyActionCap).HasColumnName("daily_action_cap");
        builder.Property(x => x.FrequencyCapMinutes).HasColumnName("frequency_cap_minutes");
        builder.Property(x => x.QuietHoursStart).HasColumnName("quiet_hours_start");
        builder.Property(x => x.QuietHoursEnd).HasColumnName("quiet_hours_end");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);

        // Tenant-scoped rule lookups.
        builder.HasIndex(x => new { x.BusinessId, x.Enabled })
            .HasDatabaseName("ix_automation_rules_business_enabled");
        builder.HasIndex(x => new { x.BusinessId, x.Trigger })
            .HasDatabaseName("ix_automation_rules_business_trigger");
        // Unique name per business (dedupe on upsert).
        builder.HasIndex(x => new { x.BusinessId, x.Name })
            .HasDatabaseName("ux_automation_rules_business_name")
            .IsUnique();
    }
}