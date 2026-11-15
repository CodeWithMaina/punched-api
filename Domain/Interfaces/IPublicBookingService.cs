using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Booking from the public storefront, for whoever turns up — anonymous, signed in, or signed in
/// to an account that belongs to a different business.
///
/// <para><b>The rule this service exists to keep.</b> A storefront booking must not depend on who
/// the visitor already is, or on whether money has been verified. The business is resolved from
/// the request (and the tenant host, when there is one), the customer record for <i>that</i>
/// business is found or created, the appointment is created and booked immediately, and any
/// payment that comes with it is recorded — pending the owner's verification, never gating the
/// booking.</para>
///
/// <para>Tenant isolation is absolute: the customer the service resolves is enrolled with the
/// business being booked, no business data is read on the caller's behalf, and nothing here can
/// touch another tenant's records.</para>
/// </summary>
public interface IPublicBookingService
{
    /// <summary>
    /// Books an appointment from the storefront.
    /// </summary>
    /// <param name="callerUserId">
    /// The authenticated caller's id, when the request carried a valid token. Used only to reuse a
    /// <b>customer</b> identity; a business or staff token is deliberately not treated as a
    /// customer, because an owner browsing their own page is a visitor like any other.
    /// </param>
    /// <param name="callerRole">The caller's role from the token, or null when anonymous.</param>
    /// <param name="tenantBusinessId">
    /// The business the request arrived on, when it arrived on a tenant host. A body naming a
    /// different business is refused rather than trusted.
    /// </param>
    Task<ApiResponse<PublicBookingResponse>> BookAsync(
        Guid? callerUserId, string? callerRole, Guid? tenantBusinessId,
        PublicBookingRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a late M-PESA confirmation against a booking already made from the storefront. The
    /// appointment is never touched: only the payment's verification state moves.
    /// </summary>
    Task<ApiResponse<PublicBookingResponse>> SubmitPaymentProofAsync(
        Guid businessId, Guid appointmentId, SubmitPublicPaymentProofRequest request,
        CancellationToken cancellationToken = default);
}
