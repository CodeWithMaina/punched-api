using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  STAMP CARD DTOs
//  Stamp cards are children of campaigns (LoyaltyProgram).
// ═══════════════════════════════════════════════════════════════

/// <summary>POST /v1/programs/me/{programId}/stamp-cards request body.</summary>
public class CreateStampCardRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Stamp Card";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("stampsRequired")]
    public int StampsRequired { get; set; } = 10;

    [JsonPropertyName("rewardDescription")]
    public string RewardDescription { get; set; } = string.Empty;

    [JsonPropertyName("rewardValue")]
    public decimal RewardValue { get; set; }

    [JsonPropertyName("cardDesignId")]
    public Guid? CardDesignId { get; set; }

    /// <summary>Initial lifecycle status — draft | active | inactive.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

/// <summary>PUT /v1/stamp-cards/me/{id} request body.</summary>
public class UpdateStampCardRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("stampsRequired")]
    public int? StampsRequired { get; set; }

    [JsonPropertyName("rewardDescription")]
    public string? RewardDescription { get; set; }

    [JsonPropertyName("rewardValue")]
    public decimal? RewardValue { get; set; }

    /// <summary>Assign (or clear with clearCardDesign=true) the card design.</summary>
    [JsonPropertyName("cardDesignId")]
    public Guid? CardDesignId { get; set; }

    [JsonPropertyName("clearCardDesign")]
    public bool ClearCardDesign { get; set; }
}

/// <summary>PATCH /v1/stamp-cards/me/{id}/status request body.</summary>
public class UpdateStampCardStatusRequest
{
    /// <summary>draft | active | inactive | archived.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

/// <summary>GET stamp-card responses (list + detail).</summary>
public class StampCardResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("programId")]
    public Guid ProgramId { get; set; }

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("stampsRequired")]
    public int StampsRequired { get; set; }

    [JsonPropertyName("rewardDescription")]
    public string RewardDescription { get; set; } = string.Empty;

    [JsonPropertyName("rewardValue")]
    public decimal RewardValue { get; set; }

    /// <summary>draft | active | inactive | archived.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "draft";

    [JsonPropertyName("cardDesignId")]
    public Guid? CardDesignId { get; set; }

    [JsonPropertyName("cardDesignName")]
    public string? CardDesignName { get; set; }

    /// <summary>Basic usage info: customer cards enrolled in the parent campaign.</summary>
    [JsonPropertyName("enrolledCustomers")]
    public int EnrolledCustomers { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>POST /v1/stamp-cards/me/{id}/duplicate — optional new name.</summary>
public class DuplicateStampCardRequest
{
    [JsonPropertyName("newName")]
    public string? NewName { get; set; }
}