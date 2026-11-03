using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Owner-side staff management: one member's profile, their services, their
/// recurring working week, dated time off and their active/suspended state.
///
/// <para><b>Business scope is resolved server-side</b> from the caller's active
/// membership (or the active tenant), never from a client-supplied business id,
/// so an id belonging to another business resolves to "not found" rather than
/// being acted on.</para>
///
/// <para><b>Nothing here deletes a person.</b> Deactivation suspends the
/// membership, which stops new bookings and new attendance while every historical
/// appointment, attendance event and commission entry stays readable.</para>
/// </summary>
public interface IStaffManagementService
{
    /// <summary>Full management profile of one staff member of the caller's business.</summary>
    Task<ApiResponse<StaffDetailResponse>> GetStaffDetailAsync(Guid callerUserId, string role, Guid staffUserId);

    /// <summary>Partially updates a staff member. An omitted field is never touched.</summary>
    Task<ApiResponse<StaffDetailResponse>> UpdateStaffAsync(
        Guid callerUserId, string role, Guid staffUserId, UpdateStaffRequest request);

    /// <summary>Activates or suspends a membership. History is preserved either way.</summary>
    Task<ApiResponse<StaffStatusResponse>> SetStaffActiveAsync(
        Guid callerUserId, string role, Guid staffUserId, bool active);

    /// <summary>The services a staff member is assigned to deliver.</summary>
    Task<ApiResponse<List<StaffServiceLinkResponse>>> GetStaffServicesAsync(
        Guid callerUserId, string role, Guid staffUserId);

    /// <summary>
    /// Replaces the set of services a staff member delivers. The array is the whole
    /// set, so add and remove are one idempotent write inside a transaction.
    /// </summary>
    Task<ApiResponse<List<StaffServiceLinkResponse>>> SetStaffServicesAsync(
        Guid callerUserId, string role, Guid staffUserId, SetStaffServicesRequest request);

    /// <summary>The recurring working week, always seven entries in week order.</summary>
    Task<ApiResponse<List<StaffAvailabilityDayResponse>>> GetStaffAvailabilityAsync(
        Guid callerUserId, string role, Guid staffUserId);

    /// <summary>Saves the recurring working week.</summary>
    Task<ApiResponse<List<StaffAvailabilityDayResponse>>> SetStaffAvailabilityAsync(
        Guid callerUserId, string role, Guid staffUserId, SetStaffAvailabilityRequest request);

    /// <summary>
    /// Clears the recurring working week, so the member's hours fall back to their
    /// dated roster and then to the business's own booking window. Without this an
    /// owner who saved a week could never return to inheriting business hours.
    /// </summary>
    Task<ApiResponse<List<StaffAvailabilityDayResponse>>> ClearStaffAvailabilityAsync(
        Guid callerUserId, string role, Guid staffUserId);

    /// <summary>Adds dated time off, which overrides the recurring week for those dates.</summary>
    Task<ApiResponse<List<StaffTimeOffResponse>>> AddStaffTimeOffAsync(
        Guid callerUserId, string role, Guid staffUserId, AddStaffTimeOffRequest request);

    /// <summary>Removes a dated time-off entry, returning the day to its pattern.</summary>
    Task<ApiResponse<List<StaffTimeOffResponse>>> RemoveStaffTimeOffAsync(
        Guid callerUserId, string role, Guid staffUserId, DateOnly date);
}
