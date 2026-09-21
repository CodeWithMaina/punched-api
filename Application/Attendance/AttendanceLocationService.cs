using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.Attendance.Verification;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Attendance;

/// <summary>
/// Owner-scoped lifecycle for attendance locations and their printed QR
/// credentials (plan §8). Locations are server-scoped to the owner's business
/// (a client-supplied id can only ever be compared, never trusted); QR
/// credentials are never returned except once, at mint/rotate time.
/// </summary>
public partial class AttendanceLocationService : IAttendanceLocationService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<AttendanceLocationService> _logger;

    public AttendanceLocationService(ApplicationDbContext context, ILogger<AttendanceLocationService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<ApiResponse<AttendanceLocationDetailResponse>> GetLocationAsync(Guid ownerUserId, Guid locationId)
    {
        var businessId = await ResolveBusinessIdAsync(ownerUserId);
        if (businessId == null)
            return ApiResponse<AttendanceLocationDetailResponse>.Fail("NOT_FOUND", "Business not found.");

        // Business-filtered load: another organisation's id answers NOT_FOUND (§10.2).
        var location = await _context.AttendanceLocations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == locationId && l.BusinessId == businessId);

        if (location == null)
            return ApiResponse<AttendanceLocationDetailResponse>.Fail("NOT_FOUND", "Location not found.");

        var state = await GetCredentialStateAsync(businessId.Value, location.Id);
        return ApiResponse<AttendanceLocationDetailResponse>.Ok(
            MapDetail(location, state.HasActiveCredential, state.LastUsedAt));
    }

    public async Task<ApiResponse<List<AttendanceLocationSummaryResponse>>> ListLocationsAsync(
        Guid ownerUserId, bool includeInactive = false)
    {
        var businessId = await ResolveBusinessIdAsync(ownerUserId);
        if (businessId == null)
            return ApiResponse<List<AttendanceLocationSummaryResponse>>.Fail("NOT_FOUND", "Business not found.");

        var locations = await _context.AttendanceLocations
            .AsNoTracking()
            .Where(l => l.BusinessId == businessId && (includeInactive || l.IsActive))
            .OrderBy(l => l.Name)
            .ToListAsync();

        var states = await GetCredentialStatesAsync(businessId.Value, locations.Select(l => l.Id).ToList());

        var items = locations.Select(location =>
        {
            states.TryGetValue(location.Id, out var state);
            return MapSummary(location, state.HasActiveCredential, state.LastUsedAt);
        }).ToList();

        return ApiResponse<List<AttendanceLocationSummaryResponse>>.Ok(items);
    }

    public async Task<ApiResponse<AttendanceLocationDetailResponse>> CreateLocationAsync(
        Guid ownerUserId, CreateAttendanceLocationRequest request)
    {
        var businessId = await ResolveBusinessIdAsync(ownerUserId);
        if (businessId == null)
            return ApiResponse<AttendanceLocationDetailResponse>.Fail("NOT_FOUND", "Business not found.");

        var name = NormalizeName(request.Name);
        if (name.Length == 0)
            return ApiResponse<AttendanceLocationDetailResponse>.Fail("LOCATION_NAME_EXISTS", "Location name is required.");

        // D8: case-insensitive per-business uniqueness is enforced in the
        // service layer, so SQLite tests and PostgreSQL prod agree (the
        // codebase uses no functional/lower() indexes anywhere).
        if (await NameExistsAsync(businessId.Value, name))
            return ApiResponse<AttendanceLocationDetailResponse>.Fail(
                "LOCATION_NAME_EXISTS", "A location with that name already exists for this business.");

        var location = new AttendanceLocation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId.Value,
            Name = name,
            Description = NormalizeOptional(request.Description),
            IsActive = true,
            CreatedByUserId = ownerUserId,
            CreatedAt = DateTime.UtcNow,
        };

        // No credential is auto-minted here on purpose: a raw token that is
        // never returned would be an unusable orphan that also occupies the
        // one-active slot, forcing a rotation before the owner could print
        // anything. Minting is explicit (MintQrAsync) and returns the token
        // once (plan §8.6, §13.3).
        await using var tx = await _context.Database.BeginTransactionAsync();
        _context.AttendanceLocations.Add(location);
        await _context.SaveChangesAsync();

        await AuditAsync(AttendanceAuditActions.LocationCreated, businessId.Value, ownerUserId,
            "POST /v1/businesses/me/attendance/locations", locationId: location.Id);

        await tx.CommitAsync();

        return ApiResponse<AttendanceLocationDetailResponse>.Ok(MapDetail(location, false, null));
    }

    public async Task<ApiResponse<AttendanceLocationDetailResponse>> UpdateLocationAsync(
        Guid ownerUserId, Guid locationId, UpdateAttendanceLocationRequest request)
    {
        var businessId = await ResolveBusinessIdAsync(ownerUserId);
        if (businessId == null)
            return ApiResponse<AttendanceLocationDetailResponse>.Fail("NOT_FOUND", "Business not found.");

        var location = await _context.AttendanceLocations
            .FirstOrDefaultAsync(l => l.Id == locationId && l.BusinessId == businessId);
        if (location == null)
            return ApiResponse<AttendanceLocationDetailResponse>.Fail("NOT_FOUND", "Location not found.");

        if (request.Name != null)
        {
            var name = NormalizeName(request.Name);
            if (name.Length == 0)
                return ApiResponse<AttendanceLocationDetailResponse>.Fail("LOCATION_NAME_EXISTS", "Location name is required.");

            if (await NameExistsAsync(businessId.Value, name, exceptLocationId: locationId))
                return ApiResponse<AttendanceLocationDetailResponse>.Fail(
                    "LOCATION_NAME_EXISTS", "A location with that name already exists for this business.");

            location.Name = name;
        }

        if (request.Description != null) location.Description = NormalizeOptional(request.Description);

        var deactivating = false;
        if (request.IsActive.HasValue && request.IsActive.Value != location.IsActive)
        {
            // Deactivating blocks new scans here while all history is retained (§8.4).
            deactivating = location.IsActive && !request.IsActive.Value;
            location.IsActive = request.IsActive.Value;
        }

        await using var tx = await _context.Database.BeginTransactionAsync();
        await _context.SaveChangesAsync();

        await AuditAsync(deactivating ? AttendanceAuditActions.LocationDeactivated : AttendanceAuditActions.LocationUpdated,
            businessId.Value, ownerUserId, "PUT /v1/businesses/me/attendance/locations/{id}",
            locationId: location.Id, details: new { isActive = location.IsActive, deactivating });

        await tx.CommitAsync();

        var state = await GetCredentialStateAsync(businessId.Value, location.Id);
        return ApiResponse<AttendanceLocationDetailResponse>.Ok(
            MapDetail(location, state.HasActiveCredential, state.LastUsedAt));
    }

    public async Task<ApiResponse<bool>> DeleteLocationAsync(Guid ownerUserId, Guid locationId)
    {
        var businessId = await ResolveBusinessIdAsync(ownerUserId);
        if (businessId == null) return ApiResponse<bool>.Fail("NOT_FOUND", "Business not found.");

        var location = await _context.AttendanceLocations
            .FirstOrDefaultAsync(l => l.Id == locationId && l.BusinessId == businessId);
        if (location == null) return ApiResponse<bool>.Fail("NOT_FOUND", "Location not found.");

        // LOCATION_HAS_HISTORY: the ledger is append-only and must never be
        // orphaned, so the UI steers owners to Deactivate instead (§8.4, §15.3).
        var hasHistory = await _context.AttendanceEvents
            .AnyAsync(e => e.AttendanceLocationId == locationId && e.BusinessId == businessId);
        if (hasHistory)
            return ApiResponse<bool>.Fail(
                "LOCATION_HAS_HISTORY", "This location has scan history and cannot be deleted. Deactivate it instead.");

        await using var tx = await _context.Database.BeginTransactionAsync();

        // Deleting a location cascades to its credentials only; event history
        // keeps its own Restrict location FK (Phase 1 configuration).
        _context.AttendanceLocations.Remove(location);
        await _context.SaveChangesAsync();

        await AuditAsync(AttendanceAuditActions.LocationDeleted, businessId.Value, ownerUserId,
            "DELETE /v1/businesses/me/attendance/locations/{id}", locationId: locationId);

        await tx.CommitAsync();

        return ApiResponse<bool>.Ok(true);
    }

    public Task<ApiResponse<AttendanceQrCredentialResponse>> MintQrAsync(Guid ownerUserId, Guid locationId) =>
        UpsertQrAsync(ownerUserId, locationId,
            AttendanceAuditActions.QrCreated, "POST /v1/businesses/me/attendance/locations/{id}/qr");

    public Task<ApiResponse<AttendanceQrCredentialResponse>> RotateQrAsync(Guid ownerUserId, Guid locationId) =>
        UpsertQrAsync(ownerUserId, locationId,
            AttendanceAuditActions.QrRegenerated, "POST /v1/businesses/me/attendance/locations/{id}/qr/regenerate");

    public async Task<ApiResponse<bool>> RevokeQrAsync(Guid ownerUserId, Guid locationId)
    {
        var businessId = await ResolveBusinessIdAsync(ownerUserId);
        if (businessId == null) return ApiResponse<bool>.Fail("NOT_FOUND", "Business not found.");

        var locationExists = await _context.AttendanceLocations
            .AnyAsync(l => l.Id == locationId && l.BusinessId == businessId);
        if (!locationExists) return ApiResponse<bool>.Fail("NOT_FOUND", "Location not found.");

        var credential = await _context.AttendanceQrCredentials
            .FirstOrDefaultAsync(c => c.AttendanceLocationId == locationId
                                      && c.BusinessId == businessId
                                      && c.Status == AttendanceCredentialStatus.Active);
        if (credential == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "No active QR credential for this location.");

        await using var tx = await _context.Database.BeginTransactionAsync();

        // The revoked hash is retained so the superseded sheet scans as
        // QR_REVOKED (actionable) rather than INVALID_QR (§8.4).
        credential.Status = AttendanceCredentialStatus.Revoked;
        credential.RevokedAt = DateTime.UtcNow;
        credential.RevokedByUserId = ownerUserId;
        await _context.SaveChangesAsync();

        await AuditAsync(AttendanceAuditActions.QrRevoked, businessId.Value, ownerUserId,
            "DELETE /v1/businesses/me/attendance/locations/{id}/qr",
            locationId: locationId, credentialId: credential.Id);

        await tx.CommitAsync();

        return ApiResponse<bool>.Ok(true);
    }
    /// <summary>
    /// Mint/rotate in ONE transaction: revoke any Active credential, then insert
    /// the new one. Revoke-then-insert satisfies
    /// <c>ix_attendance_qr_credentials_one_active_per_location</c> at every
    /// instant, so there is never a window with zero or two live codes (§8.4).
    /// The raw token is returned exactly once and never persisted or logged.
    /// </summary>
    private async Task<ApiResponse<AttendanceQrCredentialResponse>> UpsertQrAsync(
        Guid ownerUserId, Guid locationId, string auditAction, string endpoint)
    {
        var businessId = await ResolveBusinessIdAsync(ownerUserId);
        if (businessId == null)
            return ApiResponse<AttendanceQrCredentialResponse>.Fail("NOT_FOUND", "Business not found.");

        var location = await _context.AttendanceLocations
            .FirstOrDefaultAsync(l => l.Id == locationId && l.BusinessId == businessId);
        if (location == null)
            return ApiResponse<AttendanceQrCredentialResponse>.Fail("NOT_FOUND", "Location not found.");

        if (!location.IsActive)
            return ApiResponse<AttendanceQrCredentialResponse>.Fail(
                "LOCATION_INACTIVE", "This attendance point is currently disabled.");

        await using var tx = await _context.Database.BeginTransactionAsync();
        try
        {
            var now = DateTime.UtcNow;

            var superseded = await _context.AttendanceQrCredentials
                .Where(c => c.AttendanceLocationId == locationId
                            && c.BusinessId == businessId
                            && c.Status == AttendanceCredentialStatus.Active)
                .ToListAsync();

            foreach (var stale in superseded)
            {
                stale.Status = AttendanceCredentialStatus.Revoked;
                stale.RevokedAt = now;
                stale.RevokedByUserId = ownerUserId;
            }

            // Flush the revocations first: the partial unique index only allows
            // the insert once no Active row remains for this location.
            await _context.SaveChangesAsync();

            var rawToken = AttendanceTokenFactory.CreatePayload();

            var credential = new AttendanceQrCredential
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId.Value,
                AttendanceLocationId = locationId,
                TokenHash = AttendanceTokenFactory.HashToken(rawToken),
                Status = AttendanceCredentialStatus.Active,
                CreatedByUserId = ownerUserId,
                CreatedAt = now,
            };

            _context.AttendanceQrCredentials.Add(credential);
            await _context.SaveChangesAsync();

            await AuditAsync(auditAction, businessId.Value, ownerUserId, endpoint,
                locationId: locationId, credentialId: credential.Id,
                details: new { supersededCount = superseded.Count });

            await tx.CommitAsync();

            return ApiResponse<AttendanceQrCredentialResponse>.Ok(new AttendanceQrCredentialResponse
            {
                CredentialId = credential.Id,
                LocationId = locationId,
                Token = rawToken,
                Status = AttendanceVerification.WireValue(credential.Status),
                CreatedAt = credential.CreatedAt,
            });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex,
                "Attendance QR mint/rotate failed for location {LocationId} business {BusinessId}",
                locationId, businessId.Value);
            throw;
        }
    }
}