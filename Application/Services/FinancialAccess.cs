using Microsoft.EntityFrameworkCore;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Tenant and identity helpers shared by every financial service.
///
/// <para><b>The rule.</b> A caller's business is resolved from their ACTIVE membership, never
/// from a request field, route value or claim. Every financial query then filters on that
/// resolved id, so a body carrying another business's <c>businessId</c> is simply ignored —
/// which is what stops one business reading or writing another's books.</para>
/// </summary>
internal static class FinancialAccess
{
    /// <summary>
    /// The caller's business scope. Owners win over staff (a user who is both acts as owner),
    /// and an inactive membership grants nothing.
    /// </summary>
    public static async Task<Guid?> ResolveBusinessIdAsync(ApplicationDbContext context, Guid userId)
    {
        return await context.BusinessMemberships.AsNoTracking()
            .Where(m => m.UserId == userId
                        && m.Status == BusinessMembershipStatus.Active
                        && (m.Role == BusinessMembershipRole.Owner || m.Role == BusinessMembershipRole.Staff)
                        && !m.Business.IsDeleted)
            .OrderBy(m => m.Role == BusinessMembershipRole.Owner ? 0 : 1)
            .Select(m => (Guid?)m.BusinessId)
            .FirstOrDefaultAsync();
    }

    /// <summary>The actor's display name, for audit entries.</summary>
    public static async Task<string?> ActorNameAsync(ApplicationDbContext context, Guid? userId)
    {
        if (userId is not Guid id) return null;

        return await context.Users.AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => u.FullName)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Display names for a set of actors, resolved in ONE query.
    ///
    /// <para>The ledger stores the id of whoever recorded a row, not their name (a rename must not
    /// rewrite history). A register that shows "Recorded by" therefore has to look the names up —
    /// and it must do so once per page, never once per row, or a 100-row cashbook becomes 100
    /// round trips.</para>
    /// </summary>
    public static async Task<Dictionary<Guid, string>> ResolveUserNamesAsync(
        ApplicationDbContext context, IEnumerable<Guid?> userIds)
    {
        var ids = userIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();

        var names = await context.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new UserIdNameRow(u.Id, u.FullName))
            .ToListAsync();

        return names
            .Where(n => !string.IsNullOrWhiteSpace(n.FullName))
            .GroupBy(n => n.Id)
            .ToDictionary(g => g.Key, g => g.First().FullName!);
    }

    /// <summary>
    /// The business's IANA timezone, or UTC when the business has none (or the id is not a
    /// zone this machine knows — a missing tz database must not stop the books opening).
    /// </summary>
    public static async Task<TimeZoneInfo> ResolveTimeZoneAsync(ApplicationDbContext context, Guid businessId)
    {
        var id = await context.Businesses.AsNoTracking()
            .Where(b => b.Id == businessId)
            .Select(b => b.TimeZoneId)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Tenant-confirms an appointment before financial code touches it.</summary>
    public static async Task<bool> AppointmentBelongsToAsync(
        ApplicationDbContext context, Guid appointmentId, Guid businessId) =>
        await context.Appointments.AsNoTracking()
            .AnyAsync(a => a.Id == appointmentId && a.BusinessId == businessId);

    /// <summary>Tenant-confirms a customer (a Users row enrolled with this business).</summary>
    public static async Task<bool> CustomerBelongsToAsync(
        ApplicationDbContext context, Guid customerId, Guid businessId) =>
        await context.CustomerBusinessEnrollments.AsNoTracking()
            .AnyAsync(e => e.CustomerId == customerId
                           && e.BusinessId == businessId
                           && e.Status == CustomerBusinessEnrollmentStatus.Active);
}
