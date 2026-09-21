using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Card design lifecycle and availability.
///
/// Responsibility split (plan §15):
///  • <b>Admin</b> authors and manages designs (see the Admin* methods).
///  • <b>Subscription</b> decides which businesses may <i>use</i> custom designs
///    (<see cref="ValidateSelectionAsync"/> + <see cref="GetAvailableDesignsAsync"/>).
///
/// Creating HTML designs is an admin capability: a business subscription never
/// grants it, it only grants the right to select from the designs an Admin
/// published for that business.
/// </summary>
public interface ICardDesignService
{
    /// <summary>Module key that gates business use of custom designs.</summary>
    const string ModuleKey = "customCardDesign";

    // ── Admin ───────────────────────────────────────────────

    /// <summary>All designs authored for the given business (never another tenant's).</summary>
    Task<ApiResponse<List<CardDesignResponse>>> GetDesignsForBusinessAsync(Guid businessId);

    /// <summary>Creates a business design. HTML is validated + sanitized before storage.</summary>
    Task<ApiResponse<CardDesignResponse>> CreateDesignAsync(
        Guid businessId, CreateCardDesignRequest request, Guid? adminUserId);

    /// <summary>Updates a business design (re-sanitizes any new HTML).</summary>
    Task<ApiResponse<CardDesignResponse>> UpdateDesignAsync(
        Guid designId, UpdateCardDesignRequest request, Guid? adminUserId);

    /// <summary>
    /// Deactivates a design without deleting it. Existing selections stop
    /// rendering it (they fall back to the default) and become reversible again
    /// if the design is reactivated.
    /// </summary>
    Task<ApiResponse<CardDesignResponse>> SetDesignActiveAsync(Guid designId, bool isActive);

    /// <summary>
    /// Hard-deletes a design. Refused while any program or stamp card still
    /// references it — deactivate instead so the selection survives (plan §12).
    /// </summary>
    Task<ApiResponse<bool>> DeleteDesignAsync(Guid designId);

    /// <summary>Renders a card for review before (or after) the row is persisted.</summary>
    Task<ApiResponse<PreviewCardDesignResponse>> PreviewAsync(AdminPreviewCardDesignRequest request);

    // ── Business (selection / availability) ─────────────────

    /// <summary>
    /// The designs the caller's business may choose from: the global default
    /// tooling always plus the business's own active designs, and only those,
    /// when the <c>customCardDesign</c> module is enabled (plan §16–17).
    /// </summary>
    Task<ApiResponse<List<AvailableCardDesignResponse>>> GetAvailableDesignsAsync(Guid ownerId);

    /// <summary>
    /// Previews a design for a business owner. Always allowed so loyalty-only
    /// businesses can still see the default card.
    /// </summary>
    Task<ApiResponse<PreviewCardDesignResponse>> PreviewForBusinessAsync(
        Guid ownerId, PreviewCardDesignRequest request);

    /// <summary>
    /// Validates that a program/stamp-card may select <paramref name="cardDesignId"/>.
    ///
    /// Rules (plan §5, §16):
    ///  • <c>null</c> ⇒ always valid (means "use the default").
    ///  • The platform default ⇒ always valid.
    ///  • A business design ⇒ must belong to <paramref name="businessId"/>,
    ///    must be active, and the business must hold the entitlement.
    /// </summary>
    Task<CardDesignSelectionCheck> ValidateSelectionAsync(Guid businessId, Guid? cardDesignId);
}

/// <summary>Outcome of a card-design selection validation.</summary>
public sealed record CardDesignSelectionCheck(bool IsValid, string? ErrorCode, string? ErrorMessage)
{
    /// <summary>The selection is allowed (or null ⇒ default).</summary>
    public static readonly CardDesignSelectionCheck Valid = new(true, null, null);

    public static CardDesignSelectionCheck Invalid(string code, string message) => new(false, code, message);
}