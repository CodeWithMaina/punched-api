using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Seeding.Demo.AureliaLuxe;

internal sealed record AureliaLuxeLoyaltyResult(
    int Cards,
    int Stamps,
    int QrTokens,
    int Transactions,
    int Adjustments,
    int Entitlements,
    int Redemptions);

/// <summary>
/// Rebuilds each guest's loyalty state so the demo covers every stage of the
/// programme — no stamps yet, a few stamps, nearly complete, card complete and
/// waiting to be claimed, and already redeemed.
///
/// The balance is written from an explicit ledger: every credit and debit that
/// makes up <c>TotalStamps</c> is materialised as a <see cref="StampTransaction"/>
/// (the platform's source of truth for balances), while the <see cref="Stamp"/>
/// rows remain the QR-scan audit log. The two can therefore never disagree.
/// </summary>
internal static class AureliaLuxeLoyaltySeeder
{
    public static async Task<AureliaLuxeLoyaltyResult> SeedAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var program = await db.LoyaltyPrograms
            .Where(p => p.BusinessId == demo.BusinessId && p.Name == AureliaLuxeDemoData.ProgramName)
            .FirstOrDefaultAsync(cancellationToken);

        var card = await db.StampCards
            .Where(c => c.BusinessId == demo.BusinessId && c.Status == StampCardStatus.Active)
            .FirstOrDefaultAsync(cancellationToken);

        var reward = await db.Rewards
            .Where(r => r.BusinessId == demo.BusinessId && r.Status == RewardStatus.Active)
            .FirstOrDefaultAsync(cancellationToken);

        if (program is null || card is null || reward is null)
        {
            return new AureliaLuxeLoyaltyResult(0, 0, 0, 0, 0, 0, 0);
        }

        var earningRule = await db.LoyaltyEarningRules
            .Where(r => r.ProgramId == program.Id && r.Source == EarningSource.Appointment)
            .FirstOrDefaultAsync(cancellationToken);

        var loyaltyCards = await db.LoyaltyCards
            .Where(c => c.BusinessId == demo.BusinessId)
            .ToDictionaryAsync(c => c.CustomerId, cancellationToken);

        var referralRewards = await db.Referrals
            .Where(r => r.BusinessId == demo.BusinessId && r.Status == ReferralStatus.Rewarded && r.RewardedAt != null)
            .Select(r => new AureliaLuxeReferralRewardRow(r.Id, r.ReferrerId, r.RewardedAt!.Value))
            .ToListAsync(cancellationToken);

