using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.Assets;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Design;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

/// <summary>
/// Card design lifecycle (Admin) and availability (Business).
///
/// Every design is validated + sanitized here — this is the only writer of
/// <see cref="CardDesign.HtmlTemplate"/>, so saved, previewed and rendered
/// templates are guaranteed to share the same sanitization rules.
/// </summary>
public class CardDesignService : ICardDesignService
{
    /// <summary>Largest number of custom designs a single business may own (storage-abuse guard).</summary>
    private const int MaxBusinessDesigns = 25;

    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardDesignResolver _resolver;
    private readonly ILogger<CardDesignService> _logger;

    public CardDesignService(
        IUnitOfWork unitOfWork,
        ICardDesignResolver resolver,
        ILogger<CardDesignService> logger)
    {
        _unitOfWork = unitOfWork;
        _resolver = resolver;
        _logger = logger;
    }

    // ── Shared helpers ──────────────────────────────────────

    private static CardDesignResponse MapDesign(
        CardDesign design,
        string? businessName = null,
        int assignedPrograms = 0,
        int assignedCards = 0) => new()
        {
            Id = design.Id,
            BusinessId = design.BusinessId,
            BusinessName = businessName,
            Name = design.Name,
            HtmlTemplate = design.HtmlTemplate,
            IsActive = design.IsActive,
            IsDefault = design.IsDefault,
            ConfigJson = design.ConfigJson,
            CurrentVersion = design.CurrentVersion,
            UpdatedAt = design.UpdatedAt,
            AssignedPrograms = assignedPrograms,
            AssignedCards = assignedCards,
            CreatedAt = design.CreatedAt
        };

    /// <summary>
    /// Appends an immutable presentation snapshot and bumps the design's version.
    /// Staged (not saved) so the caller's SaveChanges persists design + version
    /// atomically; a concurrent publish collides on the unique
    /// (design, version) index and the loser surfaces as CONFLICT rather than a
    /// forked history.
    /// </summary>
    private async Task AppendVersionAsync(CardDesign design, Guid? actorUserId, string? changeNote)
    {
        var now = DateTime.UtcNow;
        design.CurrentVersion += 1;
        design.UpdatedAt = now;

        await _unitOfWork.CardDesignVersions.AddAsync(new CardDesignVersion
        {
            Id = Guid.NewGuid(),
            CardDesignId = design.Id,
            VersionNumber = design.CurrentVersion,
            Name = design.Name,
            HtmlTemplate = design.HtmlTemplate,
            ConfigJson = design.ConfigJson,
            PublishedAt = now,
            PublishedByUserId = actorUserId,
            ChangeNote = changeNote,
            CreatedAt = now
        });
    }

    /// <summary>
    /// Validates a structured config and turns it into a sanitized template.
    /// Shape is checked by <see cref="CardDesignConfigValidator"/>; asset
    /// ownership by the shared <see cref="CardAssetReferences"/> evaluator, so
    /// "is this asset usable by this business" has exactly one implementation.
    /// </summary>
    private async Task<(string? Template, string? ErrorCode, string? Message)> BuildConfigTemplateAsync(
        Guid? businessId, CardDesignConfig config)
    {
        var validation = CardDesignConfigValidator.Validate(config);
        if (!validation.IsValid)
            return (null, "INVALID_CONFIG", validation.Summary);

        if (businessId.HasValue)
        {
            var ids = config.ReferencedAssetIds().ToList();
            if (ids.Count > 0)
            {
                var owned = (await _unitOfWork.CardAssets.FindAsync(a =>
                        a.BusinessId == businessId.Value &&
                        a.Status == CardAssetStatus.Active &&
                        ids.Contains(a.Id)))
                    .Select(a => a.Id)
                    .ToList();

                var check = CardAssetReferences.Evaluate(ids, owned);
                if (!check.IsValid)
                    return (null, check.ErrorCode!, check.Message!);
            }
        }

        // The generated template only ever uses allow-listed tags/attrs/CSS, so
        // sanitization is a defence-in-depth no-op — but it is still applied so
        // config-generated and hand-authored designs share one pipeline (§15).
        return (CardTemplateSanitizer.Sanitize(CardDesignTemplate.Build(config)), null, null);
    }

