using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Owns the explicit Customer ↔ Business enrollment and Customer ↔ StampCard
/// membership. The database rows here are the authoritative source of truth.
/// </summary>
public interface ICustomerEnrollmentService
{
    Task<ApiResponse<List<CustomerBusinessDto>>> GetMyBusinessesAsync(Guid customerId);

    /// <summary>
    /// Creates or reactivates the (customer, business) relationship and the Customer
    /// membership in one commit.
    ///
    /// <para><paramref name="type"/> records how the relationship was established.
    /// It defaults to <see cref="CustomerType.Registered"/> — every existing caller
    /// means "a real account" — and is only ever <b>upgraded</b> to
    /// <see cref="CustomerType.Registered"/>: a guest enrollment can become
    /// registered, but a registered one is never demoted to guest.</para>
    /// </summary>
    Task<ApiResponse<CustomerBusinessDto>> EnrollAsync(
        Guid customerId, Guid businessId, string? source = null,
        CustomerType type = CustomerType.Registered);
    Task<ApiResponse<bool>> LeaveAsync(Guid customerId, Guid businessId);
    Task<bool> IsEnrolledAsync(Guid customerId, Guid businessId);
    Task<ApiResponse<List<CustomerStampCardDto>>> GetMyStampCardsAsync(Guid customerId, Guid? businessId = null);
    Task<ApiResponse<CustomerStampCardDto>> JoinStampCardAsync(Guid customerId, Guid stampCardId);
    Task<ApiResponse<bool>> LeaveStampCardAsync(Guid customerId, Guid stampCardId);
}
