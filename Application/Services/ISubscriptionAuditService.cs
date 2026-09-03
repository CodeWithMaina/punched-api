using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Writes append-only subscription audit records. Audit writes must never
/// cause the primary subscription mutation to fail — failures are logged and
/// swallowed.
/// </summary>
public interface ISubscriptionAuditService
{
    /// <summary>
    /// Records an audit entry. Failures are logged and MUST NOT propagate so the
    /// primary mutation is never rolled back by an audit-write problem.
    /// </summary>
    Task RecordAsync(
        string action,
        Guid? actorUserId,
        Guid? targetBusinessId,
        Guid? targetPlanId,
        string? payloadJson = null,
        string? reason = null);
}