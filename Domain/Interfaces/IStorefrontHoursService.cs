using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Public, anonymous read of a business's opening hours.
///
/// <para>The hours are derived from the SAME scheduling domain the availability
/// engine uses — the staff working pattern, dated overrides and the business's
/// own booking window — so what a visitor reads and what the booking engine
/// offers cannot drift apart. No second scheduling structure is introduced.</para>
/// </summary>
public interface IStorefrontHoursService
{
    /// <summary>
    /// The business's opening hours, business-local, plus dated exceptions.
    ///
    /// <para>Returns <c>NOT_FOUND</c> for an unknown/deleted business or a
    /// business that is not the active tenant.</para>
    /// </summary>
    Task<ApiResponse<PublicOpeningHoursResponse>> GetPublicOpeningHoursAsync(
        Guid businessId, CancellationToken cancellationToken = default);
}
