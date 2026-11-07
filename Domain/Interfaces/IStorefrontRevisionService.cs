using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Storefront revision history: reading past configurations, comparing them, and
/// restoring one into the working draft.
///
/// <para>Every method is owner-scoped: the business is derived from the caller's
/// membership through <c>IBusinessScopeResolver</c>, never from the request, so a
/// caller can never reach another tenant's history. A revision that belongs to
/// another business is reported as <c>NOT_FOUND</c> (never <c>FORBIDDEN</c>), so
/// the endpoint cannot be used to probe for the existence of other tenants'
/// revisions.</para>
///
/// <para>Restoring replaces the <b>draft only</b>. The published storefront is
/// never changed by a rollback — that requires an explicit publish, which records
/// its own revision.</para>
/// </summary>
public interface IStorefrontRevisionService
{
    /// <summary>One page of history metadata, newest first. Never returns payloads.</summary>
    Task<ApiResponse<StorefrontRevisionListResponse>> ListAsync(
        Guid ownerId, StorefrontRevisionQuery query, CancellationToken cancellationToken = default);

    /// <summary>One revision with its complete configuration snapshot.</summary>
    Task<ApiResponse<StorefrontRevisionDetailResponse>> GetAsync(
        Guid ownerId, Guid revisionId, CancellationToken cancellationToken = default);

    /// <summary>Structured, human-readable differences between two revisions.</summary>
    Task<ApiResponse<StorefrontRevisionCompareResponse>> CompareAsync(
        Guid ownerId, Guid fromRevisionId, Guid toRevisionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a historical configuration into the draft and records the rollback as
    /// a new revision. Returns the new revision's metadata.
    /// </summary>
    Task<ApiResponse<StorefrontRevisionSummaryResponse>> RestoreAsync(
        Guid ownerId, Guid revisionId, RestoreStorefrontRevisionRequest request, CancellationToken cancellationToken = default);
}
