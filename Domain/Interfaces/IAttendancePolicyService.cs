using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Reads the effective attendance policy for a business (plan §6.1). The policy
/// is NOT a second on/off switch — module entitlement remains the on/off
/// authority (§4.4); the policy only pauses clocking and declares which
/// verification methods must run.
/// </summary>
public interface IAttendancePolicyService
{
    /// <summary>
    /// The business's policy, or the implicit Standard default when no row
    /// exists (a business that never opened the settings page still has
    /// working attendance). The returned default is NOT persisted — Phase 4
    /// creates the row lazily on the first settings write.
    /// </summary>
    Task<AttendancePolicy> GetEffectivePolicyAsync(Guid businessId);

    /// <summary>Wire-shaped view of the effective policy (SCREAMING_SNAKE values).</summary>
    Task<ApiResponse<AttendancePolicyResponse>> GetPolicyAsync(Guid businessId);
}