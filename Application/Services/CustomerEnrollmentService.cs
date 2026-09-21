using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Authoritative Customer-Business enrollment + Customer-StampCard membership.
/// </summary>
public class CustomerEnrollmentService : ICustomerEnrollmentService
{
    private static readonly HashSet<string> ValidSources = new(StringComparer.OrdinalIgnoreCase)
        { "qr", "discovery", "business", "booking", "referral", "loyalty" };

    private readonly IUnitOfWork _unitOfWork;
    private readonly ApplicationDbContext _context;
    private readonly ILogger<CustomerEnrollmentService> _logger;

    public CustomerEnrollmentService(IUnitOfWork u, ApplicationDbContext c, ILogger<CustomerEnrollmentService> l)
    { _unitOfWork = u; _context = c; _logger = l; }

    public async Task<bool> IsEnrolledAsync(Guid customerId, Guid businessId) =>
        await _context.CustomerBusinessEnrollments.AnyAsync(e =>
            e.CustomerId == customerId && e.BusinessId == businessId &&
            e.Status == CustomerBusinessEnrollmentStatus.Active);

    public async Task<ApiResponse<List<CustomerBusinessDto>>> GetMyBusinessesAsync(Guid customerId)
    {
        var rows = await _context.CustomerBusinessEnrollments.AsNoTracking()
            .Include(e => e.Business)
            .Where(e => e.CustomerId == customerId && e.Status == CustomerBusinessEnrollmentStatus.Active)
            .OrderByDescending(e => e.EnrolledAt).ToListAsync();
        return ApiResponse<List<CustomerBusinessDto>>.Ok(rows.Select(e => MapEnrollment(e)).ToList());
    }


