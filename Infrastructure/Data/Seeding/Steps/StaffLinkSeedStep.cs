using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Seeding.Steps;

/// <summary>
/// Seeds staff relationships. Staff membership is a row in
/// <c>business_memberships</c> — the same table the invitation and provisioning
/// flows write — so seeded tenants behave exactly like production ones.
///
/// Idempotent: a reseed never duplicates a row (the table has a unique
/// <c>(user_id, business_id)</c> index) and re-activates a suspended one.
/// </summary>
public sealed class StaffLinkSeedStep : ISeedStep
{
    public string Name => "StaffLink";

    public async Task ExecuteAsync(SeedExecutionContext context, CancellationToken cancellationToken)
    {
        var businessIds = context.Scenario.Businesses
            .Select(b => context.BusinessesByKey[b.Key].Id)
            .Distinct()
            .ToList();

        var existing = await context.Db.BusinessMemberships
            .Where(m => businessIds.Contains(m.BusinessId) &&
                        m.Role == BusinessMembershipRole.Staff)
            .ToListAsync(cancellationToken);

        foreach (var business in context.Scenario.Businesses)
        {
            var businessEntity = context.BusinessesByKey[business.Key];
            foreach (var staffKey in business.StaffUserKeys)
            {
                var staff = context.UsersByKey[staffKey];
                var membership = existing.FirstOrDefault(
                    m => m.BusinessId == businessEntity.Id && m.UserId == staff.Id);

                if (membership == null)
                {
                    await context.Db.BusinessMemberships.AddAsync(new BusinessMembership
                    {
                        Id = Guid.NewGuid(),
                        UserId = staff.Id,
                        BusinessId = businessEntity.Id,
                        Role = BusinessMembershipRole.Staff,
                        Status = BusinessMembershipStatus.Active,
                        JoinedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        CreatedAt = DateTime.UtcNow
                    }, cancellationToken);
                    continue;
                }

                // Re-running the seed restores an active membership without
                // creating a second row for the same person and tenant.
                if (membership.Status == BusinessMembershipStatus.Active) continue;
                membership.Status = BusinessMembershipStatus.Active;
                membership.EndedAt = null;
                membership.UpdatedAt = DateTime.UtcNow;
            }
        }

        await context.Db.SaveChangesAsync(cancellationToken);

        // Counted from the canonical table (not User.Role): one row per staff
        // membership, which is what authorization actually reads.
        context.Report.Counts["StaffLinked"] = await context.Db.BusinessMemberships
            .CountAsync(m => businessIds.Contains(m.BusinessId) &&
                             m.Role == BusinessMembershipRole.Staff &&
                             m.Status == BusinessMembershipStatus.Active,
                        cancellationToken);
    }
}
