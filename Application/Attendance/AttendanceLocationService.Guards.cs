using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Attendance;

/// <summary>
/// Actor/scope guards, credential-state projection and mappers for
/// <see cref="AttendanceLocationService"/> (the
/// <c>LoyaltyStampingService.Guards.cs</c> partial-file precedent).
/// </summary>
public partial class AttendanceLocationService
{
    /// <summary>
    /// Resolves the owner's business id server-side: the JWT-derived user id
    /// must be a live Business-role user that owns a live business. No client
    /// input participates (§10.2). Null ⇒ the caller is not a resolvable owner.
    /// </summary>
    private async Task<Guid?> ResolveBusinessIdAsync(Guid ownerUserId)
    {
        var owner = await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == ownerUserId && !u.IsDeleted);

        if (owner?.Role != UserRole.Business) return null;

        return await _context.Businesses
            .AsNoTracking()
            .Where(b => b.OwnerId == ownerUserId && !b.IsDeleted)
            .Select(b => b.Id)
            .FirstOrDefaultAsync();
    }

    private static string NormalizeName(string? name) => (name ?? string.Empty).Trim();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Case-insensitive per-business name uniqueness (decision D8).</summary>
    private Task<bool> NameExistsAsync(Guid businessId, string name, Guid? exceptLocationId = null)
    {
        var lowered = name.ToLower();
        return _context.AttendanceLocations.AnyAsync(l =>
            l.BusinessId == businessId &&
            (exceptLocationId == null || l.Id != exceptLocationId) &&
            l.Name.ToLower() == lowered);
    }

    private async Task<(bool HasActiveCredential, DateTime? LastUsedAt)> GetCredentialStateAsync(
        Guid businessId, Guid locationId)
    {
        var states = await GetCredentialStatesAsync(businessId, new List<Guid> { locationId });
        return states.TryGetValue(locationId, out var state) ? state : (false, null);
    }

    /// <summary>
    /// "Has a live QR?" plus "last scanned when?" for the owner's location list.
    /// The token itself is never selected, let alone returned (§13.3).
    /// </summary>
    private async Task<Dictionary<Guid, (bool HasActiveCredential, DateTime? LastUsedAt)>> GetCredentialStatesAsync(
        Guid businessId, IReadOnlyCollection<Guid> locationIds)
    {
        if (locationIds.Count == 0) return new Dictionary<Guid, (bool, DateTime?)>();

        var ids = locationIds.ToList();

        var credentials = await _context.AttendanceQrCredentials
            .AsNoTracking()
            .Where(c => c.BusinessId == businessId && ids.Contains(c.AttendanceLocationId))
            .Select(c => new { c.AttendanceLocationId, c.Status, c.LastUsedAt })
            .ToListAsync();

        return credentials
            .GroupBy(c => c.AttendanceLocationId)
            .ToDictionary(
                g => g.Key,
                g => (
                    HasActiveCredential: g.Any(c => c.Status == AttendanceCredentialStatus.Active),
                    LastUsedAt: g.Where(c => c.LastUsedAt.HasValue).Select(c => c.LastUsedAt).Max()));
    }

    /// <summary>
    /// Writes the business-meaningful audit row (the <c>StampService</c>
    /// pattern). The action name lives inside <c>DetailsJson</c>; the raw token
    /// and its hash are NEVER written, only the credential id (§8.3, §19.3).
    /// </summary>
    private async Task AuditAsync(
        string action,
        Guid businessId,
        Guid userId,
        string endpoint,
        Guid? locationId = null,
        Guid? credentialId = null,
        object? details = null)
    {
        await _context.ApiEventLogs.AddAsync(new ApiEventLog
        {
            Id = Guid.NewGuid(),
            TenantId = businessId,
            UserId = userId,
            Endpoint = endpoint,
            Method = endpoint.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "POST",
            StatusCode = 200,
            CreatedAt = DateTime.UtcNow,
            DetailsJson = JsonSerializer.Serialize(new
            {
                action,
                actor = userId,
                locationId,
                credentialId,
                details,
            }),
        });

        await _context.SaveChangesAsync();
    }

    private static AttendanceLocationDetailResponse MapDetail(
        AttendanceLocation location, bool hasActiveCredential, DateTime? lastUsedAt) => new()
    {
        Id = location.Id,
        BusinessId = location.BusinessId,
        Name = location.Name,
        Description = location.Description,
        IsActive = location.IsActive,
        HasActiveCredential = hasActiveCredential,
        LastUsedAt = lastUsedAt,
        CreatedAt = location.CreatedAt,
    };

    private static AttendanceLocationSummaryResponse MapSummary(
        AttendanceLocation location, bool hasActiveCredential, DateTime? lastUsedAt) => new()
    {
        Id = location.Id,
        Name = location.Name,
        Description = location.Description,
        IsActive = location.IsActive,
        HasActiveCredential = hasActiveCredential,
        LastUsedAt = lastUsedAt,
        CreatedAt = location.CreatedAt,
    };
}

/// <summary>Attendance audit action names (plan §19.2), recorded in DetailsJson.</summary>
internal static class AttendanceAuditActions
{
    public const string LocationCreated = "ATTENDANCE_LOCATION_CREATED";
    public const string LocationUpdated = "ATTENDANCE_LOCATION_UPDATED";
    public const string LocationDeactivated = "ATTENDANCE_LOCATION_DEACTIVATED";
    public const string LocationDeleted = "ATTENDANCE_LOCATION_DELETED";
    public const string QrCreated = "QR_CREATED";
    public const string QrRegenerated = "QR_REGENERATED";
    public const string QrRevoked = "QR_REVOKED";
}