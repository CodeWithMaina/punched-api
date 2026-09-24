using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Loyalty;

/// <summary>
/// Pure, side-effect-free policy for the stamp-card domain. This is the single
/// place the *business rules* are expressed, so they can be unit-tested at their
/// boundaries without a database, and so the presentation layer never has to
/// re-derive them.
///
/// The central rule this type enforces:
/// <code>
/// required stamps to complete a card = business logic (owned by StampCard)
/// how the card looks                  = presentation (owned by CardDesign)
/// </code>
/// Presentation must never be an input to any method here.
/// </summary>
public static class CardRulesPolicy
{
    /// <summary>Smallest legal required-stamp count for a stamp card.</summary>
    public const int MinRequiredStamps = 1;

    /// <summary>Largest legal required-stamp count for a stamp card.</summary>
    public const int MaxRequiredStamps = 100;

    /// <summary>True when <paramref name="value"/> is a legal required-stamp count.</summary>
    public static bool IsValidRequiredStamps(int value) =>
        value is >= MinRequiredStamps and <= MaxRequiredStamps;

    /// <summary>
    /// A customer's effective required-stamp count for the current cycle.
    ///
    /// Resolution order (never trusts client input):
    /// <list type="number">
    /// <item>The enrolment's own snapshot (<see cref="LoyaltyCard.RequiredStamps"/>),
    /// which freezes the rule the customer enrolled under.</item>
    /// <item>The bound stamp card's current requirement (for rows snapshotted
    /// before card binding existed).</item>
    /// <item>The parent program's scalar requirement (legacy programs).</item>
    /// </list>
    /// The result is always clamped into [<see cref="MinRequiredStamps"/>,
    /// <see cref="MaxRequiredStamps"/>] so a corrupt column can never produce a
    /// nonsensical goal such as 0 or a negative value.
    /// </summary>
    public static int ResolveEffectiveRequiredStamps(
        LoyaltyCard card, LoyaltyProgram program, StampCard? stampCard)
    {
        var candidate = card.RequiredStamps;
        if (!IsValidRequiredStamps(candidate) && stampCard != null)
            candidate = stampCard.StampsRequired;
        if (!IsValidRequiredStamps(candidate))
            candidate = program.StampsRequired;

        return Math.Clamp(candidate, MinRequiredStamps, MaxRequiredStamps);
    }

    /// <summary>
    /// The reward description a customer sees for their current cycle, resolved
    /// the same way as <see cref="ResolveEffectiveRequiredStamps"/> so the goal
    /// and the prize can never disagree.
    /// </summary>
    public static string ResolveEffectiveRewardDescription(
        LoyaltyCard card, LoyaltyProgram program, StampCard? stampCard)
    {
        if (card.StampCardId.HasValue && stampCard != null
            && !string.IsNullOrWhiteSpace(stampCard.RewardDescription))
            return stampCard.RewardDescription;

        if (!string.IsNullOrWhiteSpace(program.RewardDescription))
            return program.RewardDescription;

        return stampCard?.RewardDescription ?? string.Empty;
    }

    /// <summary>
    /// Number of stamps actually presented as filled on a card face.
    ///
    /// Deliberately clamped: the ledger path intentionally allows a credit to
    /// overshoot the requirement (stamps are only consumed at redemption), so
    /// <see cref="LoyaltyCard.TotalStamps"/> may legitimately exceed the goal.
    /// Presentation must never render more slots than the card has.
    /// </summary>
    public static int ClampCompletedStamps(int totalStamps, int effectiveRequired) =>
        Math.Clamp(totalStamps, 0, Math.Max(MinRequiredStamps, effectiveRequired));

    /// <summary>Stamp card lifecycle state of a progress snapshot.</summary>
    public enum ProgressState
    {
        /// <summary>No stamps earned yet in this cycle.</summary>
        Empty = 0,

        /// <summary>At least one stamp, fewer than the requirement.</summary>
        Partial = 1,

        /// <summary>Requirement reached — a reward is (or will be) available.</summary>
        Complete = 2
    }

