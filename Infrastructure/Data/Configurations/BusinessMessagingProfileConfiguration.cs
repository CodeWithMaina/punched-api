using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Maps <see cref="BusinessMessagingProfile"/> onto the
/// <c>business_messaging_profiles</c> table. One row per business.
/// </summary>
public class BusinessMessagingProfileConfiguration : IEntityTypeConfiguration<BusinessMessagingProfile>
{
    public void Configure(EntityTypeBuilder<BusinessMessagingProfile> builder)
    {
        builder.ToTable("business_messaging_profiles");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.BusinessId).HasColumnName("business_id").IsRequired();
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.LogoMediaId).HasColumnName("logo_media_id");
        builder.Property(x => x.PrimaryColor).HasColumnName("primary_color").HasMaxLength(20);
        builder.Property(x => x.ReplyTo).HasColumnName("reply_to").HasMaxLength(200);
        builder.Property(x => x.FooterNote).HasColumnName("footer_note").HasMaxLength(1000);
        builder.Property(x => x.SenderDomainId).HasColumnName("sender_domain_id").HasMaxLength(200);
        builder.Property(x => x.Timezone).HasColumnName("timezone").HasMaxLength(64);
        builder.Property(x => x.ReminderPolicyJson).HasColumnName("reminder_policy_json").HasDefaultValue("[1440,60]");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);

        // One profile per business.
        builder.HasIndex(x => x.BusinessId)
            .HasDatabaseName("ux_business_messaging_profiles_business_id")
            .IsUnique();
    }
}