    /// <summary>
    /// Builds the preview response through the single shared pipeline:
    /// (already sanitized template) → resolve variables → render.
    /// </summary>
    private static PreviewCardDesignResponse BuildPreview(
        string sanitizedTemplate,
        CardTemplateRenderer.CardRenderContext context) => new()
        {
            SanitizedTemplate = sanitizedTemplate,
            RenderedHtml = CardTemplateRenderer.Render(sanitizedTemplate, context),
            Variables = CardTemplateRenderer.AvailableVariables.ToList()
        };

    /// <summary>Sample context branded with a business's public identity (plan §7).</summary>
    private static CardTemplateRenderer.CardRenderContext SampleContextFor(
        Business? business,
        PreviewCardDesignRequest request) =>
        CardPreviewSampleData.CreateContext(
            businessName: request.BusinessName ?? business?.Name,
            businessLogoUrl: business?.LogoUrl,
            customerName: request.CustomerName,
            cardName: request.CardName,
            rewardName: request.RewardName,
            totalStamps: request.TotalStamps,
            completedStamps: request.CompletedStamps,
            programName: request.ProgramName);

    private Task<Business?> ResolveBusinessForOwnerAsync(Guid ownerId) =>
        _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.OwnerId == ownerId);

    private async Task<string?> GetOwnerBusinessNameAsync(CardDesign design)
    {
        if (!design.BusinessId.HasValue) return null;
        var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.Id == design.BusinessId.Value);
        return business?.Name;
    }
