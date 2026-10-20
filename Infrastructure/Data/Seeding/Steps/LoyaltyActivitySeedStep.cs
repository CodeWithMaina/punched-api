using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Seeding.Steps;

public sealed class LoyaltyActivitySeedStep : ISeedStep
{
    public string Name => "LoyaltyActivity";

    /// <summary>Flush to the database every N tracked rows to bound memory.</summary>
    private const int BatchSize = 500;

    public async Task ExecuteAsync(SeedExecutionContext context, CancellationToken cancellationToken)
    {
        var cardsCreated = 0;
        var stampsCreated = 0;
        var qrCreated = 0;
        var redemptionsCreated = 0;

        foreach (var businessDef in context.Scenario.Businesses)
        {
            var business = context.BusinessesByKey[businessDef.Key];
            var program = context.ActiveProgramsByBusiness[business.Id];

            var stampAwarders = new List<Guid> { context.UsersByKey[businessDef.OwnerUserKey].Id };
            stampAwarders.AddRange(businessDef.StaffUserKeys.Select(k => context.UsersByKey[k].Id));

            // Preload this business's cards/QRs/stamps/redemptions once instead
            // of one query per row (N+1). Deterministic IDs make upsert idempotent.
            var businessId = business.Id;
            var existingCards = await context.Db.LoyaltyCards
                .AsNoTracking()
                .Where(c => c.BusinessId == businessId)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);
            var cardIds = existingCards.ToHashSet();
            var existingQrIds = (await context.Db.QrTokens
                .AsNoTracking()
                .Where(q => q.BusinessId == businessId)
                .Select(q => q.Id)
                .ToListAsync(cancellationToken)).ToHashSet();
            var existingStampTokenIds = (await context.Db.Stamps
                .AsNoTracking()
                .Where(s => s.Card.BusinessId == businessId && s.QrTokenId != null)
                .Select(s => s.QrTokenId!.Value)
                .ToListAsync(cancellationToken)).ToHashSet();
            var existingRedemptionIds = (await context.Db.Redemptions
                .AsNoTracking()
                .Where(r => r.BusinessId == businessId)
                .Select(r => r.Id)
                .ToListAsync(cancellationToken)).ToHashSet();

            var pending = 0;

            for (var i = 0; i < businessDef.CustomerUserKeys.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var customer = context.UsersByKey[businessDef.CustomerUserKeys[i]];

                var enrolledAt = business.CreatedAt.AddDays(3 + (i * 2));
                var baseLifetime = businessDef.IsFocusBusiness ? 18 + (i % 14) : 6 + (i % 7);
                var rewardHold = i % 5 == 0 ? 1 : 0;
                var completedCycles = Math.Max(0, (baseLifetime / program.StampsRequired) - rewardHold);
                var totalStamps = rewardHold == 1
                    ? program.StampsRequired
                    : baseLifetime - (completedCycles * program.StampsRequired);
                var lifetimeStamps = (completedCycles * program.StampsRequired) + totalStamps;
                var lastStampAt = enrolledAt.AddDays(Math.Max(1, lifetimeStamps) * (businessDef.IsFocusBusiness ? 3 : 6));

                var cardId = DeterministicSeed.GuidFor("card", $"{businessDef.Key}:{customer.Id}");
                Domain.Entities.LoyaltyCard? card = null;
                var cardExists = cardIds.Contains(cardId);
                if (!cardExists)
                {
                    card = new Domain.Entities.LoyaltyCard
                    {
                        Id = cardId,
                        CreatedAt = enrolledAt,
                    };
                    context.Db.LoyaltyCards.Add(card);
                    cardIds.Add(cardId);
                    cardsCreated++;
                    if (++pending >= BatchSize)
                    {
                        await context.Db.SaveChangesAsync(cancellationToken);
                        pending = 0;
                    }
                }
                else
                {
                    card = await context.Db.LoyaltyCards.FirstAsync(c => c.Id == cardId, cancellationToken);
                }

                card.CustomerId = customer.Id;
                card.BusinessId = business.Id;
                card.ProgramId = program.Id;
                card.EnrolledAt = enrolledAt;
                card.TotalStamps = totalStamps;
                card.LifetimeStamps = lifetimeStamps;
                card.TotalRedemptions = completedCycles;
                card.LastStampAt = lifetimeStamps > 0 ? lastStampAt : null;
                card.RewardExpiresAt = rewardHold == 1
                    ? lastStampAt.AddHours(program.RewardExpirationHours)
                    : null;

                for (var stampNo = 1; stampNo <= lifetimeStamps; stampNo++)
                {
                    var stampedAt = enrolledAt.AddDays(stampNo * (businessDef.IsFocusBusiness ? 3 : 6));
                    var qrId = DeterministicSeed.GuidFor("qr", $"{cardId}:{stampNo}");

                    if (!existingQrIds.Contains(qrId))
                    {
                        context.Db.QrTokens.Add(new Domain.Entities.QrToken
                        {
                            Id = qrId,
                            CustomerId = customer.Id,
                            BusinessId = business.Id,
                            TokenHash = DeterministicSeed.TokenFor("qr", $"{cardId}:{stampNo}"),
                            ExpiresAt = stampedAt.AddSeconds(45),
                            IsUsed = true,
                            CreatedAt = stampedAt.AddSeconds(-15),
                        });
                        existingQrIds.Add(qrId);
                        qrCreated++;
                        if (++pending >= BatchSize)
                        {
                            await context.Db.SaveChangesAsync(cancellationToken);
                            pending = 0;
                        }
                    }

                    if (!existingStampTokenIds.Contains(qrId))
                    {
                        context.Db.Stamps.Add(new Domain.Entities.Stamp
                        {
                            Id = DeterministicSeed.GuidFor("stamp", $"{cardId}:{stampNo}"),
                            CardId = cardId,
                            StampNumber = (short)stampNo,
                            StampedAt = stampedAt,
                            QrTokenId = qrId,
                            AwardedByUserId = stampAwarders[stampNo % stampAwarders.Count],
                            CreatedAt = stampedAt,
                        });
                        existingStampTokenIds.Add(qrId);
                        stampsCreated++;
                        if (++pending >= BatchSize)
                        {
                            await context.Db.SaveChangesAsync(cancellationToken);
                            pending = 0;
                        }
                    }
                }

                for (var redemptionNo = 1; redemptionNo <= completedCycles; redemptionNo++)
                {
                    var redemptionId = DeterministicSeed.GuidFor("redemption", $"{cardId}:{redemptionNo}");
                    var redeemedAt = enrolledAt.AddDays((redemptionNo * program.StampsRequired * (businessDef.IsFocusBusiness ? 3 : 6)) + 1);

                    if (!existingRedemptionIds.Contains(redemptionId))
                    {
                        context.Db.Redemptions.Add(new Domain.Entities.Redemption
                        {
                            Id = redemptionId,
                            CardId = cardId,
                            BusinessId = business.Id,
                            RewardValue = program.RewardValue,
                            Status = RedemptionStatus.Fulfilled,
                            PayoutStatus = "completed",
                            RedeemedAt = redeemedAt,
                            PaidAt = redeemedAt.AddMinutes(15),
                            MpesaRef = $"MPESA-{businessDef.Key.ToUpperInvariant()}-{redemptionNo:D4}",
                            CreatedAt = redeemedAt,
                        });
                        existingRedemptionIds.Add(redemptionId);
                        redemptionsCreated++;
                        if (++pending >= BatchSize)
                        {
                            await context.Db.SaveChangesAsync(cancellationToken);
                            pending = 0;
                        }
                    }
                }

                // Keep the card aggregate in sync via a single batched UPDATE per
                // card (EfCore-less raw SQL keeps this provider-agnostic enough
                // for tests while avoiding re-materializing every card row).
                if (cardExists)
                {
                    await context.Db.LoyaltyCards
                        .Where(c => c.Id == cardId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(c => c.CustomerId, customer.Id)
                            .SetProperty(c => c.BusinessId, business.Id)
                            .SetProperty(c => c.ProgramId, program.Id)
                            .SetProperty(c => c.EnrolledAt, enrolledAt)
                            .SetProperty(c => c.TotalStamps, totalStamps)
                            .SetProperty(c => c.LifetimeStamps, lifetimeStamps)
                            .SetProperty(c => c.TotalRedemptions, completedCycles)
                            .SetProperty(c => c.LastStampAt, lifetimeStamps > 0 ? lastStampAt : null),
                            cancellationToken);
                }
                else
                {
                    card!.CustomerId = customer.Id;
                    card.BusinessId = business.Id;
                    card.ProgramId = program.Id;
                    card.EnrolledAt = enrolledAt;
                    card.TotalStamps = totalStamps;
                    card.LifetimeStamps = lifetimeStamps;
                    card.TotalRedemptions = completedCycles;
                    card.LastStampAt = lifetimeStamps > 0 ? lastStampAt : null;
                    card.RewardExpiresAt = rewardHold == 1
                        ? lastStampAt.AddHours(program.RewardExpirationHours)
                        : null;
                }
            }

            if (pending > 0)
            {
                await context.Db.SaveChangesAsync(cancellationToken);
                pending = 0;
            }
        }