    public async Task<ApiResponse<CustomerBusinessDto>> EnrollAsync(Guid customerId, Guid businessId, string? source = null)
    {
        var src = NormalizeSource(source);
        try
        {
            var business = await _context.Businesses.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == businessId && !b.IsDeleted);
            if (business == null) return ApiResponse<CustomerBusinessDto>.Fail("NOT_FOUND", "Business not found.");
            var existing = await _unitOfWork.CustomerBusinessEnrollments
                .FirstOrDefaultAsync(e => e.CustomerId == customerId && e.BusinessId == businessId);
            if (existing != null)
            {
                if (existing.Status != CustomerBusinessEnrollmentStatus.Active)
                {
                    existing.Status = CustomerBusinessEnrollmentStatus.Active;
                    existing.LeftAt = null;
                    existing.EnrolledAt = DateTime.UtcNow;
                    existing.UpdatedAt = DateTime.UtcNow;
                    existing.Source = src;
                    _unitOfWork.CustomerBusinessEnrollments.Update(existing);
                    await _unitOfWork.SaveChangesAsync();
                }
                return ApiResponse<CustomerBusinessDto>.Ok(MapEnrollment(existing, business));
            }
            var enrollment = new CustomerBusinessEnrollment
            {
                Id = Guid.NewGuid(), CustomerId = customerId, BusinessId = businessId,
                Status = CustomerBusinessEnrollmentStatus.Active, Source = src,
                EnrolledAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
            };
            await _unitOfWork.CustomerBusinessEnrollments.AddAsync(enrollment);
            await _unitOfWork.SaveChangesAsync();
            return ApiResponse<CustomerBusinessDto>.Ok(MapEnrollment(enrollment, business));
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            var row = await _context.CustomerBusinessEnrollments.AsNoTracking().Include(e => e.Business)
                .FirstOrDefaultAsync(e => e.CustomerId == customerId && e.BusinessId == businessId);
            if (row != null) return ApiResponse<CustomerBusinessDto>.Ok(MapEnrollment(row));
            return ApiResponse<CustomerBusinessDto>.Fail("ENROLL_FAILED", "Failed to enroll.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Enroll failed {C} {B}", customerId, businessId);
            return ApiResponse<CustomerBusinessDto>.Fail("ENROLL_FAILED", "Failed to enroll.");
        }
    }

    public async Task<ApiResponse<bool>> LeaveAsync(Guid customerId, Guid businessId)
    {
        var existing = await _unitOfWork.CustomerBusinessEnrollments
            .FirstOrDefaultAsync(e => e.CustomerId == customerId && e.BusinessId == businessId);
        if (existing == null || existing.Status == CustomerBusinessEnrollmentStatus.Left)
            return ApiResponse<bool>.Ok(true);
        existing.Status = CustomerBusinessEnrollmentStatus.Left;
        existing.LeftAt = DateTime.UtcNow;
        existing.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.CustomerBusinessEnrollments.Update(existing);
        await _unitOfWork.SaveChangesAsync();
        return ApiResponse<bool>.Ok(true);
    }

    public async Task<ApiResponse<List<CustomerStampCardDto>>> GetMyStampCardsAsync(Guid customerId, Guid? businessId = null)
    {
        var query = _context.CustomerStampCards.AsNoTracking()
            .Include(c => c.StampCard).ThenInclude(s => s.Business)
            .Where(c => c.CustomerId == customerId && c.Status == CustomerStampCardStatus.Active);
        if (businessId.HasValue) query = query.Where(c => c.StampCard.BusinessId == businessId.Value);
        var rows = await query.OrderByDescending(c => c.JoinedAt).ToListAsync();
        var totals = await _context.LoyaltyCards.AsNoTracking()
            .Where(l => l.CustomerId == customerId).ToDictionaryAsync(l => l.BusinessId, l => l.TotalStamps);
        return ApiResponse<List<CustomerStampCardDto>>.Ok(
            rows.Select(r => MapStampCard(r, totals.GetValueOrDefault(r.StampCard.BusinessId))).ToList());
    }


    public async Task<ApiResponse<CustomerStampCardDto>> JoinStampCardAsync(Guid customerId, Guid stampCardId)
    {
        try
        {
            var card = await _context.StampCards.AsNoTracking().Include(s => s.Business)
                .FirstOrDefaultAsync(s => s.Id == stampCardId);
            if (card == null) return ApiResponse<CustomerStampCardDto>.Fail("NOT_FOUND", "Stamp card not found.");
            if (card.Status == StampCardStatus.Archived) return ApiResponse<CustomerStampCardDto>.Fail("CARD_ARCHIVED", "Archived.");
            if (card.Status != StampCardStatus.Active) return ApiResponse<CustomerStampCardDto>.Fail("CARD_INACTIVE", "Not active.");
            if (!await IsEnrolledAsync(customerId, card.BusinessId))
                return ApiResponse<CustomerStampCardDto>.Fail("NOT_ENROLLED", "Enroll in the business first.");
            var existing = await _unitOfWork.CustomerStampCards
                .FirstOrDefaultAsync(c => c.CustomerId == customerId && c.StampCardId == stampCardId);
            if (existing != null)
            {
                if (existing.Status != CustomerStampCardStatus.Active)
                {
                    existing.Status = CustomerStampCardStatus.Active;
                    existing.LeftAt = null;
                    existing.JoinedAt = DateTime.UtcNow;
                    existing.UpdatedAt = DateTime.UtcNow;
                    _unitOfWork.CustomerStampCards.Update(existing);
                    await _unitOfWork.SaveChangesAsync();
                }
                return ApiResponse<CustomerStampCardDto>.Ok(await MapStampCardWithTotalAsync(existing, card));
            }
            var m = new CustomerStampCard
            {
                Id = Guid.NewGuid(), CustomerId = customerId, StampCardId = stampCardId,
                Status = CustomerStampCardStatus.Active, JoinedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
            };
            await _unitOfWork.CustomerStampCards.AddAsync(m);
            await _unitOfWork.SaveChangesAsync();
            return ApiResponse<CustomerStampCardDto>.Ok(await MapStampCardWithTotalAsync(m, card));
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            var row = await _context.CustomerStampCards.AsNoTracking().Include(c => c.StampCard).ThenInclude(s => s.Business)
                .FirstOrDefaultAsync(c => c.CustomerId == customerId && c.StampCardId == stampCardId);
            if (row != null) return ApiResponse<CustomerStampCardDto>.Ok(MapStampCard(row, 0));
            return ApiResponse<CustomerStampCardDto>.Fail("JOIN_FAILED", "Failed to join.");
        }
    }

    public async Task<ApiResponse<bool>> LeaveStampCardAsync(Guid customerId, Guid stampCardId)
    {
        var existing = await _unitOfWork.CustomerStampCards
            .FirstOrDefaultAsync(c => c.CustomerId == customerId && c.StampCardId == stampCardId);
        if (existing == null || existing.Status == CustomerStampCardStatus.Removed)
            return ApiResponse<bool>.Ok(true);
        existing.Status = CustomerStampCardStatus.Removed;
        existing.LeftAt = DateTime.UtcNow;
        existing.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.CustomerStampCards.Update(existing);
        await _unitOfWork.SaveChangesAsync();
        return ApiResponse<bool>.Ok(true);
    }

    private static string NormalizeSource(string? s)
    {
        if (!string.IsNullOrWhiteSpace(s) && ValidSources.Contains(s.Trim())) return s.Trim().ToLowerInvariant();
        return "discovery";
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("23505") == true
        || ex.InnerException?.Message.Contains("UNIQUE constraint failed") == true
        || ex.Message.Contains("23505");

    private static CustomerBusinessDto MapEnrollment(CustomerBusinessEnrollment e, Business? b = null)
    {
        b ??= e.Business;
        return new CustomerBusinessDto
        {
            Id = e.Id, BusinessId = e.BusinessId, BusinessName = b?.Name ?? string.Empty,
            LogoUrl = b?.LogoUrl, Category = b?.Category, Location = b?.Location,
            Status = e.Status == CustomerBusinessEnrollmentStatus.Active ? "active" : e.Status.ToString().ToLowerInvariant(),
            Source = e.Source, EnrolledAt = e.EnrolledAt
        };
    }

    private async Task<CustomerStampCardDto> MapStampCardWithTotalAsync(CustomerStampCard m, StampCard card)
    {
        var total = await _context.LoyaltyCards.AsNoTracking()
            .Where(l => l.CustomerId == m.CustomerId && l.BusinessId == card.BusinessId)
            .Select(l => (int?)l.TotalStamps).FirstOrDefaultAsync() ?? 0;
        return MapStampCard(m, total, card);
    }

    private static CustomerStampCardDto MapStampCard(CustomerStampCard m, int current, StampCard? card = null)
    {
        card ??= m.StampCard;
        return new CustomerStampCardDto
        {
            Id = m.Id, StampCardId = m.StampCardId, BusinessId = card.BusinessId,
            BusinessName = card.Business?.Name ?? string.Empty, Name = card.Name,
            RewardDescription = card.RewardDescription, StampsRequired = card.StampsRequired,
            CurrentStamps = current,
            Status = m.Status == CustomerStampCardStatus.Active ? "active" : "removed",
            JoinedAt = m.JoinedAt
        };
    }
}



