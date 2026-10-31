using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Seeding.Demo.AureliaLuxe;

internal sealed record AureliaLuxeReferralReward(Guid ReferrerId, Guid ReferralId, DateTime RewardedAt, int Stamps);

internal sealed record AureliaLuxeReferralResult(
    int Links,
    int Referrals,
    int RewardStamps,
    IReadOnlyList<AureliaLuxeReferralReward> Rewards);

/// <summary>
/// Creates the atelier's referral program activity: shareable links for the
/// guest advocates and referrals in every lifecycle state (pending, activated,
/// qualified, rewarded, expired). Rewarded referrals are reported back so the
/// loyalty ledger can credit the matching bonus stamps — the referral and the
/// stamp award stay one consistent story.
/// </summary>
internal static class AureliaLuxeReferralSeeder
{
    public static async Task<AureliaLuxeReferralResult> SeedAsync(
        ApplicationDbContext db, AureliaLuxeDemoContext demo, CancellationToken cancellationToken)
    {
        var program = await db.ReferralPrograms
            .FirstOrDefaultAsync(p => p.BusinessId == demo.BusinessId, cancellationToken);

        if (program is null || demo.Customers.Count < 10)
        {
            return new AureliaLuxeReferralResult(0, 0, 0, []);
        }

        var rng = new AureliaDemoRandom(11);
        var today = DateTime.UtcNow;
        var links = 0;
        var referrals = 0;
        var rewards = new List<AureliaLuxeReferralReward>();

        // Six advocates: the most engaged guests act as referrers.
        var referrers = demo.Customers.Take(6).ToList();
        var refereePool = demo.Customers.Skip(6).ToList();

        var statuses = new[]
        {
            ReferralStatus.Rewarded,
            ReferralStatus.Rewarded,
            ReferralStatus.Qualified,
            ReferralStatus.Activated,
            ReferralStatus.Pending,
            ReferralStatus.Expired,
        };

        for (var r = 0; r < referrers.Count; r++)
        {
            var referrer = referrers[r];
            var linkId = AureliaLuxeDemoIds.ReferralLink(referrer.Key);
            var linkCreatedAt = today.AddDays(-120 + (r * 6));

            var link = await db.ReferralLinks.FirstOrDefaultAsync(l => l.Id == linkId, cancellationToken);
            if (link is null)
            {
                link = new ReferralLink
                {
                    Id = linkId,
                    ReferrerId = referrer.User.Id,
                    BusinessId = demo.BusinessId,
                    Code = AureliaLuxeDemoIds.Token("ref-code", referrer.Key)[..8].ToUpperInvariant(),
                    SuccessfulReferrals = 0,
                    OpenCount = rng.Next(8, 60),
                    FirstOpenedAt = linkCreatedAt.AddDays(1),
                    LastOpenedAt = today.AddDays(-rng.Next(1, 20)),
                    IsActive = true,
                    CreatedAt = linkCreatedAt,
                };
                db.ReferralLinks.Add(link);
                links++;
            }

            var rewarded = 0;
            for (var j = 0; j < 3; j++)
            {
                var refereeIndex = ((r * 3) + j + 6) % refereePool.Count;
                var referee = refereePool[refereeIndex];
                if (referee.User.Id == referrer.User.Id)
                {
                    continue;
                }

                var referralId = AureliaLuxeDemoIds.Referral(referrer.Key, referee.Key);
                var exists = await db.Referrals.AnyAsync(x => x.Id == referralId, cancellationToken);
                if (exists)
                {
                    continue;
                }

                var status = statuses[(r + j) % statuses.Length];
                var createdAt = linkCreatedAt.AddDays(2 + j * 3);
                DateTime? activatedAt = status is ReferralStatus.Activated or ReferralStatus.Qualified or ReferralStatus.Rewarded
                    ? createdAt.AddDays(1)
                    : null;
                DateTime? qualifiedAt = status is ReferralStatus.Qualified or ReferralStatus.Rewarded
                    ? createdAt.AddDays(6)
                    : null;
                DateTime? rewardedAt = status == ReferralStatus.Rewarded
                    ? createdAt.AddDays(8)
                    : null;

                db.Referrals.Add(new Referral
                {
                    Id = referralId,
                    ReferralLinkId = linkId,
                    ReferrerId = referrer.User.Id,
                    RefereeId = referee.User.Id,
                    BusinessId = demo.BusinessId,
                    Status = status,
                    ActivatedAt = activatedAt,
                    QualifiedAt = qualifiedAt,
                    RewardedAt = rewardedAt,
                    ExpiresAt = status == ReferralStatus.Expired ? createdAt.AddDays(-1) : createdAt.AddDays(45),
                    CreatedAt = createdAt,
                });
                referrals++;

                if (status is ReferralStatus.Qualified or ReferralStatus.Rewarded)
                {
                    rewarded++;
                }

                if (rewardedAt is { } at)
                {
                    rewards.Add(new AureliaLuxeReferralReward(referrer.User.Id, referralId, at, 2));
                }
            }

            link.SuccessfulReferrals = rewarded;
        }

        await db.SaveChangesAsync(cancellationToken);

        return new AureliaLuxeReferralResult(links, referrals, rewards.Sum(r => r.Stamps), rewards);
    }
}
