using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Seeding.Demo.AureliaLuxe;

/// <summary>
/// Resolves the existing "Aurelia Luxe Hair Atelier" tenant and its seeded
/// identities. The demo seeder never creates the business: it adopts whatever
/// row already carries the atelier's slug, so ownership and multi-tenant
/// relationships are preserved exactly as the platform created them.
/// </summary>
internal static class AureliaLuxeDemoResolver
{
    /// <summary>
    /// The business row for the atelier, or null when it does not exist yet
    /// (in which case the caller logs a warning and skips — the demo seeder
    /// never fabricates a second business with the same name).
    /// </summary>
    public static async Task<Business?> ResolveBusinessAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var business = await db.Businesses
            .FirstOrDefaultAsync(b => b.Slug == AureliaLuxeDemoData.Slug, cancellationToken);

        business ??= await db.Businesses
            .FirstOrDefaultAsync(b => b.Name == AureliaLuxeDemoData.Name, cancellationToken);

        return business;
    }

    /// <summary>
    /// Builds the resolved handle set (owner, staff, customers) for the tenant.
    /// Users are taken from the in-flight seed context when the standard steps
    /// already produced them, and otherwise loaded from the database by email —
    /// so the demo steps work standalone as well as in the normal pipeline.
    /// </summary>
    public static async Task<AureliaLuxeDemoContext?> ResolveContextAsync(
        SeedExecutionContext context,
        Business business,
        CancellationToken cancellationToken)
    {
        var db = context.Db;
        var scenario = SeedCatalog.BuildScenario(1);
        var definition = scenario.Businesses.Single(b => b.Key == AureliaLuxeDemoData.BusinessKey);

        var owner = await ResolveUserAsync(db, context, definition.OwnerUserKey, cancellationToken);
        if (owner is null)
        {
            return null;
        }

        var staff = new List<AureliaLuxeStaffHandle>();
        foreach (var staffDefinition in AureliaLuxeDemoData.Staff)
        {
            var user = await ResolveUserAsync(db, context, staffDefinition.UserKey, cancellationToken);
            if (user is not null)
            {
                staff.Add(new AureliaLuxeStaffHandle(staffDefinition.UserKey, user, staffDefinition));
            }
        }

        var customers = new List<AureliaLuxeCustomerHandle>();
        foreach (var key in definition.CustomerUserKeys)
        {
            var user = await ResolveUserAsync(db, context, key, cancellationToken);
            if (user is not null)
            {
                customers.Add(new AureliaLuxeCustomerHandle(key, user));
            }
        }

        if (staff.Count == 0 || customers.Count == 0)
        {
            return null;
        }

        return new AureliaLuxeDemoContext
        {
            Business = business,
            Owner = owner,
            Staff = staff,
            Customers = customers,
        };
    }

    private static async Task<User?> ResolveUserAsync(
        ApplicationDbContext db,
        SeedExecutionContext context,
        string userKey,
        CancellationToken cancellationToken)
    {
        if (context.UsersByKey.TryGetValue(userKey, out var fromContext))
        {
            return fromContext;
        }

        var seedUser = SeedCatalog.Users.FirstOrDefault(u => u.Key == userKey);
        if (seedUser is null)
        {
            return null;
        }

        var email = seedUser.Email.Trim().ToLowerInvariant();
        return await db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
    }

    /// <summary>
    /// Ensures the identity has a usable development credential. The password is
    /// only (re)written when the stored hash does not already verify against the
    /// documented demo password, so an unchanged demo account is never touched.
    /// </summary>
    public static async Task<bool> EnsureCredentialAsync(
        ApplicationDbContext db,
        User user,
        string demoPassword,
        DateTime? lastLoginAt,
        CancellationToken cancellationToken)
    {
        var email = user.Email.Trim().ToLowerInvariant();
        var auth = await db.UserAuths.FirstOrDefaultAsync(a => a.Email == email, cancellationToken);

        var changed = false;
        if (auth is null)
        {
            auth = new UserAuth
            {
                Id = DeterministicSeed.GuidFor("user-auth", email),
                Email = email,
                PasswordHash = AureliaLuxeDemoIds.HashPassword(demoPassword),
                IsVerified = true,
                CreatedAt = user.CreatedAt,
            };
            db.UserAuths.Add(auth);
            changed = true;
        }
        else
        {
            var matches = false;
            try
            {
                matches = BCrypt.Net.BCrypt.Verify(demoPassword, auth.PasswordHash);
            }
            catch (Exception)
            {
                matches = false;
            }

            if (!matches)
            {
                auth.PasswordHash = AureliaLuxeDemoIds.HashPassword(demoPassword);
                changed = true;
            }
        }

        // A seeded demo account must be able to sign in immediately: verified,
        // unlocked, with no pending OTP and no accumulated failure counters.
        if (!auth.IsVerified)
        {
            auth.IsVerified = true;
            changed = true;
        }

        auth.VerificationCode = null;
        auth.VerificationCodeExpiresAt = null;
        auth.VerificationCodeAttempts = 0;
        auth.FailedLoginAttempts = 0;
        auth.LockedUntil = null;
        auth.LastLoginAt ??= lastLoginAt;

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }
}
