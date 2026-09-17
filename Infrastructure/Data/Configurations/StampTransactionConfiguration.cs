using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for the immutable <see cref="StampTransaction"/> ledger.
/// </summary>
public class StampTransactionConfiguration : IEntityTypeConfiguration<StampTransaction>
{
    public void Configure(EntityTypeBuilder<StampTransaction> builder)
    {
        builder.ToTable("stamp_transactions");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(e => e.ProgramId).IsRequired().HasColumnName("program_id");
        builder.Property(e => e.CardId).IsRequired().HasColumnName("card_id");
        builder.Property(e => e.CustomerId).IsRequired().HasColumnName("customer_id");

        builder.Property(e => e.Amount).IsRequired().HasColumnName("amount");
        builder.Property(e => e.Direction).IsRequired().HasColumnName("direction");

        builder.Property(e => e.Source)
            .IsRequired()
            .HasMaxLength(30)
            .HasColumnName("source");

        builder.Property(e => e.SourceId).HasColumnName("source_id");
        builder.Property(e => e.EarningRuleId).HasColumnName("earning_rule_id");
        builder.Property(e => e.IsAutomatic).IsRequired().HasColumnName("is_automatic");

        builder.Property(e => e.Reason).HasMaxLength(500).HasColumnName("reason");
        builder.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(e => e.CreatedByRole).HasMaxLength(20).HasColumnName("created_by_role");
        builder.Property(e => e.IdempotencyKey).HasMaxLength(200).HasColumnName("idempotency_key");
        builder.Property(e => e.MetadataJson).HasColumnName("metadata_json");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");

        // Amount is always positive; the sign lives in "direction".
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("chk_stamp_transaction_amount_positive", "\"amount\" >= 1");
        });

        // Idempotency: a retried domain event can never create a second
        // transaction for the same (program, rule, source, sourceId). NULLs are
        // permitted repeatedly by PostgreSQL, which is what we want for manual
        // awards that carry no idempotency key.
        builder.HasIndex(e => e.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("IX_stamp_transactions_IdempotencyKey");

        builder.HasIndex(e => new { e.CardId, e.CreatedAt })
            .HasDatabaseName("IX_stamp_transactions_CardId_CreatedAt");

        builder.HasIndex(e => new { e.BusinessId, e.CreatedAt })
            .HasDatabaseName("IX_stamp_transactions_BusinessId_CreatedAt");

        builder.HasIndex(e => new { e.CustomerId, e.CreatedAt })
            .HasDatabaseName("IX_stamp_transactions_CustomerId_CreatedAt");

        builder.HasOne(e => e.Program)
            .WithMany(p => p.StampTransactions)
            .HasForeignKey(e => e.ProgramId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Card)
            .WithMany(c => c.StampTransactions)
            .HasForeignKey(e => e.CardId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.EarningRule)
            .WithMany()
            .HasForeignKey(e => e.EarningRuleId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
