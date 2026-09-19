using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Owns the explicit Customer ↔ Business enrollment and Customer ↔ StampCard
/// membership. The database rows here are the authoritative source of truth.
/// </summary>
public interface ICustomerEnrollmentService
{
    Task<ApiResponse<List<CustomerBusinessDto>>> GetMyBusinessesAsync(Guid customerId);
    Task<ApiResponse<CustomerBusinessDto>> EnrollAsync(Guid customerId, Guid businessId, string? source = null);
    Task<ApiResponse<bool>> LeaveAsync(Guid customerId, Guid businessId);
    Task<bool> IsEnrolledAsync(Guid customerId, Guid businessId);
    Task<ApiResponse<List<CustomerStampCardDto>>> GetMyStampCardsAsync(Guid customerId, Guid? businessId = null);
    Task<ApiResponse<CustomerStampCardDto>> JoinStampCardAsync(Guid customerId, Guid stampCardId);
    Task<ApiResponse<bool>> LeaveStampCardAsync(Guid customerId, Guid stampCardId);
}
