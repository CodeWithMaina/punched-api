using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Loyalty;

/// <summary>
/// Captures a customer's loyalty *rules snapshot* at the moment an enrollment is
/// created (or explicitly re-bound before any progress exists).
///
/// Why this exists (§36, no silent data mutation): a <see cref="LoyaltyCard"/>'s
/// <see cref="LoyaltyCard.RequiredStamps"/> and <see cref="LoyaltyCard.RulesVersion"/>
/// freeze the business rule the customer joined under. Editing the
/// <see cref="StampCard.StampsRequired"/> afterwards never rewrites existing
/// enrollments — only an explicit, audited
/// <c>StampCardRulesChange</c> with a reason may do that.
///
/// Binding rule: when the program has exactly ONE active stamp card it becomes
/// the enrollment's bound card (deterministic; ties/order decided by CreatedAt).
/// When a program has zero or several active cards the enrollment stays on the
/// program-level defaults (<see cref="LoyaltyCard.StampCardId"/> null,
/// RulesVersion 0), which is the legacy behaviour and always safe.
/// </summary>
public static class LoyaltyCardSnapshot
{
    /// <summary>
    /// The program's single active stamp card, or null when the program has zero
    /// or several active cards (ambiguous — do not bind).
    /// </summary>
    public static async Task<StampCard?> ResolveDefaultStampCardAsync(IUnitOfWork unitOfWork, Guid programId)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        var active = (await unitOfWork.StampCards.FindAsync(
                c => c.ProgramId == programId && c.Status == StampCardStatus.Active))
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToList();

        return active.Count == 1 ? active[0] : null;
    }

    /// <summary>
    /// Writes the rules snapshot onto a (fresh) loyalty card. Never called for
    /// cards that already carry a snapshot — the server does not rewrite
    /// established progress outside an audited rules change.
    /// </summary>
    public static void Apply(LoyaltyCard card, LoyaltyProgram program, StampCard? stampCard, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(program);

        card.StampCardId = stampCard?.Id;
        card.RequiredStamps =
            stampCard != null && CardRulesPolicy.IsValidRequiredStamps(stampCard.StampsRequired)
                ? stampCard.StampsRequired
                : Math.Clamp(
                    program.StampsRequired,
                    CardRulesPolicy.MinRequiredStamps,
                    CardRulesPolicy.MaxRequiredStamps);
        card.RulesVersion = stampCard?.RulesVersion ?? 0;
    }
}