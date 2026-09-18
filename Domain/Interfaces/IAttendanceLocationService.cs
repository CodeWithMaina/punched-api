using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Owner-facing lifecycle for attendance locations and their printed QR
/// credentials (plan §8). Every method is scoped server-side to the owner's
/// business — no business id is ever accepted from the client, and another
/// organisation's id answers <c>NOT_FOUND</c> (never <c>FORBIDDEN</c>), the
/// non-enumerable answer used across the rest of the API (§10.2).
/// </summary>
public interface IAttendanceLocationService
{
    Task<ApiResponse<AttendanceLocationDetailResponse>> GetLocationAsync(Guid ownerUserId, Guid locationId);

    Task<ApiResponse<List<AttendanceLocationSummaryResponse>>> ListLocationsAsync(Guid ownerUserId, bool includeInactive = false);

    Task<ApiResponse<AttendanceLocationDetailResponse>> CreateLocationAsync(Guid ownerUserId, CreateAttendanceLocationRequest request);

    Task<ApiResponse<AttendanceLocationDetailResponse>> UpdateLocationAsync(Guid ownerUserId, Guid locationId, UpdateAttendanceLocationRequest request);

    /// <summary>Deletes a location. Refused with <c>LOCATION_HAS_HISTORY</c> while events reference it.</summary>
    Task<ApiResponse<bool>> DeleteLocationAsync(Guid ownerUserId, Guid locationId);

    /// <summary>Revokes any Active credential for the location, then inserts a new Active one. Returns the raw token ONCE.</summary>
    Task<ApiResponse<AttendanceQrCredentialResponse>> MintQrAsync(Guid ownerUserId, Guid locationId);

    /// <summary>Same as <see cref="MintQrAsync"/> but audited as <c>QR_REGENERATED</c> (distinct UI copy).</summary>
    Task<ApiResponse<AttendanceQrCredentialResponse>> RotateQrAsync(Guid ownerUserId, Guid locationId);

    /// <summary>Revokes the Active credential, leaving the location unusable until a new one is minted.</summary>
    Task<ApiResponse<bool>> RevokeQrAsync(Guid ownerUserId, Guid locationId);
}