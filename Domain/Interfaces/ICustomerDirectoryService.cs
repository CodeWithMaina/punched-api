using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Owner-side customer directory writes that support the booking flow — creating a
/// customer for a walk-in without leaving the appointment wizard.
///
/// <para>Enrollment itself is delegated to <see cref="ICustomerEnrollmentService"/>,
/// the authoritative writer, so a quick-added customer ends up with exactly the same
/// membership/enrollment rows as one added any other way.</para>
/// </summary>
public interface ICustomerDirectoryService
{
    /// <summary>
    /// Adds (or reuses) a customer for the owner's business and returns the reference
    /// the booking wizard selects. Idempotent per phone/email: an existing Customer
    /// identity is reused rather than duplicated. The business is always resolved from
    /// the caller's ownership, never from the request.
    /// </summary>
    Task<ApiResponse<CustomerOptionResponse>> QuickAddAsync(Guid ownerUserId, QuickAddCustomerRequest request);
}
