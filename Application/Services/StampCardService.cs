using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

/// <summary>
/// Business-facing management of stamp cards (children of campaigns) and
/// reusable card designs (sanitized HTML templates), plus the shared preview
/// rendering pipeline.
/// </summary>
public class StampCardService : IStampCardService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<StampCardService> _logger;

    public StampCardService(IUnitOfWork unitOfWork, ILogger<StampCardService> logger)
    {
        _unitOfWork = unitOfWork;
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

    private static CardDesignResponse MapDesign(CardDesign design, int assignedCards = 0) => new()
    {
        Id = design.Id,
        BusinessId = design.BusinessId,
        Name = design.Name,
        HtmlTemplate = design.HtmlTemplate,
        IsActive = design.IsActive,
        AssignedCards = assignedCards,
        CreatedAt = design.CreatedAt
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
            return ApiResponse<List<StampCardResponse>>.Fail("NOT_FOUND", "Campaign not found.");

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
                return ApiResponse<StampCardResponse>.Fail("NOT_FOUND", "Campaign not found.");

            if (request.CardDesignId.HasValue)
            {
                var design = await _unitOfWork.CardDesigns
                    .FirstOrDefaultAsync(d => d.Id == request.CardDesignId.Value && d.BusinessId == business.Id);
                if (design == null)
                    return ApiResponse<StampCardResponse>.Fail("NOT_FOUND", "Card design not found.");
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
                var design = await _unitOfWork.CardDesigns
                    .FirstOrDefaultAsync(d => d.Id == request.CardDesignId.Value && d.BusinessId == business.Id);
                if (design == null)
                    return ApiResponse<StampCardResponse>.Fail("NOT_FOUND", "Card design not found.");
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

    public async Task<ApiResponse<List<CardDesignResponse>>> GetCardDesignsAsync(Guid ownerId)
    {
        var business = await ResolveBusinessAsync(ownerId);
        if (business == null)
            return ApiResponse<List<CardDesignResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        var designs = await _unitOfWork.CardDesigns.FindAsync(d => d.BusinessId == business.Id);
        var result = designs.OrderBy(d => d.CreatedAt).Select(d => MapDesign(d)).ToList();

        // Populate assigned card counts in one query.
        var cards = await _unitOfWork.StampCards.FindAsync(c => c.BusinessId == business.Id && c.CardDesignId != null);
        var counts = cards.GroupBy(c => c.CardDesignId!.Value).ToDictionary(g => g.Key, g => g.Count());
        foreach (var design in result)
            design.AssignedCards = counts.GetValueOrDefault(design.Id);

        return ApiResponse<List<CardDesignResponse>>.Ok(result);
    }

    public async Task<ApiResponse<CardDesignResponse>> GetCardDesignAsync(Guid ownerId, Guid designId)
    {
        var business = await ResolveBusinessAsync(ownerId);
        if (business == null)
            return ApiResponse<CardDesignResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var design = await _unitOfWork.CardDesigns.FirstOrDefaultAsync(d => d.Id == designId && d.BusinessId == business.Id);
        if (design == null)
            return ApiResponse<CardDesignResponse>.Fail("NOT_FOUND", "Card design not found.");

        var assigned = await _unitOfWork.StampCards.CountAsync(c => c.CardDesignId == designId);
        return ApiResponse<CardDesignResponse>.Ok(MapDesign(design, assigned));
    }

    public async Task<ApiResponse<CardDesignResponse>> CreateCardDesignAsync(Guid ownerId, CreateCardDesignRequest request)
    {
        try
        {
            var business = await ResolveBusinessAsync(ownerId);
            if (business == null)
                return ApiResponse<CardDesignResponse>.Fail("NOT_FOUND", "No business found for this account.");

            var design = new CardDesign
            {
                Id = Guid.NewGuid(),
                BusinessId = business.Id,
                Name = request.Name.Trim(),
                HtmlTemplate = CardTemplateSanitizer.Sanitize(request.HtmlTemplate),
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.CardDesigns.AddAsync(design);
            await _unitOfWork.SaveChangesAsync();

            return ApiResponse<CardDesignResponse>.Ok(MapDesign(design));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating card design for owner {OwnerId}", ownerId);
            return ApiResponse<CardDesignResponse>.Fail("CREATE_FAILED", "Failed to create the card design.");
        }
    }

    public async Task<ApiResponse<CardDesignResponse>> UpdateCardDesignAsync(Guid ownerId, Guid designId, UpdateCardDesignRequest request)
    {
        try
        {
            var business = await ResolveBusinessAsync(ownerId);
            if (business == null)
                return ApiResponse<CardDesignResponse>.Fail("NOT_FOUND", "No business found for this account.");

            var design = await _unitOfWork.CardDesigns.FirstOrDefaultAsync(d => d.Id == designId && d.BusinessId == business.Id);
            if (design == null)
                return ApiResponse<CardDesignResponse>.Fail("NOT_FOUND", "Card design not found.");

            if (request.Name != null) design.Name = request.Name.Trim();
            if (request.HtmlTemplate != null) design.HtmlTemplate = CardTemplateSanitizer.Sanitize(request.HtmlTemplate);
            if (request.IsActive.HasValue) design.IsActive = request.IsActive.Value;

            _unitOfWork.CardDesigns.Update(design);
            await _unitOfWork.SaveChangesAsync();

            var assigned = await _unitOfWork.StampCards.CountAsync(c => c.CardDesignId == designId);
            return ApiResponse<CardDesignResponse>.Ok(MapDesign(design, assigned));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating card design {DesignId}", designId);
            return ApiResponse<CardDesignResponse>.Fail("UPDATE_FAILED", "Failed to update the card design.");
        }
    }

    public async Task<ApiResponse<bool>> DeleteCardDesignAsync(Guid ownerId, Guid designId)
    {
        var business = await ResolveBusinessAsync(ownerId);
        if (business == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "No business found for this account.");

        var design = await _unitOfWork.CardDesigns.FirstOrDefaultAsync(d => d.Id == designId && d.BusinessId == business.Id);
        if (design == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "Card design not found.");

        var assigned = await _unitOfWork.StampCards.CountAsync(c => c.CardDesignId == designId);
        if (assigned > 0)
            return ApiResponse<bool>.Fail("IN_USE", $"This design is assigned to {assigned} stamp card(s). Unassign it first.");

        _unitOfWork.CardDesigns.Delete(design);
        await _unitOfWork.SaveChangesAsync();
        return ApiResponse<bool>.Ok(true);
    }

    public async Task<ApiResponse<PreviewCardDesignResponse>> PreviewCardDesignAsync(Guid ownerId, PreviewCardDesignRequest request)
    {
        var business = await ResolveBusinessAsync(ownerId);
        if (business == null)
            return ApiResponse<PreviewCardDesignResponse>.Fail("NOT_FOUND", "No business found for this account.");

        string sanitized;
        if (request.CardDesignId.HasValue)
        {
            var design = await _unitOfWork.CardDesigns
                .FirstOrDefaultAsync(d => d.Id == request.CardDesignId.Value && d.BusinessId == business.Id);
            if (design == null)
                return ApiResponse<PreviewCardDesignResponse>.Fail("NOT_FOUND", "Card design not found.");
            sanitized = design.HtmlTemplate;
        }
        else
        {
            var (isValid, error) = CardTemplateSanitizer.Validate(request.HtmlTemplate);
            if (!isValid)
                return ApiResponse<PreviewCardDesignResponse>.Fail("INVALID_TEMPLATE", error ?? "Invalid template.");
            sanitized = CardTemplateSanitizer.Sanitize(request.HtmlTemplate);
        }

        // Realistic sample data (overridable by the caller for their own brand).
        var totalStamps = Math.Clamp(request.TotalStamps ?? 10, 1, 100);
        var context = new CardTemplateRenderer.CardRenderContext
        {
            BusinessName = string.IsNullOrWhiteSpace(request.BusinessName) ? business.Name : request.BusinessName.Trim(),
            BusinessLogoUrl = business.LogoUrl,
            BusinessDescription = business.Description,
            CustomerName = string.IsNullOrWhiteSpace(request.CustomerName) ? "Peter Maina" : request.CustomerName.Trim(),
            CampaignName = "Coffee Rewards",
            CardName = string.IsNullOrWhiteSpace(request.CardName) ? "Coffee Card" : request.CardName.Trim(),
            RewardName = string.IsNullOrWhiteSpace(request.RewardName) ? "Buy 10 coffees, get 1 free" : request.RewardName.Trim(),
            TotalStamps = totalStamps,
            CompletedStamps = Math.Clamp(request.CompletedStamps ?? 4, 0, totalStamps)
        };

        var rendered = CardTemplateRenderer.Render(sanitized, context);

        return ApiResponse<PreviewCardDesignResponse>.Ok(new PreviewCardDesignResponse
        {
            SanitizedTemplate = sanitized,
            RenderedHtml = rendered,
            Variables = CardTemplateRenderer.AvailableVariables.ToList()
        });
    }
}