using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

public interface IReviewService
{
    Task<ApiResponse<ReviewResponse>> CreateAsync(Guid customerId, CreateReviewRequest request);
    Task<ApiResponse<ReviewEligibilityResponse>> GetAppointmentStateAsync(Guid customerId, Guid appointmentId);
    Task<ApiResponse<PaginatedResponse<ReviewResponse>>> GetCustomerReviewsAsync(Guid customerId, int page, int pageSize);
    Task<ApiResponse<ReviewResponse>> UpdateAsync(Guid customerId, Guid reviewId, UpdateReviewRequest request);
    Task<ApiResponse<PaginatedResponse<PublicReviewResponse>>> GetBusinessReviewsAsync(Guid businessId, int page, int pageSize);
    Task<ApiResponse<ReviewSummaryResponse>> GetSummaryAsync(Guid businessId);
    Task<ApiResponse<PaginatedResponse<BusinessReviewResponse>>> GetOwnerReviewsAsync(
        Guid businessId, int page, int pageSize,
        int? rating = null, DateTime? from = null, DateTime? to = null);

    /// <summary>
    /// Reviews left for a specific staff member (their own work), scoped to
    /// that staff member's linked business. Published reviews only — staff are
    /// never shown hidden/removed content. Returns an empty page (not an error)
    /// when the staff member is not linked to a business.
    /// </summary>
    Task<ApiResponse<PaginatedResponse<PublicReviewResponse>>> GetStaffReviewsAsync(Guid staffUserId, int page, int pageSize);
}