/// <summary>
    /// Resolves the sanitized template a preview should render. Untrusted input
    /// (raw HTML) is always validated + sanitized BEFORE rendering (plan §18).
    /// When <paramref name="enforceTenant"/> is true, only the platform default
    /// or the caller's own design may be previewed by id (plan §16).
    /// </summary>
    private async Task<(string? Template, string? ErrorCode, string? ErrorMessage)> ResolvePreviewTemplateAsync(
        string? rawHtml,
        Guid? cardDesignId,
        Guid? allowedBusinessId,
        bool enforceTenant)
    {
        if (!string.IsNullOrWhiteSpace(rawHtml))
        {
            var (isValid, error) = CardTemplateSanitizer.Validate(rawHtml);
            if (!isValid)
                return (null, "INVALID_TEMPLATE", error ?? "Invalid template.");

            return (CardTemplateSanitizer.Sanitize(rawHtml), null, null);
        }

        if (cardDesignId.HasValue)
        {
            var design = enforceTenant
                ? await _unitOfWork.CardDesigns.FirstOrDefaultAsync(d =>
                    d.Id == cardDesignId.Value &&
                    (d.BusinessId == null || d.BusinessId == allowedBusinessId))
                : await _unitOfWork.CardDesigns.FirstOrDefaultAsync(d => d.Id == cardDesignId.Value);

            if (design == null)
                return (null, "NOT_FOUND", "Card design not found.");

            return (design.HtmlTemplate, null, null);
        }

        var defaultDesign = await _resolver.GetDefaultDesignAsync();
        return (defaultDesign?.HtmlTemplate ?? DefaultCardTemplate.Html, null, null);
    }

    // ─ Admin: read ────────────────────────────────────────

    public async Task<ApiResponse<List<CardDesignResponse>>> GetDesignsForBusinessAsync(Guid businessId)
    {
        var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.Id == businessId);
        if (business == null)
            return ApiResponse<List<CardDesignResponse>>.Fail(
                "BUSINESS_NOT_FOUND", "No business exists with the given id.");

        var designs = (await _unitOfWork.CardDesigns.FindAsync(d => d.BusinessId == businessId))
            .OrderBy(d => d.CreatedAt)
            .ToList();

        var programs = await _unitOfWork.LoyaltyPrograms.FindAsync(p => p.BusinessId == businessId);
        var programCounts = programs
            .Where(p => p.CardDesignId.HasValue)
            .GroupBy(p => p.CardDesignId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var cards = await _unitOfWork.StampCards.FindAsync(
            c => c.BusinessId == businessId && c.CardDesignId != null);
        var cardCounts = cards
            .GroupBy(c => c.CardDesignId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var result = designs
            .Select(d => MapDesign(
                d,
                business.Name,
                programCounts.GetValueOrDefault(d.Id),
                cardCounts.GetValueOrDefault(d.Id)))
            .ToList();

        return ApiResponse<List<CardDesignResponse>>.Ok(result);
    }
// ── Admin: write ────────────────────────────────────────

    public async Task<ApiResponse<CardDesignResponse>> CreateDesignAsync(
        Guid businessId,
        CreateCardDesignRequest request,
        Guid? adminUserId)
    {
        try
        {
            var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.Id == businessId);
            if (business == null)
                return ApiResponse<CardDesignResponse>.Fail(
                    "BUSINESS_NOT_FOUND", "No business exists with the given id.");

            var name = (request.Name ?? string.Empty).Trim();
            if (name.Length == 0)
                return ApiResponse<CardDesignResponse>.Fail("INVALID_NAME", "Design name must not be empty.");
            if (name.Length > 100)
                return ApiResponse<CardDesignResponse>.Fail(
                    "INVALID_NAME", "Design name must not exceed 100 characters.");

            var (isValid, error) = CardTemplateSanitizer.Validate(request.HtmlTemplate);
            if (!isValid)
                return ApiResponse<CardDesignResponse>.Fail("INVALID_TEMPLATE", error ?? "Invalid template.");

            // Creating a design does NOT require the business to hold the
            // entitlement: Admin-authored designs are preserved across plan
            // changes and stay invisible to the business until the module is
            // enabled again (plan §12).
            var design = new CardDesign
            {
                Id = Guid.NewGuid(),
                BusinessId = business.Id,
                Name = name,
                HtmlTemplate = CardTemplateSanitizer.Sanitize(request.HtmlTemplate),
                IsActive = true,
                IsDefault = false,
                CreatedAt = DateTime.UtcNow
            };

            // Version 1 is recorded with the row itself — from the first moment a
            // design exists there is an immutable presentation history (§6).
            await AppendVersionAsync(design, adminUserId, "Design created");

            await _unitOfWork.CardDesigns.AddAsync(design);
            await _unitOfWork.SaveChangesAsync();

            _logger.LogInformation(
                "DESIGN_CREATED DesignId={DesignId} BusinessId={BusinessId} Version={Version} Actor={ActorId}",
                design.Id, business.Id, design.CurrentVersion, adminUserId);

            _logger.LogInformation(
                "Admin {AdminUserId} created card design {DesignId} ('{DesignName}') for business {BusinessId}.",
                adminUserId, design.Id, design.Name, business.Id);

            return ApiResponse<CardDesignResponse>.Ok(MapDesign(design, business.Name));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating card design for business {BusinessId}", businessId);
            return ApiResponse<CardDesignResponse>.Fail("CREATE_FAILED", "Failed to create the card design.");
        }
    }

    public async Task<ApiResponse<CardDesignResponse>> UpdateDesignAsync(
        Guid designId,
        UpdateCardDesignRequest request,
        Guid? adminUserId)
    {
        try
        {
            var design = await _unitOfWork.CardDesigns.FirstOrDefaultAsync(d => d.Id == designId);
            if (design == null)
                return ApiResponse<CardDesignResponse>.Fail("NOT_FOUND", "Card design not found.");

            var contentChanged = false;

            if (request.Name != null)
            {
                var name = request.Name.Trim();
                if (name.Length == 0)
                    return ApiResponse<CardDesignResponse>.Fail("INVALID_NAME", "Design name must not be empty.");
                if (name.Length > 100)
                    return ApiResponse<CardDesignResponse>.Fail(
                        "INVALID_NAME", "Design name must not exceed 100 characters.");
                if (!string.Equals(design.Name, name, StringComparison.Ordinal))
                {
                    design.Name = name;
                    contentChanged = true;
                }
            }

            if (request.HtmlTemplate != null)
            {
                var (isValid, error) = CardTemplateSanitizer.Validate(request.HtmlTemplate);
                if (!isValid)
                    return ApiResponse<CardDesignResponse>.Fail("INVALID_TEMPLATE", error ?? "Invalid template.");
                var sanitized = CardTemplateSanitizer.Sanitize(request.HtmlTemplate);
                if (!string.Equals(design.HtmlTemplate, sanitized, StringComparison.Ordinal))
                {
                    design.HtmlTemplate = sanitized;
                    contentChanged = true;
                }
            }

            if (request.IsActive.HasValue)
            {
                if (design.IsDefault && !request.IsActive.Value)
                    return ApiResponse<CardDesignResponse>.Fail(
                        "DEFAULT_IMMUTABLE",
                        "The platform default design cannot be deactivated — every loyalty program falls back to it.");

                design.IsActive = request.IsActive.Value;
            }

            // Only a content change produces a new immutable version; an
            // activation toggle is lifecycle metadata, not presentation.
            if (contentChanged)
                await AppendVersionAsync(design, adminUserId, "Design updated");

            design.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.CardDesigns.Update(design);
            await _unitOfWork.SaveChangesAsync();

            _logger.LogInformation(
                "DESIGN_UPDATED DesignId={DesignId} Version={Version} ContentChanged={ContentChanged} Actor={ActorId}",
                design.Id, design.CurrentVersion, contentChanged, adminUserId);
            _logger.LogInformation("Admin {AdminUserId} updated card design {DesignId}.", adminUserId, design.Id);

            return ApiResponse<CardDesignResponse>.Ok(MapDesign(design, await GetOwnerBusinessNameAsync(design)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating card design {DesignId}", designId);
            return ApiResponse<CardDesignResponse>.Fail("UPDATE_FAILED", "Failed to update the card design.");
        }
    }
public async Task<ApiResponse<CardDesignResponse>> SetDesignActiveAsync(Guid designId, bool isActive)
    {
        var design = await _unitOfWork.CardDesigns.FirstOrDefaultAsync(d => d.Id == designId);
        if (design == null)
            return ApiResponse<CardDesignResponse>.Fail("NOT_FOUND", "Card design not found.");

        if (design.IsDefault && !isActive)
            return ApiResponse<CardDesignResponse>.Fail(
                "DEFAULT_IMMUTABLE",
                "The platform default design cannot be deactivated — every loyalty program falls back to it.");

        design.IsActive = isActive;
        _unitOfWork.CardDesigns.Update(design);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation(
            "Card design {DesignId} {Action}.", designId, isActive ? "activated" : "deactivated");

        return ApiResponse<CardDesignResponse>.Ok(MapDesign(design, await GetOwnerBusinessNameAsync(design)));
    }

    public async Task<ApiResponse<bool>> DeleteDesignAsync(Guid designId)
    {
        var design = await _unitOfWork.CardDesigns.FirstOrDefaultAsync(d => d.Id == designId);
        if (design == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "Card design not found.");

        if (design.IsDefault)
            return ApiResponse<bool>.Fail(
                "DEFAULT_IMMUTABLE",
                "The platform default design cannot be deleted — every loyalty program falls back to it.");

        // Preserve data: refuse while referenced so a business never silently
        // loses its program selection (plan §12). Deactivate instead.
        var programs = await _unitOfWork.LoyaltyPrograms.CountAsync(p => p.CardDesignId == designId);
        var cards = await _unitOfWork.StampCards.CountAsync(c => c.CardDesignId == designId);
        if (programs > 0 || cards > 0)
            return ApiResponse<bool>.Fail(
                "IN_USE",
                $"This design is selected by {programs} loyalty program(s) and used by {cards} stamp card(s). Deactivate it instead, or reassign them first.");

        _unitOfWork.CardDesigns.Delete(design);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogWarning("Card design {DesignId} deleted.", designId);
        return ApiResponse<bool>.Ok(true);
    }

    // ─ Preview (single shared pipeline) ────────────────────

    public async Task<ApiResponse<PreviewCardDesignResponse>> PreviewAsync(AdminPreviewCardDesignRequest request)
    {
        Business? business = null;
        if (request.BusinessId.HasValue)
        {
            business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.Id == request.BusinessId.Value);
            if (business == null)
                return ApiResponse<PreviewCardDesignResponse>.Fail(
                    "BUSINESS_NOT_FOUND", "No business exists with the given id.");
        }

        // Config preview shares the same builder as the business path; asset
        // ownership is checked whenever the target business is known.
        if (request.Config != null)
        {
            var (configTemplate, configCode, configMessage) =
                await BuildConfigTemplateAsync(business?.Id, request.Config);
            if (configTemplate == null)
                return ApiResponse<PreviewCardDesignResponse>.Fail(
                    configCode!, configMessage ?? "Invalid configuration.");

            return ApiResponse<PreviewCardDesignResponse>.Ok(
                BuildPreview(configTemplate, SampleContextFor(business, request)));
        }

        var (template, code, message) = await ResolvePreviewTemplateAsync(
            request.HtmlTemplate, request.CardDesignId, business?.Id, enforceTenant: false);

        if (template == null)
            return ApiResponse<PreviewCardDesignResponse>.Fail(code ?? "PREVIEW_FAILED", message ?? "Preview failed.");

        return ApiResponse<PreviewCardDesignResponse>.Ok(
            BuildPreview(template, SampleContextFor(business, request)));
    }

    public async Task<ApiResponse<PreviewCardDesignResponse>> PreviewForBusinessAsync(
        Guid ownerId,
        PreviewCardDesignRequest request)
    {
        var business = await ResolveBusinessForOwnerAsync(ownerId);
        if (business == null)
            return ApiResponse<PreviewCardDesignResponse>.Fail("NOT_FOUND", "No business found for this account.");

        // Live designer preview: an unsaved config is validated + rendered
        // through the exact save-time pipeline (§15 — preview == production).
        if (request.Config != null)
        {
            if (!await _resolver.BusinessHasCustomCardDesignAsync(business.Id))
                return ApiResponse<PreviewCardDesignResponse>.Fail(
                    "MODULE_DISABLED",
                    $"The '{ICardDesignService.ModuleKey}' module is not enabled for this business.");

            var (configTemplate, configCode, configMessage) =
                await BuildConfigTemplateAsync(business.Id, request.Config);
            if (configTemplate == null)
                return ApiResponse<PreviewCardDesignResponse>.Fail(
                    configCode!, configMessage ?? "Invalid configuration.");

            return ApiResponse<PreviewCardDesignResponse>.Ok(
                BuildPreview(configTemplate, SampleContextFor(business, request)));
        }

        var (template, code, message) = await ResolvePreviewTemplateAsync(
            request.HtmlTemplate, request.CardDesignId, business.Id, enforceTenant: true);

        if (template == null)
            return ApiResponse<PreviewCardDesignResponse>.Fail(code ?? "PREVIEW_FAILED", message ?? "Preview failed.");

        return ApiResponse<PreviewCardDesignResponse>.Ok(
            BuildPreview(template, SampleContextFor(business, request)));
    }
// ── Business: availability + validation ─────────────────

    public async Task<ApiResponse<List<AvailableCardDesignResponse>>> GetAvailableDesignsAsync(Guid ownerId)
    {
        var business = await ResolveBusinessForOwnerAsync(ownerId);
        if (business == null)
            return ApiResponse<List<AvailableCardDesignResponse>>.Fail(
                "NOT_FOUND", "No business found for this account.");

        var hasModule = await _resolver.BusinessHasCustomCardDesignAsync(business.Id);
        var sampleContext = SampleContextFor(business, new PreviewCardDesignRequest());
        var result = new List<AvailableCardDesignResponse>();

        // 1. The platform default — always present, always available (plan §4).
        var defaultDesign = await _resolver.GetDefaultDesignAsync();
        result.Add(new AvailableCardDesignResponse
        {
            Id = defaultDesign?.Id ?? DefaultCardTemplate.Id,
            Name = defaultDesign?.Name ?? DefaultCardTemplate.Name,
            IsDefault = true,
            IsActive = true,
            PreviewHtml = CardTemplateRenderer.Render(
                defaultDesign?.HtmlTemplate ?? DefaultCardTemplate.Html, sampleContext)
        });

        // 2. The business's own designs — ONLY when the module is enabled, and
        //    never another tenant's (plan §16).
        if (hasModule)
        {
            var designs = (await _unitOfWork.CardDesigns.FindAsync(d =>
                    d.BusinessId == business.Id && d.IsActive && !d.IsDefault))
                .OrderBy(d => d.CreatedAt);

            foreach (var design in designs)
            {
                result.Add(new AvailableCardDesignResponse
                {
                    Id = design.Id,
                    Name = design.Name,
                    IsDefault = false,
                    IsActive = design.IsActive,
                    PreviewHtml = CardTemplateRenderer.Render(design.HtmlTemplate, sampleContext)
                });
            }
        }

        return ApiResponse<List<AvailableCardDesignResponse>>.Ok(result);
    }

    public async Task<CardDesignSelectionCheck> ValidateSelectionAsync(Guid businessId, Guid? cardDesignId)
    {
        // No selection ⇒ the default card renders. Always valid.
        if (!cardDesignId.HasValue) return CardDesignSelectionCheck.Valid;

        var design = await _unitOfWork.CardDesigns.FirstOrDefaultAsync(d => d.Id == cardDesignId.Value);
        if (design == null)
            return CardDesignSelectionCheck.Invalid(
                "DESIGN_NOT_FOUND", "The selected card design does not exist.");

        // The platform default is never gated by the optional module.
        if (design.BusinessId == null)
        {
            return design.IsActive
                ? CardDesignSelectionCheck.Valid
                : CardDesignSelectionCheck.Invalid(
                    "DESIGN_INACTIVE", "The default card design is not active.");
        }

        // Tenant isolation: never accept another business's design (plan §16).
        if (design.BusinessId.Value != businessId)
            return CardDesignSelectionCheck.Invalid(
                "DESIGN_NOT_AVAILABLE", "The selected card design is not available to this business.");

        if (!design.IsActive)
            return CardDesignSelectionCheck.Invalid(
                "DESIGN_INACTIVE", "The selected card design is inactive. Choose another design.");

        if (!await _resolver.BusinessHasCustomCardDesignAsync(businessId))
            return CardDesignSelectionCheck.Invalid(
                "MODULE_DISABLED",
                $"The '{ICardDesignService.ModuleKey}' module is not enabled for this business.");

        return CardDesignSelectionCheck.Valid;
    }

    // ── Business (config-based authoring, §5/§6/§16) ────────────

    public async Task<ApiResponse<List<CardDesignResponse>>> GetMyDesignsAsync(Guid ownerId)
    {
        var business = await ResolveBusinessForOwnerAsync(ownerId);
        if (business == null)
            return ApiResponse<List<CardDesignResponse>>.Fail(
                "NOT_FOUND", "No business found for this account.");

        // Reads are allowed even when the module is off: a downgrade must never
        // hide (or delete) a business's own designs — only writes are gated (§12).
        var designs = (await _unitOfWork.CardDesigns.FindAsync(
                d => d.BusinessId == business.Id && !d.IsDefault))
            .OrderBy(d => d.CreatedAt)
            .ToList();

        var programCounts = (await _unitOfWork.LoyaltyPrograms.FindAsync(p => p.BusinessId == business.Id))
            .Where(p => p.CardDesignId.HasValue)
            .GroupBy(p => p.CardDesignId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var cardCounts = (await _unitOfWork.StampCards.FindAsync(
                c => c.BusinessId == business.Id && c.CardDesignId != null))
            .GroupBy(c => c.CardDesignId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        return ApiResponse<List<CardDesignResponse>>.Ok(
            designs.Select(d => MapDesign(
                d,
                business.Name,
                programCounts.GetValueOrDefault(d.Id),
                cardCounts.GetValueOrDefault(d.Id)))
            .ToList());
    }

    public async Task<ApiResponse<CardDesignResponse>> CreateBusinessDesignAsync(
        Guid ownerId, CreateBusinessCardDesignRequest request)
    {
        try
        {
            var business = await ResolveBusinessForOwnerAsync(ownerId);
            if (business == null)
                return ApiResponse<CardDesignResponse>.Fail(
                    "NOT_FOUND", "No business found for this account.");

            // Fail-closed module gate for writes (plan §16): the data survives a
            // downgrade, but new authoring requires the entitlement.
            if (!await _resolver.BusinessHasCustomCardDesignAsync(business.Id))
                return ApiResponse<CardDesignResponse>.Fail(
                    "MODULE_DISABLED",
                    $"The '{ICardDesignService.ModuleKey}' module is not enabled for this business.");

            var name = (request.Name ?? string.Empty).Trim();
            if (name.Length == 0)
                return ApiResponse<CardDesignResponse>.Fail("INVALID_NAME", "Design name must not be empty.");
            if (name.Length > 100)
                return ApiResponse<CardDesignResponse>.Fail(
                    "INVALID_NAME", "Design name must not exceed 100 characters.");

            // Storage-abuse guard: cap a tenant's design library.
            var existingCount = await _unitOfWork.CardDesigns.CountAsync(
                d => d.BusinessId == business.Id && !d.IsDefault);
            if (existingCount >= MaxBusinessDesigns)
                return ApiResponse<CardDesignResponse>.Fail(
                    "DESIGN_LIMIT_REACHED",
                    $"This business has reached the limit of {MaxBusinessDesigns} card designs.");

            // Null config ⇒ the safe professional default (§17): a usable branded
            // card before any artwork exists.
            var config = request.Config ?? CardDesignConfig.CreateDefault();
            var (template, errorCode, errorMessage) = await BuildConfigTemplateAsync(business.Id, config);
            if (template == null)
                return ApiResponse<CardDesignResponse>.Fail(
                    errorCode!, errorMessage ?? "The card design configuration is invalid.");

            var design = new CardDesign
            {
                Id = Guid.NewGuid(),
                BusinessId = business.Id,
                Name = name,
                HtmlTemplate = template,
                ConfigJson = config.ToJson(),
                IsActive = true,
                IsDefault = false,
                CreatedAt = DateTime.UtcNow
            };

            await AppendVersionAsync(design, ownerId, request.ChangeNote?.Trim() ?? "Design created");
            await _unitOfWork.CardDesigns.AddAsync(design);
            await _unitOfWork.SaveChangesAsync();

            _logger.LogInformation(
                "DESIGN_CREATED DesignId={DesignId} BusinessId={BusinessId} Version={Version} Actor={ActorId}",
                design.Id, business.Id, design.CurrentVersion, ownerId);

            return ApiResponse<CardDesignResponse>.Ok(MapDesign(design, business.Name));
        }
        catch (DbUpdateException)
        {
            _logger.LogWarning("Concurrent design create rejected for owner {OwnerId}.", ownerId);
            return ApiResponse<CardDesignResponse>.Fail(
                "CONFLICT", "The design was modified concurrently. Reload and try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating business card design for owner {OwnerId}", ownerId);
            return ApiResponse<CardDesignResponse>.Fail("CREATE_FAILED", "Failed to create the card design.");
        }
    }

    public async Task<ApiResponse<CardDesignResponse>> UpdateBusinessDesignAsync(
        Guid ownerId, Guid designId, UpdateBusinessCardDesignRequest request)
    {
        try
        {
            var business = await ResolveBusinessForOwnerAsync(ownerId);
            if (business == null)
                return ApiResponse<CardDesignResponse>.Fail(
                    "NOT_FOUND", "No business found for this account.");

            if (!await _resolver.BusinessHasCustomCardDesignAsync(business.Id))
                return ApiResponse<CardDesignResponse>.Fail(
                    "MODULE_DISABLED",
                    $"The '{ICardDesignService.ModuleKey}' module is not enabled for this business.");

            // Tenant scoping in the query: another tenant's id reads as NOT_FOUND (§20).
            var design = await _unitOfWork.CardDesigns.FirstOrDefaultAsync(d =>
                d.Id == designId && d.BusinessId == business.Id && !d.IsDefault);
            if (design == null)
                return ApiResponse<CardDesignResponse>.Fail("NOT_FOUND", "Card design not found.");

            var contentChanged = false;

            if (request.Name != null)
            {
                var name = request.Name.Trim();
                if (name.Length == 0)
                    return ApiResponse<CardDesignResponse>.Fail("INVALID_NAME", "Design name must not be empty.");
                if (name.Length > 100)
                    return ApiResponse<CardDesignResponse>.Fail(
                        "INVALID_NAME", "Design name must not exceed 100 characters.");
                if (!string.Equals(design.Name, name, StringComparison.Ordinal))
                {
                    design.Name = name;
                    contentChanged = true;
                }
            }

            if (request.Config != null)
            {
                var (template, errorCode, errorMessage) = await BuildConfigTemplateAsync(business.Id, request.Config);
                if (template == null)
                    return ApiResponse<CardDesignResponse>.Fail(
                        errorCode!, errorMessage ?? "The card design configuration is invalid.");

                var configJson = request.Config.ToJson();
                if (!string.Equals(design.ConfigJson, configJson, StringComparison.Ordinal)
                    || !string.Equals(design.HtmlTemplate, template, StringComparison.Ordinal))
                {
                    design.ConfigJson = configJson;
                    design.HtmlTemplate = template;
                    contentChanged = true;
                }
            }

            if (contentChanged)
                await AppendVersionAsync(design, ownerId, request.ChangeNote?.Trim() ?? "Design updated");

            design.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.CardDesigns.Update(design);
            await _unitOfWork.SaveChangesAsync();

            // Presentation-only operation: no LoyaltyCard, StampCard or
            // StampTransaction is touched anywhere in this method (§36).
            _logger.LogInformation(
                "DESIGN_UPDATED DesignId={DesignId} BusinessId={BusinessId} Version={Version} " +
                "ContentChanged={ContentChanged} Actor={ActorId}",
                design.Id, business.Id, design.CurrentVersion, contentChanged, ownerId);

            return ApiResponse<CardDesignResponse>.Ok(MapDesign(design, business.Name));
        }
        catch (DbUpdateException)
        {
            _logger.LogWarning("Concurrent design update rejected for design {DesignId}.", designId);
            return ApiResponse<CardDesignResponse>.Fail(
                "CONFLICT", "The design was modified concurrently. Reload and try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating business card design {DesignId}", designId);
            return ApiResponse<CardDesignResponse>.Fail("UPDATE_FAILED", "Failed to update the card design.");
        }
    }

    public async Task<ApiResponse<List<CardDesignVersionResponse>>> GetVersionsForBusinessAsync(
        Guid ownerId, Guid designId)
    {
        var business = await ResolveBusinessForOwnerAsync(ownerId);
        if (business == null)
            return ApiResponse<List<CardDesignVersionResponse>>.Fail(
                "NOT_FOUND", "No business found for this account.");

        var design = await _unitOfWork.CardDesigns.FirstOrDefaultAsync(d =>
            d.Id == designId && d.BusinessId == business.Id && !d.IsDefault);
        if (design == null)
            return ApiResponse<List<CardDesignVersionResponse>>.Fail("NOT_FOUND", "Card design not found.");

        return ApiResponse<List<CardDesignVersionResponse>>.Ok(await MapVersionsAsync(design));
    }

    public async Task<ApiResponse<List<CardDesignVersionResponse>>> GetVersionsForAdminAsync(Guid designId)
    {
        var design = await _unitOfWork.CardDesigns.FirstOrDefaultAsync(d => d.Id == designId);
        if (design == null)
            return ApiResponse<List<CardDesignVersionResponse>>.Fail("NOT_FOUND", "Card design not found.");

        return ApiResponse<List<CardDesignVersionResponse>>.Ok(await MapVersionsAsync(design));
    }

    private async Task<List<CardDesignVersionResponse>> MapVersionsAsync(CardDesign design)
    {
        var versions = await _unitOfWork.CardDesignVersions.FindAsync(
            v => v.CardDesignId == design.Id);

        return versions
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new CardDesignVersionResponse
            {
                VersionNumber = v.VersionNumber,
                Name = v.Name,
                ConfigJson = v.ConfigJson,
                PublishedAt = v.PublishedAt,
                PublishedByUserId = v.PublishedByUserId,
                ChangeNote = v.ChangeNote,
                IsCurrent = v.VersionNumber == design.CurrentVersion
            })
            .ToList();
    }
}