using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Business-scoped staff positions (job titles) and their history.
///
/// <para>Positions are <b>descriptive only</b>: assigning one grants no
/// permission and no service eligibility. Service eligibility stays in
/// <c>staff_services</c>; authority stays in RBAC.</para>
/// </summary>
public interface IStaffPositionService
{
    /// <summary>All positions of the caller's business, in display order.</summary>
    Task<ApiResponse<List<StaffPositionResponse>>> GetPositionsAsync(Guid callerUserId, string role, bool includeInactive = true);

    /// <summary>A single position of the caller's business.</summary>
    Task<ApiResponse<StaffPositionResponse>> GetPositionAsync(Guid callerUserId, string role, Guid positionId);

    /// <summary>Creates a position. Names are unique per business (case-insensitive).</summary>
    Task<ApiResponse<StaffPositionResponse>> CreatePositionAsync(Guid callerUserId, string role, CreateStaffPositionRequest request);

    /// <summary>Partially updates a position (rename, description, activate/deactivate).</summary>
    Task<ApiResponse<StaffPositionResponse>> UpdatePositionAsync(Guid callerUserId, string role, Guid positionId, UpdateStaffPositionRequest request);

    /// <summary>Deterministically rewrites the display order.</summary>
    Task<ApiResponse<List<StaffPositionResponse>>> ReorderPositionsAsync(Guid callerUserId, string role, ReorderStaffPositionsRequest request);

    /// <summary>
    /// Deletes a position that no staff member currently holds. A position with
    /// current holders is refused (POSITION_IN_USE); history rows survive either
    /// way because the label is snapshotted.
    /// </summary>
    Task<ApiResponse<bool>> DeletePositionAsync(Guid callerUserId, string role, Guid positionId);

    /// <summary>The current position of one staff member of the caller's business.</summary>
    Task<ApiResponse<StaffPositionAssignmentResponse>> GetStaffPositionAsync(Guid callerUserId, string role, Guid staffUserId);

    /// <summary>
    /// Assigns, changes or clears a staff member position. Always appends a
    /// history row, so the title a person held is never lost.
    /// </summary>
    Task<ApiResponse<StaffPositionAssignmentResponse>> AssignStaffPositionAsync(
        Guid callerUserId, string role, Guid staffUserId, AssignStaffPositionRequest request);

    /// <summary>
    /// The position history of one staff member (or of the whole business when
    /// <paramref name="staffUserId"/> is null), newest first.
    /// </summary>
    Task<ApiResponse<List<StaffPositionHistoryResponse>>> GetPositionHistoryAsync(
        Guid callerUserId, string role, Guid? staffUserId);
}
