using Microsoft.Extensions.Logging;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

/// <summary>
/// The ONLY component that decides which card design a program renders.
///
/// Decision table (plan §13):
/// <code>
/// program.CardDesignId != null
///   AND business entitled to "customCardDesign"
///   AND design.IsActive
///     → the selected business design
///   otherwise
///     → the platform default design (in-code constant when the seeded row is absent)
/// </code>
///
/// The subscription entitlement controls *availability*, never data: a downgrade
/// silently falls back to the default and the program keeps its
/// <see cref="LoyaltyProgram.CardDesignId"/>, so an upgrade restores the design.
/// </summary>
public interface ICardDesignResolver
{
    /// <summary>Does the business currently have the optional module enabled?</summary>
    Task<bool> BusinessHasCustomCardDesignAsync(Guid businessId);

    /// <summary>The platform default design row, or null when it has not been seeded.</summary>
    Task<CardDesign?> GetDefaultDesignAsync();

    /// <summary>Resolves the design that must be used for the given program.</summary>
    Task<ResolvedCardDesign> ResolveForProgramAsync(LoyaltyProgram program);
}

/// <summary>
/// The outcome of <see cref="ICardDesignResolver.ResolveForProgramAsync"/>.
/// </summary>
public sealed class ResolvedCardDesign
{
    /// <summary>The sanitized template body that must be rendered.</summary>
    public string Template { get; init; } = string.Empty;

    /// <summary>The design id (null when falling back to the in-code default).</summary>
    public Guid? DesignId { get; init; }

    /// <summary>Design display name surfaced to the UI.</summary>
    public string DesignName { get; init; } = DefaultCardTemplate.Name;

    /// <summary>True when the platform default is being used.</summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// True when a business design was selected but could not be used
    /// (entitlement revoked, design deactivated or deleted) and the default was
    /// substituted instead. Surfaced for diagnostics only.
    /// </summary>
    public bool IsFallback { get; init; }

    /// <summary>Whether the business currently holds the <c>customCardDesign</c> entitlement.</summary>
    public bool BusinessHasEntitlement { get; init; }
}

/// <inheritdoc />
public sealed class CardDesignResolver : ICardDesignResolver
{
    /// <summary>Module key granting business-specific card designs.</summary>
    public const string ModuleKey = "customCardDesign";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IModuleEntitlementService _entitlements;
    private readonly ILogger<CardDesignResolver> _logger;

    public CardDesignResolver(
        IUnitOfWork unitOfWork,
        IModuleEntitlementService entitlements,
        ILogger<CardDesignResolver> logger)
    {
        _unitOfWork = unitOfWork;
        _entitlements = entitlements;
        _logger = logger;
    }

    public Task<bool> BusinessHasCustomCardDesignAsync(Guid businessId) =>
        _entitlements.IsModuleEnabledAsync(businessId, ModuleKey);

    public async Task<CardDesign?> GetDefaultDesignAsync() =>
        await _unitOfWork.CardDesigns.FirstOrDefaultAsync(
            d => d.IsDefault && d.BusinessId == null && d.IsActive);

    public async Task<ResolvedCardDesign> ResolveForProgramAsync(LoyaltyProgram program)
    {
        var hasEntitlement = await BusinessHasCustomCardDesignAsync(program.BusinessId);

        if (program.CardDesignId.HasValue && hasEntitlement)
        {
            // The design may belong to the business or be the global default.
            var selected = await _unitOfWork.CardDesigns.FirstOrDefaultAsync(d =>
                d.Id == program.CardDesignId.Value &&
                d.IsActive &&
                (d.BusinessId == null || d.BusinessId == program.BusinessId));

            if (selected != null)
            {
                return new ResolvedCardDesign
                {
                    Template = selected.HtmlTemplate,
                    DesignId = selected.Id,
                    DesignName = selected.Name,
                    IsDefault = selected.IsDefault,
                    BusinessHasEntitlement = true
                };
            }

            // Selected design vanished (deleted / deactivated). Fall through to the
            // default rather than failing the customer's card.
            _logger.LogWarning(
                "Card design {CardDesignId} selected by program {ProgramId} is unavailable; falling back to the default design.",
                program.CardDesignId, program.Id);
        }

        var fallback = await GetDefaultDesignAsync();
        return new ResolvedCardDesign
        {
            Template = fallback?.HtmlTemplate ?? DefaultCardTemplate.Html,
            DesignId = fallback?.Id,
            DesignName = fallback?.Name ?? DefaultCardTemplate.Name,
            IsDefault = true,
            IsFallback = program.CardDesignId.HasValue,
            BusinessHasEntitlement = hasEntitlement
        };
    }

    /// <summary>
    /// Renders a resolved design through the single shared pipeline. There is no
    /// other way to turn a design into HTML in this codebase.
    /// </summary>
    public static string Render(ResolvedCardDesign design, CardTemplateRenderer.CardRenderContext context) =>
        CardTemplateRenderer.Render(design.Template, context);
}