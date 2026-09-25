using Microsoft.EntityFrameworkCore;
using Npgsql;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

public sealed class ReviewService : IReviewService
{
    internal static readonly TimeSpan SubmissionWindow = TimeSpan.FromDays(30);
    internal static readonly TimeSpan EditWindow = TimeSpan.FromDays(7);
    private readonly ApplicationDbContext _context;
    private readonly TimeProvider _timeProvider;

    public ReviewService(ApplicationDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<ApiResponse<ReviewResponse>> CreateAsync(Guid customerId, CreateReviewRequest request)
    {
        var appointment = await _context.Appointments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.AppointmentId);
        if (appointment == null || appointment.CustomerId != customerId || appointment.Status != "completed")
            return NotEligible<ReviewResponse>();
        var completedAt = await CompletionTimeAsync(appointment.Id);
        if (completedAt == null || _timeProvider.GetUtcNow().UtcDateTime > completedAt.Value.Add(SubmissionWindow))
            return NotEligible<ReviewResponse>();
        if (await _context.Reviews.AnyAsync(r => r.CustomerId == customerId && r.AppointmentId == appointment.Id))
            return ApiResponse<ReviewResponse>.Fail("REVIEW_ALREADY_EXISTS", "You have already reviewed this appointment.");

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var review = new Review
        {
            Id = Guid.NewGuid(), AppointmentId = appointment.Id, BusinessId = appointment.BusinessId,
            CustomerId = customerId, StaffUserId = appointment.StaffUserId, Rating = request.Rating,
            Comment = Normalize(request.Comment), Status = ReviewStatuses.Published,
            CreatedAt = now, UpdatedAt = now
        };
        _context.Reviews.Add(review);
        try { await _context.SaveChangesAsync(); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        { return ApiResponse<ReviewResponse>.Fail("REVIEW_ALREADY_EXISTS", "You have already reviewed this appointment."); }
        return ApiResponse<ReviewResponse>.Ok(MapCustomer(review));
    }

    public async Task<ApiResponse<ReviewEligibilityResponse>> GetAppointmentStateAsync(Guid customerId, Guid appointmentId)
    {
        var appointment = await _context.Appointments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appointmentId);
        if (appointment == null || appointment.CustomerId != customerId)
            return NotEligible<ReviewEligibilityResponse>();
        var completedAt = await CompletionTimeAsync(appointmentId);
        var review = await _context.Reviews.AsNoTracking().FirstOrDefaultAsync(r => r.CustomerId == customerId && r.AppointmentId == appointmentId);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var deadline = completedAt?.Add(SubmissionWindow);
        return ApiResponse<ReviewEligibilityResponse>.Ok(new ReviewEligibilityResponse
        {
            AppointmentId = appointmentId,
            Eligible = appointment.Status == "completed" && deadline.HasValue && now <= deadline.Value && review == null,
            AlreadyReviewed = review != null,
            SubmissionDeadline = deadline,
            EditDeadline = review?.CreatedAt.Add(EditWindow),
            Review = review == null ? null : MapCustomer(review)
        });
    }

    public Task<ApiResponse<PaginatedResponse<ReviewResponse>>> GetCustomerReviewsAsync(Guid customerId, int page, int pageSize) =>
        PageAsync(_context.Reviews.AsNoTracking().Where(r => r.CustomerId == customerId), page, pageSize,
            rows => Task.FromResult(rows.Select(MapCustomer).ToList()));

    public async Task<ApiResponse<PaginatedResponse<PublicReviewResponse>>> GetBusinessReviewsAsync(Guid businessId, int page, int pageSize)
    {
        if (!await ActiveBusinessExistsAsync(businessId)) return NotFoundPage<PublicReviewResponse>();
        var query = _context.Reviews.AsNoTracking().Where(r => r.BusinessId == businessId && r.Status == ReviewStatuses.Published);
        return await PageAsync(query, page, pageSize, async rows =>
        {
            var customers = await DisplayNamesAsync(rows);
            return rows.Select(r => MapPublic(r, customers[r.CustomerId])).ToList();
        });
    }

    public Task<ApiResponse<PaginatedResponse<BusinessReviewResponse>>> GetOwnerReviewsAsync(Guid businessId, int page, int pageSize) =>
        PageAsync(_context.Reviews.AsNoTracking().Where(r => r.BusinessId == businessId), page, pageSize, async rows =>
        {
            var customers = await DisplayNamesAsync(rows);
            return rows.Select(r => MapBusiness(r, customers[r.CustomerId])).ToList();
        });

