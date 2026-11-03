using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Booking without an account.
///
/// <para>A visitor proves control of an inbox with a one-time code; the platform
/// then resolves or creates the customer record behind that identity and returns
/// a short-lived customer token. The booking itself is then made through the
/// existing customer appointment API, so availability, staff eligibility,
/// working hours, booking windows, payment rules and module entitlements are
/// enforced exactly as they are for a signed-in customer — nothing is bypassed,
/// and no anonymous caller can act as an identity they have not proved.</para>
/// </summary>
public interface IGuestBookingService
{
    /// <summary>
    /// Sends a one-time code to the address the visitor supplied, after resolving
    /// or creating the unverified customer record it belongs to.
    ///
    /// <para>Refuses with <c>ACCOUNT_EXISTS</c> when the address already belongs
    /// to a verified account: verifying it here would be an account-takeover
    /// path, so that visitor is told to sign in instead.</para>
    /// </summary>
    Task<ApiResponse<StartGuestBookingResponse>> StartAsync(
        Guid businessId, StartGuestBookingRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the code and issues a short-lived customer token for the business.
    /// </summary>
    Task<ApiResponse<VerifyGuestBookingResponse>> VerifyAsync(
        Guid businessId, VerifyGuestBookingRequest request, CancellationToken cancellationToken = default);
}