    /// <summary>
    /// Classifies progress into the three states the card designer previews and
    /// the customer UI renders. Pure function of (earned, required).
    /// </summary>
    public static ProgressState ClassifyProgress(int totalStamps, int effectiveRequired)
    {
        var clamped = ClampCompletedStamps(totalStamps, effectiveRequired);
        if (clamped <= 0) return ProgressState.Empty;
        return clamped >= effectiveRequired ? ProgressState.Complete : ProgressState.Partial;
    }

    /// <summary>
    /// A single change detected between the persisted business rules and an
    /// incoming update. Used both for validation and for the audit row.
    /// </summary>
    public sealed record RulesChange(string Field, string? OldValue, string? NewValue);

    /// <summary>
    /// Detects which business rules an incoming update actually changes.
    /// An empty result means the request is rules-neutral (typically a
    /// presentation-only edit), so it must not bump the rules version or write an
    /// audit row.
    /// </summary>
    public static IReadOnlyList<RulesChange> DetectRulesChanges(
        StampCard existing,
        int? stampsRequired,
        string? rewardDescription,
        decimal? rewardValue)
    {
        var changes = new List<RulesChange>();

        if (stampsRequired.HasValue && stampsRequired.Value != existing.StampsRequired)
            changes.Add(new RulesChange(
                nameof(StampCard.StampsRequired),
                existing.StampsRequired.ToString(),
                stampsRequired.Value.ToString()));

        if (rewardDescription != null)
        {
            var trimmed = rewardDescription.Trim();
            if (!string.Equals(trimmed, existing.RewardDescription, StringComparison.Ordinal))
                changes.Add(new RulesChange(
                    nameof(StampCard.RewardDescription), existing.RewardDescription, trimmed));
        }

        if (rewardValue.HasValue && rewardValue.Value != existing.RewardValue)
            changes.Add(new RulesChange(
                nameof(StampCard.RewardValue),
                existing.RewardValue.ToString(),
                rewardValue.Value.ToString()));

        return changes;
    }

    /// <summary>
    /// Whether a rules change is allowed to touch existing in-flight enrolments.
    ///
    /// A caller may only push a change onto existing cards by explicitly opting in
    /// *and* supplying a reason, so "silently reinterpreted my progress" is
    /// impossible: an unaudited bulk mutation cannot be expressed.
    /// </summary>
    public static bool CanApplyToExistingCards(bool applyToExistingRequested, string? reason) =>
        applyToExistingRequested && !string.IsNullOrWhiteSpace(reason);

    /// <summary>
    /// Valid stamp-card lifecycle transitions. Enforced in the service so an
    /// invalid transition can never be persisted.
    ///
    /// <c>Active → Inactive → Active</c> is reversible (a temporary pause).
    /// <c>Archived</c> is terminal: an archived card is a historical record that
    /// must not be accidentally reused.
    /// </summary>
    public static bool CanTransition(StampCardStatus from, StampCardStatus to)
    {
        if (from == to) return true;
        if (from == StampCardStatus.Archived) return false;

        return (from, to) switch
        {
            (StampCardStatus.Draft, StampCardStatus.Active) => true,
            (StampCardStatus.Draft, StampCardStatus.Inactive) => true,
            (StampCardStatus.Draft, StampCardStatus.Archived) => true,
            (StampCardStatus.Active, StampCardStatus.Inactive) => true,
            (StampCardStatus.Active, StampCardStatus.Archived) => true,
            (StampCardStatus.Inactive, StampCardStatus.Active) => true,
            (StampCardStatus.Inactive, StampCardStatus.Archived) => true,
            _ => false
        };
    }

    /// <summary>
    /// True when a card in this state may be selected for a brand-new enrolment.
    /// Only <see cref="StampCardStatus.Active"/> cards are customer-facing.
    /// </summary>
    public static bool IsEnrollable(StampCardStatus status) => status == StampCardStatus.Active;

    /// <summary>
    /// True when a card in this state may be hard-deleted. Live cards (Draft is
    /// still being configured, Active is customer-facing) are refused; archived
    /// and inactive cards carry no customer-facing surface.
    /// </summary>
    public static bool CanHardDelete(StampCardStatus status) =>
        status is StampCardStatus.Inactive or StampCardStatus.Archived;
}
