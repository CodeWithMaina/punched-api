using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Attendance.Verification;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Attendance;

/// <summary>
/// Reads (and, from Phase 4, writes) the effective attendance policy
/// (plan §6.1, §15.2). A missing row is not an error: it means the implicit
/// Standard default, so a business that never opened the settings page still
/// has working attendance. The policy is NOT an on/off switch — module
/// entitlement remains the authority (§4.4); <c>IsActive</c> only pauses
/// clocking while keeping every credential and every row.
/// </summary>
public class AttendancePolicyService : IAttendancePolicyService
{
    /// <summary>Implicit Standard default: authenticated staff user + business QR (plan §6.1).</summary>
    public const string DefaultRequiredVerificationsJson = "[\"AUTHENTICATED_USER\",\"QR\"]";

    /// <summary>V1 cap for the stale-open-session rule (§9.2); the column lands in Phase 4.</summary>
    public const int DefaultMaxOpenSessionHours = 16;
    public const int MinOpenSessionHours = 4;
    public const int MaxOpenSessionHours = 48;

    private readonly ApplicationDbContext _context;

    public AttendancePolicyService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<AttendancePolicy> GetEffectivePolicyAsync(Guid businessId)
    {
        var policy = await _context.AttendancePolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.BusinessId == businessId);

        return policy ?? CreateImplicitDefault(businessId);
    }

    public async Task<ApiResponse<AttendancePolicyResponse>> GetPolicyAsync(Guid businessId)
    {
        var policy = await GetEffectivePolicyAsync(businessId);

        // Canonical (enum) order, not JSON order, so the wire contract is stable.
        var methods = AttendanceVerification
            .ParseRequiredMethods(policy.RequiredVerificationsJson)
            .OrderBy(m => (int)m)
            .Select(AttendanceVerification.WireValue)
            .ToArray();

        return ApiResponse<AttendancePolicyResponse>.Ok(new AttendancePolicyResponse
        {
            Mode = AttendanceVerification.WireValue(policy.Mode),
            RequiredVerifications = methods,
            IsActive = policy.IsActive,
            MaxOpenSessionHours = DefaultMaxOpenSessionHours,
            UpdatedAt = policy.UpdatedAt,
        });
    }

    /// <summary>
    /// The implicit Standard default. NOT persisted and NOT tracked by the
    /// context — Phase 4 creates the real row lazily on the first settings PUT.
    /// </summary>
    public static AttendancePolicy CreateImplicitDefault(Guid businessId) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        Mode = AttendanceMode.Standard,
        RequiredVerificationsJson = DefaultRequiredVerificationsJson,
        IsActive = true,
        UpdatedAt = null,
    };

    // ── Validation helpers (the write endpoint ships in Phase 4) ───────────
    // Each returns the §9.6 error code to surface, or null when valid.

    /// <summary>Only <c>STANDARD</c> exists in V1 — unknown modes are rejected, never silently coerced.</summary>
    public static string? ValidateMode(string? modeWireValue) =>
        AttendanceVerification.ParseModeWireValue(modeWireValue) != null ? null : "INVALID_MODE";

    /// <summary>
    /// Must be a non-empty set of known methods that includes AUTHENTICATED_USER.
    /// A policy demanding something the platform cannot run is rejected here
    /// rather than failing mysteriously at scan time (§15.2).
    /// </summary>
    public static string? ValidateVerificationSet(IEnumerable<string>? wireValues)
    {
        if (wireValues == null) return "INVALID_VERIFICATION_SET";

        var parsed = new List<AttendanceVerificationMethod>();
        foreach (var wire in wireValues)
        {
            var method = AttendanceVerification.ParseWireValue(wire);
            if (method == null) return "INVALID_VERIFICATION_SET";
            if (!parsed.Contains(method.Value)) parsed.Add(method.Value);
        }

        if (parsed.Count == 0) return "INVALID_VERIFICATION_SET";
        if (!parsed.Contains(AttendanceVerificationMethod.AuthenticatedUser)) return "INVALID_VERIFICATION_SET";

        return null;
    }

    /// <summary>Clamped to <c>4..48</c> hours; null means "leave unchanged" (§15.2).</summary>
    public static string? ValidateSessionWindow(int? hours) =>
        !hours.HasValue || (hours.Value >= MinOpenSessionHours && hours.Value <= MaxOpenSessionHours)
            ? null
            : "INVALID_SESSION_WINDOW";

    /// <summary>Canonical JSON storage form of a validated verification set.</summary>
    public static string SerializeVerifications(IEnumerable<string> wireValues) =>
        JsonSerializer.Serialize(
            wireValues.Select(AttendanceVerification.ParseWireValue)
                .Where(m => m.HasValue)
                .Select(m => m!.Value)
                .Distinct()
                .OrderBy(m => (int)m)
                .Select(AttendanceVerification.WireValue)
                .ToArray());
}