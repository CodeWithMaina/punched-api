using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Services;

namespace PunchedApi.Infrastructure.Data.Seeding.Steps;

public sealed class BusinessSeedStep : ISeedStep
{
    public string Name => "Business";

    public async Task ExecuteAsync(SeedExecutionContext context, CancellationToken cancellationToken)
    {
        var ownerIds = context.Scenario.Businesses
            .Select(b => context.UsersByKey[b.OwnerUserKey].Id)
            .Distinct()
            .ToList();

        var existingByOwner = await context.Db.Businesses
            .Where(b => b.OwnerId != null && ownerIds.Contains(b.OwnerId.Value))
            .ToDictionaryAsync(b => b.OwnerId!.Value, cancellationToken);

        var existingByNameLocation = await context.Db.Businesses
            .Where(b => context.Scenario.Businesses.Select(s => s.Name).Contains(b.Name))
            .ToListAsync(cancellationToken);

        // Subdomain slugs already in circulation (live + superseded) so seeded
        // addresses never collide with real businesses or captured redirects.
        var takenSlugs = new HashSet<string>(StringComparer.Ordinal);
        takenSlugs.UnionWith(await context.Db.Businesses
            .IgnoreQueryFilters()
            .Where(b => b.Slug != null)
            .Select(b => b.Slug!)
            .ToListAsync(cancellationToken));
        takenSlugs.UnionWith(await context.Db.BusinessSlugHistories
            .Select(h => h.Slug)
            .ToListAsync(cancellationToken));

        var created = 0;

        foreach (var def in context.Scenario.Businesses)
        {
            var owner = context.UsersByKey[def.OwnerUserKey];

            if (!existingByOwner.TryGetValue(owner.Id, out var business))
            {
                business = existingByNameLocation.FirstOrDefault(b => b.Name == def.Name && b.Location == def.Location);
            }

            if (business == null)
            {
                business = new Domain.Entities.Business
                {
                    Id = DeterministicSeed.GuidFor("business", def.Key),
                    CreatedAt = def.CreatedAt,
                };
                context.Db.Businesses.Add(business);
                created++;
            }

            business.Name = def.Name;
            business.Category = def.Category;
            business.Location = def.Location;
            business.PhoneNumber = def.PhoneNumber;
            business.Email = def.Email;
            business.Description = def.Description;
            business.LogoUrl = def.LogoUrl;
            business.MpesaNumber = def.MpesaNumber;
            business.OwnerId = owner.Id;

            // Subdomain address: only assign when missing — an owner-customized
            // slug is never clobbered by a reseed. Deterministic via the shared
            // policy (name → base → numbered suffix against takenSlugs).
            if (string.IsNullOrEmpty(business.Slug))
            {
                business.Slug = BusinessSlugPolicy.Next(
                    BusinessSlugPolicy.Slugify(def.Name),
                    candidate => takenSlugs.Contains(candidate));
            }
            takenSlugs.Add(business.Slug!);

            context.BusinessesByKey[def.Key] = business;
        }

        await context.Db.SaveChangesAsync(cancellationToken);
        context.Report.Counts["BusinessesCreated"] = created;

        // ── Subscriptions: every business needs one for module access ──
        var planRows = await context.Db.SubscriptionPlans
            .Include(p => p.PlanModules)
            .ToDictionaryAsync(p => p.Key, cancellationToken);

        var businessIds = context.BusinessesByKey.Values
            .Select(b => b.Id)
            .Distinct()
            .ToList();

        var existingSubBusinessIds = (await context.Db.BusinessSubscriptions
            .Where(s => businessIds.Contains(s.BusinessId))
            .Select(s => s.BusinessId)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var subsCreated = 0;
        // Deterministic plan mix so demo data exercises multiple tiers:
        // every 4th business → pro, every 5th → growth, otherwise starter.
        var planOrder = new[] { "starter", "growth", "pro" };
        var index = 0;
        foreach (var businessId in businessIds)
        {
            if (existingSubBusinessIds.Contains(businessId))
                continue;

            var planKey = index % 4 == 0 ? "pro" : index % 5 == 0 ? "growth" : planOrder[index % planOrder.Length];
            index++;

            if (!planRows.TryGetValue(planKey, out var plan))
                planKey = planRows.Keys.FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrEmpty(planKey) || !planRows.TryGetValue(planKey, out plan))
                continue;

            var now = DateTime.UtcNow;
            context.Db.BusinessSubscriptions.Add(new Domain.Entities.BusinessSubscription
            {
                Id = DeterministicSeed.GuidFor("business-subscription", businessId.ToString("N")),
                BusinessId = businessId,
                PlanId = plan.Id,
                Status = "active",
                StartsAt = now,
                EndsAt = now.AddMonths(1),
                CreatedAt = now
            });
            subsCreated++;
        }

        await context.Db.SaveChangesAsync(cancellationToken);
        context.Report.Counts["SubscriptionsCreated"] = subsCreated;
    }
}
