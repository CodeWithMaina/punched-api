using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  CARD DESIGN DTOs
//
//  Admin-authored HTML templates. Business owners may LIST the designs
//  available to them and SELECT one for a loyalty program; they cannot author
//  HTML (plan §15).
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// POST /v1/admin/businesses/{businessId}/card-designs request body.
/// The HTML is sanitized server-side before storage.
/// </summary>
public class CreateCardDesignRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Card Design";

    /// <summary>Raw (unsanitized) HTML template. Sanitized server-side before storage.</summary>
    [JsonPropertyName("htmlTemplate")]
    public string HtmlTemplate { get; set; } = string.Empty;
}

/// <summary>PUT /v1/admin/card-designs/{id} request body.</summary>
public class UpdateCardDesignRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("htmlTemplate")]
    public string? HtmlTemplate { get; set; }

    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }
}

/// <summary>
/// Admin card design projection — includes the template body (the admin owns it).
/// </summary>
public class CardDesignResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>Null for the platform default design.</summary>
    [JsonPropertyName("businessId")]
    public Guid? BusinessId { get; set; }

    /// <summary>Owning business display name (null for the platform default).</summary>
    [JsonPropertyName("businessName")]
    public string? BusinessName { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("htmlTemplate")]
    public string HtmlTemplate { get; set; } = string.Empty;

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    /// <summary>True for the single platform-wide fallback design.</summary>
    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    /// <summary>Number of loyalty programs currently selecting this design.</summary>
    [JsonPropertyName("assignedPrograms")]
    public int AssignedPrograms { get; set; }

    /// <summary>Number of stamp cards currently using this design.</summary>
    [JsonPropertyName("assignedCards")]
    public int AssignedCards { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A design available to a business for selection (plan §17). Deliberately
/// minimal: enough to render the visual selector, without exposing the raw
/// template, other tenants, or internal identifiers.
/// </summary>
public class AvailableCardDesignResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>True for the platform default — always offered, always available.</summary>
    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    /// <summary>
    /// The design rendered with safe sample data through the production
    /// pipeline, so the selector and the customer's real card always match.
    /// </summary>
    [JsonPropertyName("previewHtml")]
    public string PreviewHtml { get; set; } = string.Empty;
}

/// <summary>
/// POST /v1/card-designs/me/preview request body (business owner) — always
/// allowed, so a business can see the default card it already has.
/// </summary>
public class PreviewCardDesignRequest
{
    /// <summary>Raw HTML to sanitize + render (preview of unsaved edits).</summary>
    [JsonPropertyName("htmlTemplate")]
    public string? HtmlTemplate { get; set; }

    /// <summary>Or preview an existing saved design.</summary>
    [JsonPropertyName("cardDesignId")]
    public Guid? CardDesignId { get; set; }

    /// <summary>Sample data overrides (all optional — realistic defaults used).</summary>
    [JsonPropertyName("businessName")]
    public string? BusinessName { get; set; }

    [JsonPropertyName("customerName")]
    public string? CustomerName { get; set; }

    [JsonPropertyName("cardName")]
    public string? CardName { get; set; }

    [JsonPropertyName("rewardName")]
    public string? RewardName { get; set; }

    [JsonPropertyName("programName")]
    public string? ProgramName { get; set; }

    [JsonPropertyName("totalStamps")]
    public int? TotalStamps { get; set; }

    [JsonPropertyName("completedStamps")]
    public int? CompletedStamps { get; set; }
}

/// <summary>
/// POST /v1/admin/card-designs/preview request body. Lets an Admin render a card
/// for a chosen business **before** the design row is created (plan §6, §8).
/// </summary>
public class AdminPreviewCardDesignRequest : PreviewCardDesignRequest
{
    /// <summary>Business whose branding the sample card should use (optional).</summary>
    [JsonPropertyName("businessId")]
    public Guid? BusinessId { get; set; }
}

/// <summary>Preview response — sanitized template + rendered HTML + variable reference.</summary>
public class PreviewCardDesignResponse
{
    /// <summary>The sanitized template (what would be stored).</summary>
    [JsonPropertyName("sanitizedTemplate")]
    public string SanitizedTemplate { get; set; } = string.Empty;

    /// <summary>Fully rendered HTML (same pipeline as production). Render in a sandboxed iframe.</summary>
    [JsonPropertyName("renderedHtml")]
    public string RenderedHtml { get; set; } = string.Empty;

    /// <summary>Supported template variables — documented for authors.</summary>
    [JsonPropertyName("variables")]
    public List<string> Variables { get; set; } = new();
}