        await context.Db.SaveChangesAsync(cancellationToken);

        if (context.Report.Mode == SeedExecutionMode.AppendData)
        {
            var appendStats = await AppendAdditionalActivityAsync(context, cancellationToken);
            stampsCreated += appendStats.Stamps;
            qrCreated += appendStats.QrTokens;
            redemptionsCreated += appendStats.Redemptions;
        }

        context.Report.Counts["LoyaltyCardsCreated"] = cardsCreated;
        context.Report.Counts["QrTokensCreated"] = qrCreated;
        context.Report.Counts["StampsCreated"] = stampsCreated;
        context.Report.Counts["RedemptionsCreated"] = redemptionsCreated;
    }

    private static async Task<(int Stamps, int QrTokens, int Redemptions)> AppendAdditionalActivityAsync(
        SeedExecutionContext context,
        CancellationToken cancellationToken)
    {
        var cards = await context.Db.LoyaltyCards
            .Include(c => c.Program)
            .Where(c => context.BusinessesByKey.Values.Select(b => b.Id).Contains(c.BusinessId))
            .OrderBy(c => c.LastStampAt)
            .Take(20)
            .ToListAsync(cancellationToken);

        var createdStamps = 0;
        var createdQr = 0;
        var createdRedemptions = 0;
        var nonce = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

        foreach (var card in cards)
        {
            var extraStamps = context.Random.NextInt(1, 4);
            var lastAt = card.LastStampAt ?? card.EnrolledAt;

            for (var i = 0; i < extraStamps; i++)
            {
                var stampAt = lastAt.AddDays(i + 1);
                var qr = new Domain.Entities.QrToken
                {
                    Id = Guid.NewGuid(),
                    CustomerId = card.CustomerId,
                    BusinessId = card.BusinessId,
                    TokenHash = DeterministicSeed.TokenFor("append-qr", $"{card.Id}:{nonce}:{i}"),
                    ExpiresAt = stampAt.AddSeconds(45),
                    IsUsed = true,
                    CreatedAt = stampAt.AddSeconds(-20),
                };
                context.Db.QrTokens.Add(qr);
                createdQr++;

                card.TotalStamps++;
                card.LifetimeStamps++;
                card.LastStampAt = stampAt;

                var stamp = new Domain.Entities.Stamp
                {
                    Id = Guid.NewGuid(),
                    CardId = card.Id,
                    StampNumber = (short)card.LifetimeStamps,
                    StampedAt = stampAt,
                    QrTokenId = qr.Id,
                    AwardedByUserId = null,
                    CreatedAt = stampAt,
                };
                context.Db.Stamps.Add(stamp);
                createdStamps++;
            }

            if (card.TotalStamps >= card.Program.StampsRequired)
            {
                var redemption = new Domain.Entities.Redemption
                {
                    Id = Guid.NewGuid(),
                    CardId = card.Id,
                    BusinessId = card.BusinessId,
                    RewardValue = card.Program.RewardValue,
                    Status = RedemptionStatus.Fulfilled,
                    PayoutStatus = "completed",
                    RedeemedAt = card.LastStampAt ?? DateTime.UtcNow,
                    PaidAt = card.LastStampAt?.AddMinutes(10),
                    MpesaRef = $"APPEND-{nonce}-{context.Random.NextInt(1000, 9999)}",
                    CreatedAt = card.LastStampAt ?? DateTime.UtcNow,
                };
                context.Db.Redemptions.Add(redemption);
                createdRedemptions++;

                card.TotalStamps = 0;
                card.TotalRedemptions++;
            }
        }

        await context.Db.SaveChangesAsync(cancellationToken);
        return (createdStamps, createdQr, createdRedemptions);
    }
}
