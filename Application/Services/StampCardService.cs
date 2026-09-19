using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
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
                Status = string.IsNullOrWhiteSpace(request.Status) ? StampCardStatus.Draft : ParseStatus(request.Status),
                CardDesignId = request.CardDesignId,
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.StampCards.AddAsync(card);
            await _unitOfWork.SaveChangesAsync();

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

            if (request.Name != null) card.Name = request.Name.Trim();
            if (request.Description != null) card.Description = request.Description.Trim();
            if (request.StampsRequired.HasValue) card.StampsRequired = request.StampsRequired.Value;
            if (request.RewardDescription != null) card.RewardDescription = request.RewardDescription.Trim();
            if (request.RewardValue.HasValue) card.RewardValue = request.RewardValue.Value;

            _unitOfWork.StampCards.Update(card);
            await _unitOfWork.SaveChangesAsync();

            return ApiResponse<StampCardResponse>.Ok(MapCard(card));
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

        var newStatus = ParseStatus(request.Status);
        if (card.Status == StampCardStatus.Archived && newStatus != StampCardStatus.Archived)
            return ApiResponse<StampCardResponse>.Fail("ARCHIVED", "Archived stamp cards are terminal and cannot be restored.");

        card.Status = newStatus;
        _unitOfWork.StampCards.Update(card);
        await _unitOfWork.SaveChangesAsync();

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
        return ApiResponse<bool>.Ok(true);
    }
}