    public async Task<ApiResponse<ReviewResponse>> UpdateAsync(Guid customerId, Guid reviewId, UpdateReviewRequest request)
    {
        var review = await _context.Reviews.FirstOrDefaultAsync(r => r.Id == reviewId && r.CustomerId == customerId);
        if (review == null) return ApiResponse<ReviewResponse>.Fail("NOT_FOUND", "Review not found.");
        if (_timeProvider.GetUtcNow().UtcDateTime > review.CreatedAt.Add(EditWindow))
            return ApiResponse<ReviewResponse>.Fail("REVIEW_EDIT_WINDOW_EXPIRED", "The review edit window has expired.");
        review.Rating = request.Rating;
        review.Comment = Normalize(request.Comment);
        review.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
        await _context.SaveChangesAsync();
        return ApiResponse<ReviewResponse>.Ok(MapCustomer(review));
    }

    public async Task<ApiResponse<ReviewSummaryResponse>> GetSummaryAsync(Guid businessId)
    {
        if (!await ActiveBusinessExistsAsync(businessId)) return NotFound<ReviewSummaryResponse>();
        var query = _context.Reviews.AsNoTracking().Where(r => r.BusinessId == businessId && r.Status == ReviewStatuses.Published);
        var total = await query.CountAsync();
        var average = total == 0
            ? 0m
            : decimal.Round((decimal)await query.AverageAsync(r => (double)r.Rating), 2, MidpointRounding.AwayFromZero);
        var grouped = await query.GroupBy(r => r.Rating)
            .Select(g => new { Rating = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Rating, x => x.Count);
        return ApiResponse<ReviewSummaryResponse>.Ok(new ReviewSummaryResponse
        {
            BusinessId = businessId, AverageRating = average, TotalCount = total,
            RatingCounts = Enumerable.Range(1, 5).ToDictionary(i => i, i => grouped.GetValueOrDefault(i))
        });
    }

    private async Task<ApiResponse<PaginatedResponse<T>>> PageAsync<T>(IQueryable<Review> query, int page, int pageSize,
        Func<List<Review>, Task<List<T>>> map)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var total = await query.CountAsync();
        var rows = await query.OrderByDescending(r => r.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return ApiResponse<PaginatedResponse<T>>.Ok(new PaginatedResponse<T>
        {
            Items = await map(rows), TotalCount = total, Page = page, PageSize = pageSize
        });
    }

    private Task<DateTime?> CompletionTimeAsync(Guid id) => _context.AppointmentStatusHistory.AsNoTracking()
        .Where(h => h.AppointmentId == id && h.Status == "completed")
        .MaxAsync(h => (DateTime?)h.ChangedAt);

    private Task<bool> ActiveBusinessExistsAsync(Guid id) => _context.Businesses.AsNoTracking().AnyAsync(b => b.Id == id);
    private async Task<Dictionary<Guid, (string Name, string? Avatar)>> DisplayNamesAsync(List<Review> rows)
    {
        var ids = rows.Select(r => r.CustomerId).Distinct().ToList();
        return await _context.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => (u.FullName, u.AvatarUrl));
    }

    private static ReviewResponse MapCustomer(Review r) => new()
    {
        Id = r.Id, AppointmentId = r.AppointmentId, BusinessId = r.BusinessId, Rating = r.Rating,
        Comment = r.Comment, Status = r.Status, CreatedAt = r.CreatedAt, UpdatedAt = r.UpdatedAt
    };

    private static PublicReviewResponse MapPublic(Review r, (string Name, string? Avatar) customer) => new()
    {
        Id = r.Id, BusinessId = r.BusinessId, Rating = r.Rating, Comment = r.Comment,
        ReviewerDisplayName = DisplayName(customer.Name), ReviewerAvatar = customer.Avatar,
        CreatedAt = r.CreatedAt, UpdatedAt = r.UpdatedAt
    };

    private static BusinessReviewResponse MapBusiness(Review r, (string Name, string? Avatar) customer) => new()
    {
        Id = r.Id, BusinessId = r.BusinessId, Rating = r.Rating, Comment = r.Comment, Status = r.Status,
        ReviewerDisplayName = DisplayName(customer.Name), ReviewerAvatar = customer.Avatar,
        CreatedAt = r.CreatedAt, UpdatedAt = r.UpdatedAt
    };

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    internal static string DisplayName(string name)
    {
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 1 ? name.Trim() : $"{parts[0]} {char.ToUpperInvariant(parts[^1][0])}.";
    }
    private static ApiResponse<T> NotEligible<T>() => ApiResponse<T>.Fail("REVIEW_NOT_ELIGIBLE", "This appointment is not eligible for a review.");
    private static ApiResponse<T> NotFound<T>() => ApiResponse<T>.Fail("NOT_FOUND", "Business not found.");
    private static ApiResponse<PaginatedResponse<T>> NotFoundPage<T>() => ApiResponse<PaginatedResponse<T>>.Fail("NOT_FOUND", "Business not found.");
}
