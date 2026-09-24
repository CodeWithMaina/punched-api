using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Notifications;

/// <summary>
/// Pure preference decision, order: business kill-switch → user business-scoped
/// row → user global row → system default (architecture doc §7, R1–R6).
/// </summary>
public interface IPreferenceResolver
{
    /// <summary>The candidate channels that survive preferences for one (user, business, category).</summary>
    Task<IReadOnlyList<string>> ResolveAsync(
        Guid userId,
        Guid? businessId,
        string category,
        IEnumerable<string> candidates,
        CancellationToken ct = default);

    /// <summary>
    /// The RESOLVED view the client renders: every (category × channel) pair with
    /// its effective value, whether the platform currently ships that channel, and
    /// which level decided it. The client never re-implements R1–R6.
    /// </summary>
    Task<IReadOnlyList<ResolvedPreferenceDto>> GetResolvedAsync(
        Guid userId,
        Guid? businessId,
        CancellationToken ct = default);

    /// <summary>
    /// Upserts user-scope override rows. Rows equal to the system default are
    /// deleted so the table stays sparse (absence *is* "inherit"), and the
    /// caller's cache entry is evicted (R6).
    /// </summary>
    /// <returns>The number of added/updated/removed rows.</returns>
    Task<int> UpsertAsync(
        Guid userId,
        Guid? businessId,
        IReadOnlyList<NotificationPreferenceItem> preferences,
        CancellationToken ct = default);

    /// <summary>Evicts the cached overrides for one (user, business) pair (R6).</summary>
    void Invalidate(Guid userId, Guid? businessId);
}
