using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Business-scoped, anonymous search across everything a business publishes:
/// its showcased services and its publicly visible staff.
///
/// <para>Cross-business results are impossible by construction — every query is
/// anchored to the business the request named (and, on a tenant host, to the
/// active tenant). Unpublished services (<c>IsActive = false</c>,
/// <c>Showcase = false</c> or archived) and non-public staff are excluded at the
/// query, never filtered by the caller.</para>
/// </summary>
public interface IStorefrontSearchService
{
    /// <summary>
    /// Searches the business's published catalogue and public roster.
    /// </summary>
    /// <param name="businessId">The business to search (tenant-narrowed).</param>
    /// <param name="query">Free text. Blank returns an empty page (with categories).</param>
    /// <param name="type">`all`, `services` or `team`.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Requested page size (server-clamped).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ApiResponse<StorefrontSearchResponse>> SearchAsync(
        Guid businessId,
        string? query,
        string? type,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}
