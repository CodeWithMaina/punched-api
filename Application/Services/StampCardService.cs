using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Loyalty;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

/// <summary>
/// Business-facing management of stamp cards (children of loyalty programs).
///
/// Card design *authoring* lives in <see cref="CardDesignService"/> (Admin only);
/// this service only validates that a business may assign an available design to
/// one of its stamp cards.
/// </summary>
public class StampCardService : IStampCardService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardDesignService _cardDesignService;
    private readonly ILogger<StampCardService> _logger;

    public StampCardService(
        IUnitOfWork unitOfWork,
        ICardDesignService cardDesignService,
        ILogger<StampCardService> logger)
    {
        _unitOfWork = unitOfWork;
        _cardDesignService = cardDesignService;
        _logger = logger;
    }

    // ── Helpers ─────────────────────────────────────────────

    private async Task<Business?> ResolveBusinessAsync(Guid ownerId) =>
        await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.OwnerId == ownerId);

    private static StampCardResponse MapCard(StampCard card, int enrolledCustomers = 0) => new()
    {
        Id = card.Id,
        ProgramId = card.ProgramId,
        BusinessId = card.BusinessId,
        Name = card.Name,
        Description = card.Description,
        StampsRequired = card.StampsRequired,
        RewardDescription = card.RewardDescription,
        RewardValue = card.RewardValue,
        Status = MapStatus(card.Status),
        CardDesignId = card.CardDesignId,
        CardDesignName = card.CardDesign?.Name,
        RulesVersion = card.RulesVersion,
        EnrolledCustomers = enrolledCustomers,
        CreatedAt = card.CreatedAt
    };

    private static string MapStatus(StampCardStatus status) => status switch
    {
        StampCardStatus.Active => "active",
        StampCardStatus.Inactive => "inactive",
        StampCardStatus.Archived => "archived",
        _ => "draft"
    };

    private static StampCardStatus ParseStatus(string status) => status switch
    {
        "active" => StampCardStatus.Active,
        "inactive" => StampCardStatus.Inactive,
        "archived" => StampCardStatus.Archived,
        _ => StampCardStatus.Draft
    };

    /// <summary>
    /// Strict status parsing. Unknown values are rejected instead of being
    /// silently coerced to Draft — a typo must never change a card's lifecycle.
    /// </summary>
    private static bool TryParseStatus(string? status, out StampCardStatus parsed)
    {
        switch (status?.Trim().ToLowerInvariant())
        {
            case "draft": parsed = StampCardStatus.Draft; return true;
            case "active": parsed = StampCardStatus.Active; return true;
            case "inactive": parsed = StampCardStatus.Inactive; return true;
            case "archived": parsed = StampCardStatus.Archived; return true;
            default: parsed = default; return false;
        }
    }

    /// <summary>
    /// Server-side validation of the business-rule fields. Defence in depth on
    /// top of the DTO's [Range] attributes — a direct service call (or a future
    /// caller) hits the same boundary. Never derived from UI input.
    /// </summary>
    private static ApiResponse<T>? ValidateRules<T>(
        int? stampsRequired, string? name, string? rewardDescription, decimal? rewardValue)
    {
        if (stampsRequired.HasValue && !CardRulesPolicy.IsValidRequiredStamps(stampsRequired.Value))
            return ApiResponse<T>.Fail(
                "INVALID_STAMPS_REQUIRED",
                $"Stamps required must be between {CardRulesPolicy.MinRequiredStamps} and {CardRulesPolicy.MaxRequiredStamps}.");

        if (name != null && string.IsNullOrWhiteSpace(name))
            return ApiResponse<T>.Fail("INVALID_NAME", "Name must not be empty.");

        if (rewardDescription != null && string.IsNullOrWhiteSpace(rewardDescription))
            return ApiResponse<T>.Fail("INVALID_REWARD", "Reward description must not be empty.");

        if (rewardValue.HasValue && rewardValue.Value < 0)
            return ApiResponse<T>.Fail("INVALID_REWARD_VALUE", "Reward value cannot be negative.");

        return null;
    }

    // ── Stamp cards ─────────────────────────────────────────

    public async Task<ApiResponse<List<StampCardResponse>>> GetProgramStampCardsAsync(Guid ownerId, Guid programId)
    {
        var business = await ResolveBusinessAsync(ownerId);
        if (business == null)
            return ApiResponse<List<StampCardResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        var program = await _unitOfWork.LoyaltyPrograms
            .FirstOrDefaultAsync(p => p.Id == programId && p.BusinessId == business.Id);
        if (program == null)
            return ApiResponse<List<StampCardResponse>>.Fail("NOT_FOUND", "Loyalty program not found.");

        var cards = await _unitOfWork.StampCards.FindAsync(c => c.ProgramId == programId);
        var enrolled = await _unitOfWork.LoyaltyCards.CountAsync(lc => lc.ProgramId == programId);

        var result = cards.OrderBy(c => c.CreatedAt).Select(c =>
        {
            var response = MapCard(c);
            response.EnrolledCustomers = enrolled;
            return response;
        }).ToList();

        return ApiResponse<List<StampCardResponse>>.Ok(result);
    }

    public async Task<ApiResponse<StampCardResponse>> GetStampCardAsync(Guid ownerId, Guid stampCardId)
    {
        var business = await ResolveBusinessAsync(ownerId);
        if (business == null)
            return ApiResponse<StampCardResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var card = await _unitOfWork.StampCards.FirstOrDefaultAsync(c => c.Id == stampCardId && c.BusinessId == business.Id);
        if (card == null)
            return ApiResponse<StampCardResponse>.Fail("NOT_FOUND", "Stamp card not found.");

        var enrolled = await _unitOfWork.LoyaltyCards.CountAsync(lc => lc.ProgramId == card.ProgramId);
        return ApiResponse<StampCardResponse>.Ok(MapCard(card, enrolled));
    }

    public async Task<ApiResponse<StampCardResponse>> CreateStampCardAsync(Guid ownerId, Guid programId, CreateStampCardRequest request)
    {
        try
        {
            var business = await ResolveBusinessAsync(ownerId);
            if (business == null)
                return ApiResponse<StampCardResponse>.Fail("NOT_FOUND", "No business found for this account.");

            var program = await _unitOfWork.LoyaltyPrograms
                .FirstOrDefaultAsync(p => p.Id == programId && p.BusinessId == business.Id);
            if (program == null)
                return ApiResponse<StampCardResponse>.Fail("NOT_FOUND", "Loyalty program not found.");

            var rulesError = ValidateRules<StampCardResponse>(
                request.StampsRequired, request.Name, request.RewardDescription, request.RewardValue);
            if (rulesError != null) return rulesError;

            if (!string.IsNullOrWhiteSpace(request.Status) && !TryParseStatus(request.Status, out _))
                return ApiResponse<StampCardResponse>.Fail(
                    "INVALID_STATUS", "status must be one of: draft, active, inactive.");

            if (request.CardDesignId.HasValue)
            {
                // Entitlement + tenant isolation are enforced in one place.
                var check = await _cardDesignService.ValidateSelectionAsync(business.Id, request.CardDesignId);
                if (!check.IsValid)
                    return ApiResponse<StampCardResponse>.Fail(check.ErrorCode!, check.ErrorMessage!);
            }

            var card = new StampCard
            {
                Id = Guid.NewGuid(),
                ProgramId = program.Id,
                BusinessId = business.Id,
                Name = request.Name.Trim(),
                Description = request.Description?.Trim(),
                StampsRequired = request.StampsRequired,
                RewardDescription = request.RewardDescription.Trim(),
                RewardValue = request.RewardValue,
                Status = string.IsNullOrWhiteSpace(request.Status)
                    ? StampCardStatus.Draft
                    : ParseStatus(request.Status),
                CardDesignId = request.CardDesignId,
                RulesVersion = 1,
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.StampCards.AddAsync(card);
            await _unitOfWork.SaveChangesAsync();

            _logger.LogInformation(
                "LOYALTY_CARD_CREATED CardId={CardId} ProgramId={ProgramId} BusinessId={BusinessId} " +
                "StampsRequired={StampsRequired} RulesVersion={RulesVersion} Actor={ActorId}",
                card.Id, card.ProgramId, business.Id, card.StampsRequired, card.RulesVersion, ownerId);

            return ApiResponse<StampCardResponse>.Ok(MapCard(card));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating stamp card for program {ProgramId}", programId);
            return ApiResponse<StampCardResponse>.Fail("CREATE_FAILED", "Failed to create the stamp card.");
        }
    }

    public async Task<ApiResponse<StampCardResponse>> UpdateStampCardAsync(Guid ownerId, Guid stampCardId, UpdateStampCardRequest request)
    {
        try
        {
            var business = await ResolveBusinessAsync(ownerId);
            if (business == null)
                return ApiResponse<StampCardResponse>.Fail("NOT_FOUND", "No business found for this account.");

            var card = await _unitOfWork.StampCards.FirstOrDefaultAsync(c => c.Id == stampCardId && c.BusinessId == business.Id);
            if (card == null)
                return ApiResponse<StampCardResponse>.Fail("NOT_FOUND", "Stamp card not found.");

            if (card.Status == StampCardStatus.Archived)
                return ApiResponse<StampCardResponse>.Fail("ARCHIVED", "Archived stamp cards cannot be edited. Restore it first.");

            var rulesError = ValidateRules<StampCardResponse>(
                request.StampsRequired, request.Name, request.RewardDescription, request.RewardValue);
            if (rulesError != null) return rulesError;

            // Applying a rules change to existing customers requires an explicit,
            // audited opt-in: the flag AND a reason. One without the other is an
            // error, never a silent downgrade to "ignored".
            if (request.ApplyToExistingCards && string.IsNullOrWhiteSpace(request.Reason))
                return ApiResponse<StampCardResponse>.Fail(
                    "REASON_REQUIRED",
                    "Applying a rules change to existing cards requires a reason.");

            // ── Presentation (design) assignment ────────────────────────────
            // Changing the design never touches loyalty state (§36): it is
            // applied to this row only and produces no rules audit entry.
            if (request.CardDesignId.HasValue)
            {
                // Entitlement + tenant isolation are enforced in one place.
                var check = await _cardDesignService.ValidateSelectionAsync(business.Id, request.CardDesignId);
                if (!check.IsValid)
                    return ApiResponse<StampCardResponse>.Fail(check.ErrorCode!, check.ErrorMessage!);
                card.CardDesignId = request.CardDesignId.Value;
            }
            else if (request.ClearCardDesign)
            {
                card.CardDesignId = null;
            }

            // Non-rule cosmetic fields (no rules version bump, no audit row).
            if (request.Name != null) card.Name = request.Name.Trim();
            if (request.Description != null) card.Description = request.Description.Trim();

            // ── Business rules (stamps required / reward) ───────────────────
            // Detected against the persisted values BEFORE mutating anything, so
            // a rules-neutral request cannot bump the version or write history.
            var changes = CardRulesPolicy.DetectRulesChanges(
                card, request.StampsRequired, request.RewardDescription, request.RewardValue);

            var appliedToExisting = false;
            var affectedCards = 0;

            if (changes.Count > 0)
            {
                appliedToExisting = CardRulesPolicy.CanApplyToExistingCards(
                    request.ApplyToExistingCards, request.Reason);

                if (request.StampsRequired.HasValue) card.StampsRequired = request.StampsRequired.Value;
                if (request.RewardDescription != null) card.RewardDescription = request.RewardDescription.Trim();
                if (request.RewardValue.HasValue) card.RewardValue = request.RewardValue.Value;

                card.RulesVersion += 1;
                card.UpdatedAt = DateTime.UtcNow;

                // Default policy: existing enrollments keep the requirement they
                // joined under. Only an explicit, reasoned opt-in re-snapshots
                // in-flight cards — and every affected row is counted here.
                if (appliedToExisting)
                {
                    var bound = await _unitOfWork.LoyaltyCards.FindAsync(c => c.StampCardId == card.Id);
                    foreach (var loyaltyCard in bound)
                    {
                        loyaltyCard.RequiredStamps = card.StampsRequired;
                        loyaltyCard.RulesVersion = card.RulesVersion;
                        _unitOfWork.LoyaltyCards.Update(loyaltyCard);
                        affectedCards++;
                    }
                }

                foreach (var change in changes)
                {
                    await _unitOfWork.StampCardRulesChanges.AddAsync(new StampCardRulesChange
                    {
                        Id = Guid.NewGuid(),
                        StampCardId = card.Id,
                        BusinessId = business.Id,
                        ChangedByUserId = ownerId,
                        ChangedByRole = "Business",
                        Field = change.Field,
                        OldValue = change.OldValue,
                        NewValue = change.NewValue,
                        AppliedToExistingCards = appliedToExisting,
                        AffectedCards = affectedCards,
                        Reason = request.Reason?.Trim(),
                        RulesVersion = card.RulesVersion,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                _logger.LogInformation(
                    "LOYALTY_CARD_RULES_CHANGED CardId={CardId} BusinessId={BusinessId} RulesVersion={RulesVersion} " +
                    "AppliedToExisting={AppliedToExisting} AffectedCards={AffectedCards} Fields={Fields} Actor={ActorId}",
                    card.Id, business.Id, card.RulesVersion, appliedToExisting, affectedCards,
                    string.Join(",", changes.Select(c => c.Field)), ownerId);
            }

            _unitOfWork.StampCards.Update(card);
            await _unitOfWork.SaveChangesAsync();

            _logger.LogInformation(
                "LOYALTY_CARD_UPDATED CardId={CardId} BusinessId={BusinessId} RulesChanged={RulesChanged} Actor={ActorId}",
                card.Id, business.Id, changes.Count > 0, ownerId);

            return ApiResponse<StampCardResponse>.Ok(MapCard(card));
        }
        catch (DbUpdateException)
        {
            // A concurrent rules change on the same card raced this write. The
            // audit rows and version bump are written in one transaction, so the
            // caller simply retries against the fresh state.
            _logger.LogWarning("Concurrent update rejected for stamp card {StampCardId}.", stampCardId);
            return ApiResponse<StampCardResponse>.Fail(
                "CONFLICT", "The stamp card was modified concurrently. Reload and try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating stamp card {StampCardId}", stampCardId);
            return ApiResponse<StampCardResponse>.Fail("UPDATE_FAILED", "Failed to update the stamp card.");
        }
    }

    public async Task<ApiResponse<StampCardResponse>> UpdateStampCardStatusAsync(Guid ownerId, Guid stampCardId, UpdateStampCardStatusRequest request)
    {
        var business = await ResolveBusinessAsync(ownerId);
        if (business == null)
            return ApiResponse<StampCardResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var card = await _unitOfWork.StampCards.FirstOrDefaultAsync(c => c.Id == stampCardId && c.BusinessId == business.Id);
        if (card == null)
            return ApiResponse<StampCardResponse>.Fail("NOT_FOUND", "Stamp card not found.");

        if (!TryParseStatus(request.Status, out var newStatus))
            return ApiResponse<StampCardResponse>.Fail(
                "INVALID_STATUS", "status must be one of: draft, active, inactive, archived.");

        // Explicit lifecycle matrix (CardRulesPolicy.CanTransition): Archived is
        // terminal; everything else follows Draft → Active ↔ Inactive, Archived.
        if (!CardRulesPolicy.CanTransition(card.Status, newStatus))
            return ApiResponse<StampCardResponse>.Fail(
                "INVALID_TRANSITION",
                $"A {MapStatus(card.Status).ToLowerInvariant()} stamp card cannot transition to {request.Status.Trim().ToLowerInvariant()}.");

        card.Status = newStatus;
        card.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.StampCards.Update(card);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation(
            "LOYALTY_CARD_STATUS_CHANGED CardId={CardId} BusinessId={BusinessId} Status={Status} Actor={ActorId}",
            card.Id, business.Id, MapStatus(card.Status), ownerId);

        return ApiResponse<StampCardResponse>.Ok(MapCard(card));
    }

    public async Task<ApiResponse<StampCardResponse>> DuplicateStampCardAsync(Guid ownerId, Guid stampCardId, DuplicateStampCardRequest? request)
    {
        try
        {
            var business = await ResolveBusinessAsync(ownerId);
            if (business == null)
                return ApiResponse<StampCardResponse>.Fail("NOT_FOUND", "No business found for this account.");

            var card = await _unitOfWork.StampCards.FirstOrDefaultAsync(c => c.Id == stampCardId && c.BusinessId == business.Id);
            if (card == null)
                return ApiResponse<StampCardResponse>.Fail("NOT_FOUND", "Stamp card not found.");

            var copy = new StampCard
            {
                Id = Guid.NewGuid(),
                ProgramId = card.ProgramId,
                BusinessId = card.BusinessId,
                Name = string.IsNullOrWhiteSpace(request?.NewName) ? $"{card.Name} (Copy)" : request!.NewName.Trim(),
                Description = card.Description,
                StampsRequired = card.StampsRequired,
                RewardDescription = card.RewardDescription,
                RewardValue = card.RewardValue,
                Status = StampCardStatus.Draft,
                CardDesignId = card.CardDesignId,
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.StampCards.AddAsync(copy);
            await _unitOfWork.SaveChangesAsync();

            return ApiResponse<StampCardResponse>.Ok(MapCard(copy));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error duplicating stamp card {StampCardId}", stampCardId);
            return ApiResponse<StampCardResponse>.Fail("DUPLICATE_FAILED", "Failed to duplicate the stamp card.");
        }
    }

    public async Task<ApiResponse<bool>> DeleteStampCardAsync(Guid ownerId, Guid stampCardId)
    {
        var business = await ResolveBusinessAsync(ownerId);
        if (business == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "No business found for this account.");

        var card = await _unitOfWork.StampCards.FirstOrDefaultAsync(c => c.Id == stampCardId && c.BusinessId == business.Id);
        if (card == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "Stamp card not found.");

        // Only safe when the card is not live — archived/inactive cards have no
        // customer-facing surface depending on them.
        if (card.Status is StampCardStatus.Active or StampCardStatus.Draft)
            return ApiResponse<bool>.Fail("NOT_SAFE", "Deactivate or archive this stamp card before deleting it.");

        _unitOfWork.StampCards.Delete(card);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation(
            "LOYALTY_CARD_DELETED CardId={CardId} BusinessId={BusinessId} Actor={ActorId}",
            card.Id, card.BusinessId, ownerId);
        return ApiResponse<bool>.Ok(true);
    }
}