        var rewardedByCustomer = referralRewards
            .GroupBy(r => r.ReferrerId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.RewardedAt).ToList());

        var result = new AureliaLuxeLoyaltyResult(0, 0, 0, 0, 0, 0, 0);

        for (var index = 0; index < demo.Customers.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var customer = demo.Customers[index];
            if (!loyaltyCards.TryGetValue(customer.User.Id, out var loyaltyCard))
            {
                continue;
            }

            var plan = BuildPlan(index, card, customer, rewardedByCustomer);

            WriteLedger(db, demo, plan, loyaltyCard, program, card, reward, customer);

            loyaltyCard.ProgramId = program.Id;
            loyaltyCard.StampCardId = card.Id;
            loyaltyCard.RequiredStamps = card.StampsRequired;
            loyaltyCard.RulesVersion = card.RulesVersion;
            loyaltyCard.TotalStamps = plan.CurrentStamps;
            loyaltyCard.LifetimeStamps = plan.CreditCount;
            loyaltyCard.TotalRedemptions = plan.Cycles;
            loyaltyCard.LastStampAt = plan.LastCreditAt;
            loyaltyCard.RewardExpiresAt = plan.RewardExpiresAt;
            loyaltyCard.EnrolledAt = plan.EnrolledAt;

            result = result with
            {
                Cards = result.Cards + 1,
                Stamps = result.Stamps + plan.ScanStamps,
                QrTokens = result.QrTokens + plan.ScanStamps,
                // One ledger row per credit, plus one row per debit (a debit may
                // consume several stamps at once, so DebitCount is not the row count).
                Transactions = result.Transactions + plan.CreditCount + plan.Debits.Count,
                Adjustments = result.Adjustments + (plan.Adjustment == 0 ? 0 : 1),
                Entitlements = result.Entitlements + plan.Cycles + (plan.RewardReady ? 1 : 0),
                Redemptions = result.Redemptions + plan.Cycles,
            };
        }

        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private static AureliaLuxeLoyaltyPlan BuildPlan(
        int index,
        StampCard card,
        AureliaLuxeCustomerHandle customer,
        IReadOnlyDictionary<Guid, List<AureliaLuxeReferralRewardRow>> rewardedByCustomer)
    {
        var required = card.StampsRequired;
        var rng = new AureliaDemoRandom(100 + index);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Nine deterministic lifecycle states so every loyalty surface has data.
        var (cycles, current) = (index % 9) switch
        {
            0 => (0, 7),
            1 => (0, 0),                 // brand-new guest
            2 => (0, 2),
            3 => (0, 5),
            4 => (0, required - 1),      // one stamp short
            5 => (0, required),          // card complete, reward unclaimed
            6 => (1, 3),                 // redeemed once
            7 => (2, required),          // redeemed twice, complete again
            _ => (0, 4),
        };

        var isLapsed = index % 7 == 3;
        var lastStampDate = isLapsed
            ? today.AddDays(-(70 + (index % 12)))
            : today.AddDays(-(1 + (index % 14)));

        var enrolledAt = lastStampDate.AddDays(-(30 + (index % 90)))
            .ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc);

        // Referral bonus stamps land in the guest's current cycle.
        var referralStamps = rewardedByCustomer.TryGetValue(customer.User.Id, out var rewards)
            ? rewards.Count * 2
            : 0;

        var bonusApplied = 0;
        var bonus = referralStamps;
        while (bonus > 0)
        {
            var room = required - current;
            if (room <= 0)
            {
                cycles++;
                current = 0;
                room = required;
            }

            var take = Math.Min(room, bonus);
            current += take;
            bonus -= take;
            bonusApplied += take;
        }

        // One owner correction per customer, for a realistic adjustment audit.
        var adjustment = index % 11 == 5 ? 2 : index % 13 == 6 ? -1 : 0;
        if (adjustment < 0 && current + adjustment < 0)
        {
            adjustment = 0;
        }

        var baseCurrent = current;
        var currentStamps = baseCurrent + adjustment;
        var adjustmentCredits = Math.Max(0, adjustment);
        var adjustmentDebits = Math.Max(0, -adjustment);

        var creditCount = (cycles * required) + baseCurrent + adjustmentCredits;
        var debitCount = (cycles * required) + adjustmentDebits;

        var gap = 3 + (index % 6);
        var times = new List<DateTime>();
        var cursor = lastStampDate.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc);
        for (var k = 0; k < creditCount; k++)
        {
            times.Add(cursor);
            cursor = cursor.AddDays(-gap).AddMinutes(rng.Next(-45, 46));
        }

        times.Reverse();

        var credits = new List<AureliaLuxeCredit>();
        var referralSlots = bonusApplied;
        for (var k = 0; k < creditCount; k++)
        {
            var at = times[k];
            if (k >= creditCount - referralSlots && referralSlots > 0 && rewards is not null)
            {
                var rewardRow = rewards[(k - (creditCount - referralSlots)) % rewards.Count];
                credits.Add(new AureliaLuxeCredit(at, StampTransactions.Referral, rewardRow.Id, false));
                continue;
            }

            if (adjustmentCredits > 0 && k == creditCount - referralSlots - 1)
            {
                credits.Add(new AureliaLuxeCredit(at, StampTransactions.Adjustment, null, false));
                continue;
            }

            credits.Add(new AureliaLuxeCredit(at, StampTransactions.Scan, null, true));
        }

        var debits = new List<AureliaLuxeDebit>();
        for (var cycle = 1; cycle <= cycles; cycle++)
        {
            var boundaryIndex = Math.Min(creditCount - 1, (cycle * required) - 1);
            var at = times[Math.Max(0, boundaryIndex)].AddDays(1);
            debits.Add(new AureliaLuxeDebit(at, StampTransactions.Redemption, required));
        }

        if (adjustmentDebits > 0)
        {
            debits.Add(new AureliaLuxeDebit(times[^1].AddHours(6), StampTransactions.Adjustment, adjustmentDebits));
        }

        var rewardReady = currentStamps >= required;
        var lastCreditAt = creditCount > 0 ? times[^1] : (DateTime?)null;

        return new AureliaLuxeLoyaltyPlan(
            CustomerKey: customer.Key,
            CustomerId: customer.User.Id,
            Cycles: cycles,
            CurrentStamps: currentStamps,
            CreditCount: creditCount,
            DebitCount: debitCount,
            ScanStamps: credits.Count(c => c.IsScan),
            Adjustment: adjustment,
            RewardReady: rewardReady,
            EnrolledAt: enrolledAt,
            LastCreditAt: lastCreditAt,
            RewardExpiresAt: rewardReady && lastCreditAt is { } last
                ? last.AddHours(card.RewardValue > 0 ? AureliaLuxeDemoData.RewardExpirationHours : 0)
                : null,
            Credits: credits,
            Debits: debits);
    }

    private static void WriteLedger(
        ApplicationDbContext db,
        AureliaLuxeDemoContext demo,
        AureliaLuxeLoyaltyPlan plan,
        LoyaltyCard loyaltyCard,
        LoyaltyProgram program,
        StampCard card,
        Reward reward,
        AureliaLuxeCustomerHandle customer)
    {
        var staff = demo.Staff.Where(s => s.Definition.ServiceKeys.Length > 0).ToList();
        var stampNumber = 0;

        // ── Credits: QR-scan audit rows + the ledger entries ────
        foreach (var (credit, index) in plan.Credits.Select((c, i) => (c, i)))
        {
            var sourceId = credit.SourceId;
            var awardedBy = staff[index % staff.Count].User.Id;
            var source = credit.IsScan ? StampTransactions.Scan : credit.Source;

            if (credit.IsScan)
            {
                stampNumber++;

                var qrId = AureliaLuxeDemoIds.QrToken(plan.CustomerKey, stampNumber);
                db.QrTokens.Add(new QrToken
                {
                    Id = qrId,
                    CustomerId = plan.CustomerId,
                    BusinessId = demo.BusinessId,
                    TokenHash = AureliaLuxeDemoIds.Token("qr", $"{plan.CustomerKey}:{stampNumber}"),
                    ExpiresAt = credit.At.AddSeconds(60),
                    IsUsed = true,
                    CreatedAt = credit.At.AddSeconds(-20),
                });

                db.Stamps.Add(new Stamp
                {
                    Id = AureliaLuxeDemoIds.Stamp(plan.CustomerKey, stampNumber),
                    CardId = loyaltyCard.Id,
                    StampNumber = (short)stampNumber,
                    StampedAt = credit.At,
                    QrTokenId = qrId,
                    AwardedByUserId = awardedBy,
                    Source = StampSource.Scan,
                    UnlockedAt = credit.At,
                    CreatedAt = credit.At,
                });
            }

            db.StampTransactions.Add(new StampTransaction
            {
                Id = AureliaLuxeDemoIds.StampTransaction(plan.CustomerKey, index + 1),
                BusinessId = demo.BusinessId,
                ProgramId = program.Id,
                CardId = loyaltyCard.Id,
                CustomerId = plan.CustomerId,
                Amount = 1,
                Direction = StampTransactionDirection.Credit,
                Source = source,
                SourceId = sourceId,
                EarningRuleId = null,
                IsAutomatic = false,
                Reason = source switch
                {
                    StampTransactions.Referral => "Referral bonus stamps.",
                    StampTransactions.Adjustment => "Owner correction: goodwill stamps.",
                    _ => "In-salon stamp awarded at the counter.",
                },
                CreatedByUserId = source == StampTransactions.Scan ? awardedBy : demo.Owner.Id,
                CreatedByRole = "Staff",
                // One deterministic key per credit: a referral can legitimately
                // award more than one bonus stamp, so the referral id alone is
                // not unique within a card.
                IdempotencyKey = source == StampTransactions.Referral && sourceId is { } refId
                    ? $"{program.Id}:referral:{refId}:{index}"
                    : null,
                MetadataJson = "{\"seeded\":true}",
                CreatedAt = credit.At,
            });
        }

        // ── Debits: stamp consumption on redemption + corrections ──
        var debitOrdinal = 0;
        foreach (var debit in plan.Debits)
        {
            debitOrdinal++;
            db.StampTransactions.Add(new StampTransaction
            {
                Id = DeterministicSeed.GuidFor("aurelia-demo", $"stamp-tx-debit:{plan.CustomerKey}:{debitOrdinal}"),
                BusinessId = demo.BusinessId,
                ProgramId = program.Id,
                CardId = loyaltyCard.Id,
                CustomerId = plan.CustomerId,
                Amount = debit.Amount,
                Direction = StampTransactionDirection.Debit,
                Source = debit.Source,
                SourceId = null,
                IsAutomatic = false,
                Reason = debit.Source == StampTransactions.Redemption
                    ? "Stamps consumed to claim the atelier reward."
                    : "Owner correction: duplicate stamps removed.",
                CreatedByUserId = demo.Owner.Id,
                CreatedByRole = "Business",
                MetadataJson = "{\"seeded\":true}",
                CreatedAt = debit.At,
            });
        }

        // ── Adjustment audit row ────────────────────────────────
        if (plan.Adjustment != 0)
        {
            db.StampAdjustments.Add(new StampAdjustment
            {
                Id = AureliaLuxeDemoIds.StampAdjustment(plan.CustomerKey),
                CardId = loyaltyCard.Id,
                AdjustedByUserId = demo.Owner.Id,
                AdjustedByRole = "Business",
                Delta = plan.Adjustment,
                Reason = plan.Adjustment > 0
                    ? StampAdjustmentReason.Goodwill
                    : StampAdjustmentReason.ManualCorrection,
                Note = plan.Adjustment > 0
                    ? "Goodwill stamps for a delayed appointment."
                    : "Duplicate stamp removed after a double scan.",
                CreatedAt = plan.LastCreditAt ?? plan.EnrolledAt,
            });
        }

        WriteRewards(db, demo, plan, loyaltyCard, program, reward, staff);
    }

    /// <summary>
    /// Materialises the reward side of the ledger: one fulfilled redemption plus
    /// its redeemed entitlement for every completed cycle, and an unlocked
    /// entitlement when the current cycle is already complete.
    /// </summary>
    private static void WriteRewards(
        ApplicationDbContext db,
        AureliaLuxeDemoContext demo,
        AureliaLuxeLoyaltyPlan plan,
        LoyaltyCard loyaltyCard,
        LoyaltyProgram program,
        Reward reward,
        IReadOnlyList<AureliaLuxeStaffHandle> staff)
    {
        for (var cycle = 1; cycle <= plan.Cycles; cycle++)
        {
            var redemptionId = AureliaLuxeDemoIds.Redemption(plan.CustomerKey, cycle);
            var entitlementId = AureliaLuxeDemoIds.RewardEntitlement(plan.CustomerKey, cycle);

            var boundaryIndex = Math.Min(plan.Credits.Count - 1, (cycle * reward.StampsToConsume) - 1);
            var claimedAt = plan.Credits[Math.Max(0, boundaryIndex)].At.AddDays(1);
            var fulfiller = staff[(cycle + (plan.CustomerId.GetHashCode() & 0x7FFFFFFF)) % staff.Count].User.Id;

            db.Redemptions.Add(new Redemption
            {
                Id = redemptionId,
                CardId = loyaltyCard.Id,
                BusinessId = demo.BusinessId,
                PerformedByUserId = plan.CustomerId,
                PerformedByRole = "Customer",
                RewardValue = reward.Value,
                Status = RedemptionStatus.Fulfilled,
                FulfilledByUserId = fulfiller,
                FulfilledAt = claimedAt,
                FulfilmentCodeHash = AureliaLuxeDemoIds.Token("fulfilment", $"{plan.CustomerKey}:{cycle}"),
                PayoutStatus = "completed",
                StampsConsumed = reward.StampsToConsume,
                MpesaRef = $"MPESA-AUR-{plan.CustomerKey[^2..].ToUpperInvariant()}{cycle:D2}",
                RedeemedAt = claimedAt,
                PaidAt = claimedAt.AddMinutes(20),
                CreatedAt = claimedAt,
            });

            db.RewardEntitlements.Add(new RewardEntitlement
            {
                Id = entitlementId,
                BusinessId = demo.BusinessId,
                ProgramId = program.Id,
                CardId = loyaltyCard.Id,
                CustomerId = plan.CustomerId,
                RewardId = reward.Id,
                RewardName = reward.Name,
                RequiredStamps = reward.RequiredStamps,
                StampsToConsume = reward.StampsToConsume,
                RewardValue = reward.Value,
                Status = RewardEntitlementStatus.Redeemed,
                UnlockedAt = claimedAt.AddHours(-6),
                ExpiresAt = reward.ExpirationHours > 0 ? claimedAt.AddHours(reward.ExpirationHours) : null,
                RedeemedAt = claimedAt,
                RedemptionId = redemptionId,
                UnlockKey = $"{loyaltyCard.Id}:{reward.Id}:{cycle}",
                CreatedAt = claimedAt.AddHours(-6),
            });
        }

        if (!plan.RewardReady || plan.LastCreditAt is not { } unlockedAt)
        {
            return;
        }

        var currentCycle = plan.Cycles + 1;
        db.RewardEntitlements.Add(new RewardEntitlement
        {
            Id = AureliaLuxeDemoIds.RewardEntitlement(plan.CustomerKey, currentCycle),
            BusinessId = demo.BusinessId,
            ProgramId = program.Id,
            CardId = loyaltyCard.Id,
            CustomerId = plan.CustomerId,
            RewardId = reward.Id,
            RewardName = reward.Name,
            RequiredStamps = reward.RequiredStamps,
            StampsToConsume = reward.StampsToConsume,
            RewardValue = reward.Value,
            Status = RewardEntitlementStatus.Unlocked,
            UnlockedAt = unlockedAt,
            ExpiresAt = reward.ExpirationHours > 0 ? unlockedAt.AddHours(reward.ExpirationHours) : null,
            UnlockKey = $"{loyaltyCard.Id}:{reward.Id}:{currentCycle}",
            CreatedAt = unlockedAt,
        });
    }
}

internal sealed record AureliaLuxeReferralRewardRow(Guid Id, Guid ReferrerId, DateTime RewardedAt);

internal sealed record AureliaLuxeCredit(DateTime At, string Source, Guid? SourceId, bool IsScan);

internal sealed record AureliaLuxeDebit(DateTime At, string Source, int Amount);

internal sealed record AureliaLuxeLoyaltyPlan(
    string CustomerKey,
    Guid CustomerId,
    int Cycles,
    int CurrentStamps,
    int CreditCount,
    int DebitCount,
    int ScanStamps,
    int Adjustment,
    bool RewardReady,
    DateTime EnrolledAt,
    DateTime? LastCreditAt,
    DateTime? RewardExpiresAt,
    IReadOnlyList<AureliaLuxeCredit> Credits,
    IReadOnlyList<AureliaLuxeDebit> Debits);
