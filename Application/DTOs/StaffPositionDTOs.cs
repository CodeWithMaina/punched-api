using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ===============================================================
//  STAFF POSITIONS (job titles) - DTOs
// ===============================================================

/// <summary>
/// A business-managed job title. Purely descriptive: a position grants no
/// permission and no service eligibility.
/// </summary>
public class StaffPositionResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>Owner-controlled display order (ascending).</summary>
    [JsonPropertyName("sortOrder")] public int SortOrder { get; set; }

    /// <summary>
    /// False keeps existing assignments and their history but removes the label
    /// from assignment pickers.
    /// </summary>
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }

    /// <summary>How many active staff currently hold this title.</summary>
    [JsonPropertyName("assignedStaffCount")] public int AssignedStaffCount { get; set; }

    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }
}

/// <summary>Creates a position for the owner business.</summary>
public class CreateStaffPositionRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>Omitted appends to the end of the current order.</summary>
    [JsonPropertyName("sortOrder")] public int? SortOrder { get; set; }

    [JsonPropertyName("isActive")] public bool? IsActive { get; set; }
}

/// <summary>Partially updates a position. Only provided fields are applied.</summary>
public class UpdateStaffPositionRequest
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("isActive")] public bool? IsActive { get; set; }
}

/// <summary>Deterministic reordering of the business position labels.</summary>
public class ReorderStaffPositionsRequest
{
    [JsonPropertyName("orderedIds")] public Guid[] OrderedIds { get; set; } = [];
}

/// <summary>
/// Assigns or clears a staff member position. A null/omitted
/// <see cref="PositionId"/> with <see cref="Clear"/> = true removes the title.
/// </summary>
public class AssignStaffPositionRequest
{
    [JsonPropertyName("positionId")] public Guid? PositionId { get; set; }

    /// <summary>Explicitly removes the current position. Separate from PositionId so
    /// "leave as is" and "clear" are distinguishable.</summary>
    [JsonPropertyName("clear")] public bool? Clear { get; set; }

    [JsonPropertyName("note")] public string? Note { get; set; }
}

/// <summary>The current position of one staff member.</summary>
public class StaffPositionAssignmentResponse
{
    [JsonPropertyName("staffUserId")] public Guid StaffUserId { get; set; }
    [JsonPropertyName("staffName")] public string? StaffName { get; set; }
    [JsonPropertyName("positionId")] public Guid? PositionId { get; set; }
    [JsonPropertyName("positionName")] public string? PositionName { get; set; }
    [JsonPropertyName("positionIsActive")] public bool? PositionIsActive { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One entry of a staff member position history. <see cref="PositionName"/> is a
/// snapshot, so it stays readable even after the position is renamed or deleted.
/// </summary>
public class StaffPositionHistoryResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("staffUserId")] public Guid StaffUserId { get; set; }
    [JsonPropertyName("staffName")] public string? StaffName { get; set; }
    [JsonPropertyName("positionId")] public Guid? PositionId { get; set; }
    [JsonPropertyName("positionName")] public string? PositionName { get; set; }
    [JsonPropertyName("changedByUserId")] public Guid? ChangedByUserId { get; set; }
    [JsonPropertyName("changedAt")] public DateTime ChangedAt { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}
