using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  CARD DESIGN DTOs (reusable HTML templates per business)
// ═══════════════════════════════════════════════════════════════

/// <summary>POST /v1/card-designs/me request body.</summary>
public class CreateCardDesignRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Card Design";

    /// <summary>Raw (unsanitized) HTML template. Sanitized server-side before storage.</summary>
    [JsonPropertyName("htmlTemplate")]
    public string HtmlTemplate { get; set; } = string.Empty;
}

/// <summary>PUT /v1/card-designs/me/{id} request body.</summary>
public class UpdateCardDesignRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("htmlTemplate")]
    public string? HtmlTemplate { get; set; }

    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }
}

/// <summary>Card design response (template included — it is the owner's own content).</summary>
public class CardDesignResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("htmlTemplate")]
    public string HtmlTemplate { get; set; } = string.Empty;

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    /// <summary>Number of stamp cards currently using this design.</summary>
    [JsonPropertyName("assignedCards")]
    public int AssignedCards { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>POST /v1/card-designs/me/preview request body.</summary>
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

    [JsonPropertyName("totalStamps")]
    public int? TotalStamps { get; set; }

    [JsonPropertyName("completedStamps")]
    public int? CompletedStamps { get; set; }
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

    [JsonPropertyName("variables")]
    public List<string> Variables { get; set; } = new();
}