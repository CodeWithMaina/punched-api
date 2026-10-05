using PunchedApi.Application.Loyalty;
using PunchedApi.Domain.Entities;
using Xunit;

namespace PunchedApi.Tests;

/// <summary>
/// Boundary tests for the pure stamp-card rules policy — no database involved.
/// Every rule the loyalty read/write paths depend on is asserted at its edges
/// (0 / 1 / min / max / req-1 / req / req+1) so a regression in the single
/// source of effective rules is caught immediately (§15, §36).
/// </summary>
public class CardRulesPolicyTests
{
    // ── Entity builders ─────────────────────────────────────

    private static LoyaltyCard Card(int requiredStamps, Guid? stampCardId = null) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        BusinessId = Guid.NewGuid(),
        ProgramId = Guid.NewGuid(),
        RequiredStamps = requiredStamps,
        StampCardId = stampCardId,
        EnrolledAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow
    };

    private static LoyaltyProgram Program(int stampsRequired = 10, string reward = "Program reward") => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = Guid.NewGuid(),
        Name = "Rewards",
        StampsRequired = stampsRequired,
        RewardDescription = reward,
        CreatedAt = DateTime.UtcNow
    };

    private static StampCard Stamp(int stampsRequired = 10, string reward = "Card reward") => new()
    {
        Id = Guid.NewGuid(),
        ProgramId = Guid.NewGuid(),
        BusinessId = Guid.NewGuid(),
        Name = "Card",
        StampsRequired = stampsRequired,
        RewardDescription = reward,
        CreatedAt = DateTime.UtcNow
    };

    // ── IsValidRequiredStamps ───────────────────────────────

    [Theory]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(0, false)]
    [InlineData(101, false)]
    [InlineData(-1, false)]
    public void IsValidRequiredStamps_EnforcesOneToOneHundred(int value, bool expected) =>
        Assert.Equal(expected, CardRulesPolicy.IsValidRequiredStamps(value));

    // ── ResolveEffectiveRequiredStamps ──────────────────────

    [Fact]
    public void EffectiveStamps_EnrolmentSnapshotWins()
    {
        // Snapshot beats both the bound card and the program — an edit to either
        // must never reinterpret an existing customer's progress.
        var snapshot = Card(requiredStamps: 7, stampCardId: Guid.NewGuid());
        var effective = CardRulesPolicy.ResolveEffectiveRequiredStamps(
            snapshot, Program(stampsRequired: 10), Stamp(stampsRequired: 20));

        Assert.Equal(7, effective);
    }

    [Fact]
    public void EffectiveStamps_FallsBackToBoundCard_WhenSnapshotMissing()
    {
        // RequiredStamps = 0 is the "not snapshotted" legacy marker.
        var snapshot = Card(requiredStamps: 0, stampCardId: Guid.NewGuid());
        var effective = CardRulesPolicy.ResolveEffectiveRequiredStamps(
            snapshot, Program(stampsRequired: 10), Stamp(stampsRequired: 20));

        Assert.Equal(20, effective);
    }

    [Fact]
    public void EffectiveStamps_FallsBackToProgram_WhenNoCard()
    {
        var snapshot = Card(requiredStamps: 0, stampCardId: null);
        var effective = CardRulesPolicy.ResolveEffectiveRequiredStamps(
            snapshot, Program(stampsRequired: 10), stampCard: null);

        Assert.Equal(10, effective);
    }

    [Fact]
    public void EffectiveStamps_SkipsInvalidCardRequirement()
    {
        // Card requirement is invalid (0) → keep falling through to the program.
        var snapshot = Card(requiredStamps: 0, stampCardId: Guid.NewGuid());
        var effective = CardRulesPolicy.ResolveEffectiveRequiredStamps(
            snapshot, Program(stampsRequired: 10), Stamp(stampsRequired: 0));

        Assert.Equal(10, effective);
    }

    [Theory]
    [InlineData(0, 1)]      // below the floor is clamped up to the minimum
    [InlineData(-5, 1)]     // corrupt negative program value
    [InlineData(500, 100)]  // corrupt oversized program value
    public void EffectiveStamps_AlwaysClamped(int programRequired, int expected)
    {
        var effective = CardRulesPolicy.ResolveEffectiveRequiredStamps(
            Card(requiredStamps: 0), Program(stampsRequired: programRequired), stampCard: null);

        Assert.Equal(expected, effective);
    }

    // ── ResolveEffectiveRewardDescription ──────────────────

    [Fact]
    public void EffectiveReward_BoundCardWins_WhenCardHasReward()
    {
        var card = Card(requiredStamps: 10, stampCardId: Guid.NewGuid());
        var effective = CardRulesPolicy.ResolveEffectiveRewardDescription(
            card, Program(reward: "Program reward"), Stamp(reward: "Card reward"));

        Assert.Equal("Card reward", effective);
    }

    [Fact]
    public void EffectiveReward_UnboundCardUsesProgramReward()
    {
        var card = Card(requiredStamps: 10, stampCardId: null);
        var effective = CardRulesPolicy.ResolveEffectiveRewardDescription(
            card, Program(reward: "Program reward"), Stamp(reward: "Card reward"));

        Assert.Equal("Program reward", effective);
    }

    [Fact]
    public void EffectiveReward_EmptyCardRewardFallsBackToProgram()
    {
        var card = Card(requiredStamps: 10, stampCardId: Guid.NewGuid());
        var effective = CardRulesPolicy.ResolveEffectiveRewardDescription(
            card, Program(reward: "Program reward"), Stamp(reward: "   "));

        Assert.Equal("Program reward", effective);
    }

    [Fact]
    public void EffectiveReward_EmptyProgramFallsBackToCardReward()
    {
        var card = Card(requiredStamps: 10, stampCardId: Guid.NewGuid());
        var effective = CardRulesPolicy.ResolveEffectiveRewardDescription(
            card, Program(reward: ""), Stamp(reward: "Card reward"));

        Assert.Equal("Card reward", effective);
    }

    [Fact]
    public void EffectiveReward_NothingAvailable_ReturnsEmpty()
    {
        var effective = CardRulesPolicy.ResolveEffectiveRewardDescription(
            Card(requiredStamps: 10), Program(reward: "  "), Stamp(reward: ""));

        Assert.Equal(string.Empty, effective);
    }

    // ── ClampCompletedStamps / ClassifyProgress (req boundaries) ──

    [Theory]
    [InlineData(-5, 10, 0)]   // negatives never render as filled
    [InlineData(0, 10, 0)]    // empty card
    [InlineData(9, 10, 9)]    // req-1 → partial
    [InlineData(10, 10, 10)]  // req → exactly the goal
    [InlineData(15, 10, 10)]  // req+1 (overshoot) clamps to the goal
    public void ClampCompletedStamps_BoundsFilledSlots(int total, int required, int expected) =>
        Assert.Equal(expected, CardRulesPolicy.ClampCompletedStamps(total, required));

    [Theory]
    [InlineData(0, 10, CardRulesPolicy.ProgressState.Empty)]
    [InlineData(9, 10, CardRulesPolicy.ProgressState.Partial)]     // req-1
    [InlineData(10, 10, CardRulesPolicy.ProgressState.Complete)]   // req
    [InlineData(11, 10, CardRulesPolicy.ProgressState.Complete)]   // req+1
    [InlineData(1, 1, CardRulesPolicy.ProgressState.Complete)]     // minimum card completes at 1
    [InlineData(99, 100, CardRulesPolicy.ProgressState.Partial)]   // maximum card at req-1
    [InlineData(100, 100, CardRulesPolicy.ProgressState.Complete)] // maximum card at req
    public void ClassifyProgress_AtRequirementBoundaries(
        int total, int required, CardRulesPolicy.ProgressState expected) =>
        Assert.Equal(expected, CardRulesPolicy.ClassifyProgress(total, required));


    // ── DetectRulesChanges ─────────────────────────────────

    private static StampCard ExistingCard() => new()
    {
        Id = Guid.NewGuid(),
        ProgramId = Guid.NewGuid(),
        BusinessId = Guid.NewGuid(),
        Name = "Card",
        StampsRequired = 10,
        RewardDescription = "Free Coffee",
        RewardValue = 50,
        CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public void DetectRulesChanges_NoChange_IsRulesNeutral()
    {
        var changes = CardRulesPolicy.DetectRulesChanges(
            ExistingCard(), 10, "Free Coffee", 50m);

        Assert.Empty(changes);
    }

    [Fact]
    public void DetectRulesChanges_TrimsBeforeComparingRewardDescription()
    {
        var changes = CardRulesPolicy.DetectRulesChanges(
            ExistingCard(), 10, "  Free Coffee  ", 50m);

        Assert.Empty(changes);
    }

    [Fact]
    public void DetectRulesChanges_NullFields_AreIgnored()
    {
        var changes = CardRulesPolicy.DetectRulesChanges(
            ExistingCard(), null, null, null);

        Assert.Empty(changes);
    }

    [Fact]
    public void DetectRulesChanges_ReportsEachChangedFieldWithOldAndNewValues()
    {
        var changes = CardRulesPolicy.DetectRulesChanges(
            ExistingCard(), 20, "New reward", 75m);

        Assert.Equal(3, changes.Count);

        var stamps = Assert.Single(changes, c => c.Field == nameof(StampCard.StampsRequired));
        Assert.Equal("10", stamps.OldValue);
        Assert.Equal("20", stamps.NewValue);

        var reward = Assert.Single(changes, c => c.Field == nameof(StampCard.RewardDescription));
        Assert.Equal("Free Coffee", reward.OldValue);
        Assert.Equal("New reward", reward.NewValue);

        var value = Assert.Single(changes, c => c.Field == nameof(StampCard.RewardValue));
        Assert.Equal("50", value.OldValue);
        Assert.Equal("75", value.NewValue);
    }

    // ── CanApplyToExistingCards ────────────────────────────

    [Theory]
    [InlineData(true, "why", true)]
    [InlineData(true, "   ", false)]  // flag without a usable reason → refused
    [InlineData(true, null, false)]
    [InlineData(false, "why", false)] // reason without the flag → refused
    public void CanApplyToExistingCards_RequiresFlagAndReason(
        bool requested, string? reason, bool expected) =>
        Assert.Equal(expected, CardRulesPolicy.CanApplyToExistingCards(requested, reason));

    // ── Lifecycle matrix ───────────────────────────────────

    [Theory]
    [InlineData(StampCardStatus.Draft, StampCardStatus.Active, true)]
    [InlineData(StampCardStatus.Draft, StampCardStatus.Inactive, true)]
    [InlineData(StampCardStatus.Draft, StampCardStatus.Archived, true)]
    [InlineData(StampCardStatus.Active, StampCardStatus.Inactive, true)]
    [InlineData(StampCardStatus.Active, StampCardStatus.Archived, true)]
    [InlineData(StampCardStatus.Inactive, StampCardStatus.Active, true)]
    [InlineData(StampCardStatus.Inactive, StampCardStatus.Archived, true)]
    [InlineData(StampCardStatus.Active, StampCardStatus.Draft, false)]    // never un-launch
    [InlineData(StampCardStatus.Inactive, StampCardStatus.Draft, false)]
    [InlineData(StampCardStatus.Archived, StampCardStatus.Active, false)] // archived is terminal
    [InlineData(StampCardStatus.Archived, StampCardStatus.Draft, false)]
    public void CanTransition_EnforcesLifecycleMatrix(
        StampCardStatus from, StampCardStatus to, bool expected) =>
        Assert.Equal(expected, CardRulesPolicy.CanTransition(from, to));

    [Fact]
    public void CanTransition_SameStateIsAlwaysANoOp() =>
        Assert.True(CardRulesPolicy.CanTransition(StampCardStatus.Archived, StampCardStatus.Archived));

    [Theory]
    [InlineData(StampCardStatus.Active, true)]
    [InlineData(StampCardStatus.Draft, false)]
    [InlineData(StampCardStatus.Inactive, false)]
    [InlineData(StampCardStatus.Archived, false)]
    public void IsEnrollable_OnlyActiveCardsAreCustomerFacing(StampCardStatus status, bool expected) =>
        Assert.Equal(expected, CardRulesPolicy.IsEnrollable(status));

    [Theory]
    [InlineData(StampCardStatus.Inactive, true)]
    [InlineData(StampCardStatus.Archived, true)]
    [InlineData(StampCardStatus.Draft, false)]
    [InlineData(StampCardStatus.Active, false)]
    public void CanHardDelete_RefusesLiveCards(StampCardStatus status, bool expected) =>
        Assert.Equal(expected, CardRulesPolicy.CanHardDelete(status));
}

