using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Business-level operating schedule: the recurring weekly hours plus dated
/// exceptions (holidays, closures, special hours).
///
/// <para>This is deliberately separate from <see cref="StaffAvailability"/>: the
/// business schedule says when the premises is open, staff availability says when
/// a person works. Availability is the intersection of the two. A business with no
/// schedule rows keeps the legacy <c>BookingOpenHour/CloseHour</c> behaviour, so
/// no existing tenant changes behaviour until it saves a schedule.</para>
/// </summary>
public interface IBusinessScheduleService
{
    Task<ApiResponse<BusinessScheduleResponse>> GetAsync(Guid businessId, Guid userId, string? role);
    Task<ApiResponse<BusinessScheduleResponse>> SaveAsync(Guid businessId, SaveBusinessScheduleRequest request, Guid userId, string? role);
    Task<ApiResponse<BusinessScheduleExceptionDto>> UpsertExceptionAsync(Guid businessId, SaveScheduleExceptionRequest request, Guid userId, string? role);
    Task<ApiResponse<bool>> DeleteExceptionAsync(Guid businessId, Guid exceptionId, Guid userId, string? role);
    Task<Dictionary<DateOnly, List<(int Start, int End)>>> GetWindowsAsync(Guid businessId, DateOnly from, DateOnly to);
}

public class BusinessScheduleService : IBusinessScheduleService
{
    private readonly ApplicationDbContext _context;

    public BusinessScheduleService(ApplicationDbContext context) => _context = context;

    private async Task<bool> CanManageAsync(Guid businessId, Guid userId, string? role)
    {
        if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)) return true;

        // Authorise THIS business, using the same membership shape as GET /v1/businesses/me
        // (owner + active). The business id comes from the URL, so an owner of another
        // business cannot edit this one.
        return await _context.Businesses.AsNoTracking()
            .AnyAsync(b => b.Id == businessId
                           && b.Memberships.Any(m => m.UserId == userId
                                                     && m.Role == BusinessMembershipRole.Owner
                                                     && m.Status == BusinessMembershipStatus.Active));
    }

    public async Task<ApiResponse<BusinessScheduleResponse>> GetAsync(Guid businessId, Guid userId, string? role)
    {
        if (!await CanManageAsync(businessId, userId, role))
            return ApiResponse<BusinessScheduleResponse>.Fail("FORBIDDEN", "You do not have permission to manage this schedule.");

        var business = await _context.Businesses.AsNoTracking().FirstOrDefaultAsync(b => b.Id == businessId);
        if (business == null)
            return ApiResponse<BusinessScheduleResponse>.Fail("NOT_FOUND", "Business not found.");

        var hours = await _context.BusinessOperatingHours.AsNoTracking()
            .Include(h => h.Periods)
            .Where(h => h.BusinessId == businessId)
            .ToListAsync();

        var exceptions = await _context.BusinessScheduleExceptions.AsNoTracking()
            .Include(e => e.Periods)
            .Where(e => e.BusinessId == businessId)
            .OrderBy(e => e.Date)
            .ToListAsync();

        var configured = hours.Count > 0;
        var days = new List<BusinessScheduleDayDto>();
        for (var day = 0; day <= 6; day++)
        {
            var row = hours.FirstOrDefault(h => h.DayOfWeek == day);
            if (row != null)
            {
                days.Add(new BusinessScheduleDayDto
                {
                    DayOfWeek = day,
                    IsClosed = row.IsClosed,
                    Periods = row.Periods.OrderBy(p => p.StartMinute).Select(ToPeriod).ToList()
                });
            }
            else if (!configured)
            {
                // Nothing saved yet: start from the window the app already uses.
                days.Add(new BusinessScheduleDayDto
                {
                    DayOfWeek = day,
                    IsClosed = false,
                    Periods = new List<SchedulePeriodDto>
                    {
                        new()
                        {
                            StartMinute = business.BookingOpenHour * 60,
                            EndMinute = business.BookingCloseHour * 60
                        }
                    }
                });
            }
            else
            {
                // A schedule exists but this weekday was omitted: treat it as closed.
                days.Add(new BusinessScheduleDayDto { DayOfWeek = day, IsClosed = true });
            }
        }

        return ApiResponse<BusinessScheduleResponse>.Ok(new BusinessScheduleResponse
        {
            BusinessId = businessId,
            TimeZoneId = business.TimeZoneId,
            Days = days,
            Exceptions = exceptions.Select(ToException).ToList()
        });
    }

    private static SchedulePeriodDto ToPeriod(BusinessOperatingPeriod p) =>
        new() { StartMinute = p.StartMinute, EndMinute = p.EndMinute };

    private static BusinessScheduleExceptionDto ToException(BusinessScheduleException e) => new()
    {
        Id = e.Id,
        Date = e.Date.ToString("yyyy-MM-dd"),
        IsClosed = e.IsClosed,
        Reason = e.Reason,
        Periods = e.Periods.OrderBy(p => p.StartMinute)
            .Select(p => new SchedulePeriodDto { StartMinute = p.StartMinute, EndMinute = p.EndMinute })
            .ToList()
    };

    /// <summary>Returns null when the periods are valid, otherwise a user-facing message.</summary>
    private static string? ValidatePeriods(IReadOnlyList<SchedulePeriodDto> periods, bool isClosed)
    {
        if (isClosed) return null;
        if (periods.Count == 0) return "An open day needs at least one working period.";

        var ordered = periods.OrderBy(p => p.StartMinute).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            if (p.StartMinute < 0 || p.EndMinute > 1440 || p.EndMinute <= p.StartMinute)
                return "Each period must start before it ends, within the day.";
            if (i > 0 && ordered[i - 1].EndMinute > p.StartMinute)
                return "Working periods cannot overlap.";
        }

        return null;
    }

    public async Task<ApiResponse<BusinessScheduleResponse>> SaveAsync(
        Guid businessId, SaveBusinessScheduleRequest request, Guid userId, string? role)
    {
        if (!await CanManageAsync(businessId, userId, role))
            return ApiResponse<BusinessScheduleResponse>.Fail("NOT_FOUND", "Business not found.");

        var business = await _context.Businesses.FirstOrDefaultAsync(b => b.Id == businessId);
        if (business == null)
            return ApiResponse<BusinessScheduleResponse>.Fail("NOT_FOUND", "Business not found.");

        if (!string.IsNullOrWhiteSpace(request.TimeZoneId))
        {
            var zone = request.TimeZoneId.Trim();
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(zone);
            }
            catch (TimeZoneNotFoundException)
            {
                return ApiResponse<BusinessScheduleResponse>.Fail("VALIDATION_ERROR", "Unknown time zone.");
            }
            catch (InvalidTimeZoneException)
            {
                return ApiResponse<BusinessScheduleResponse>.Fail("VALIDATION_ERROR", "Unknown time zone.");
            }
            business.TimeZoneId = zone;
        }

        if (request.Days.Count == 0)
            return ApiResponse<BusinessScheduleResponse>.Fail("VALIDATION_ERROR", "Send opening hours for the week.");

        var byDay = request.Days.GroupBy(d => d.DayOfWeek).ToList();
        if (byDay.Any(g => g.Key < 0 || g.Key > 6 || g.Count() > 1))
            return ApiResponse<BusinessScheduleResponse>.Fail("VALIDATION_ERROR", "Each weekday may appear once (0-6).");

        foreach (var group in byDay)
        {
            var problem = ValidatePeriods(group.First().Periods, group.First().IsClosed);
            if (problem != null)
                return ApiResponse<BusinessScheduleResponse>.Fail("VALIDATION_ERROR", problem);
        }

        var now = DateTime.UtcNow;
        var existing = await _context.BusinessOperatingHours
            .Include(h => h.Periods)
            .Where(h => h.BusinessId == businessId)
            .ToListAsync();
        _context.BusinessOperatingHours.RemoveRange(existing);

        foreach (var group in byDay)
        {
            var day = group.First();
            var row = new BusinessOperatingHour
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                DayOfWeek = day.DayOfWeek,
                IsClosed = day.IsClosed,
                CreatedAt = now
            };

            if (!day.IsClosed)
            {
                foreach (var p in day.Periods.OrderBy(p => p.StartMinute))
                {
                    row.Periods.Add(new BusinessOperatingPeriod
                    {
                        Id = Guid.NewGuid(),
                        StartMinute = p.StartMinute,
                        EndMinute = p.EndMinute,
                        CreatedAt = now
                    });
                }
            }

            _context.BusinessOperatingHours.Add(row);
        }

        await _context.SaveChangesAsync();
        return await GetAsync(businessId, userId, role);
    }

    public async Task<ApiResponse<BusinessScheduleExceptionDto>> UpsertExceptionAsync(
        Guid businessId, SaveScheduleExceptionRequest request, Guid userId, string? role)
    {
        if (!await CanManageAsync(businessId, userId, role))
            return ApiResponse<BusinessScheduleExceptionDto>.Fail("NOT_FOUND", "Business not found.");

        if (!DateOnly.TryParseExact(request.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            return ApiResponse<BusinessScheduleExceptionDto>.Fail("VALIDATION_ERROR", "Use a valid date (yyyy-MM-dd).");

        var problem = ValidatePeriods(request.Periods, request.IsClosed);
        if (problem != null)
            return ApiResponse<BusinessScheduleExceptionDto>.Fail("VALIDATION_ERROR", problem);

        var now = DateTime.UtcNow;
        var exception = await _context.BusinessScheduleExceptions
            .Include(e => e.Periods)
            .FirstOrDefaultAsync(e => e.BusinessId == businessId && e.Date == date);

        if (exception == null)
        {
            exception = new BusinessScheduleException
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                Date = date,
                CreatedAt = now
            };
            _context.BusinessScheduleExceptions.Add(exception);
        }

        exception.IsClosed = request.IsClosed;
        exception.Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();

        _context.BusinessScheduleExceptionPeriods.RemoveRange(exception.Periods.ToList());
        exception.Periods.Clear();

        if (!request.IsClosed)
        {
            foreach (var p in request.Periods.OrderBy(p => p.StartMinute))
            {
                exception.Periods.Add(new BusinessScheduleExceptionPeriod
                {
                    Id = Guid.NewGuid(),
                    StartMinute = p.StartMinute,
                    EndMinute = p.EndMinute,
                    CreatedAt = now
                });
            }
        }

        await _context.SaveChangesAsync();

        var saved = await _context.BusinessScheduleExceptions.AsNoTracking()
            .Include(e => e.Periods)
            .FirstAsync(e => e.Id == exception.Id);

        return ApiResponse<BusinessScheduleExceptionDto>.Ok(ToException(saved));
    }

    public async Task<ApiResponse<bool>> DeleteExceptionAsync(
        Guid businessId, Guid exceptionId, Guid userId, string? role)
    {
        if (!await CanManageAsync(businessId, userId, role))
            return ApiResponse<bool>.Fail("NOT_FOUND", "Business not found.");

        var exception = await _context.BusinessScheduleExceptions
            .FirstOrDefaultAsync(e => e.Id == exceptionId && e.BusinessId == businessId);
        if (exception == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "Exception not found.");

        _context.BusinessScheduleExceptions.Remove(exception);
        await _context.SaveChangesAsync();
        return ApiResponse<bool>.Ok(true);
    }

    /// <summary>
    /// The effective business windows for each local date in the range.
    ///
    /// <para>An empty result means the business has never configured a schedule,
    /// and the caller must keep its legacy booking-window behaviour. Once rows
    /// exist, an exception wins over the weekday, an omitted weekday is closed,
    /// and an empty list for a date means that date is closed.</para>
    /// </summary>
    public async Task<Dictionary<DateOnly, List<(int Start, int End)>>> GetWindowsAsync(
        Guid businessId, DateOnly from, DateOnly to)
    {
        var result = new Dictionary<DateOnly, List<(int Start, int End)>>();

        var hours = await _context.BusinessOperatingHours.AsNoTracking()
            .Include(h => h.Periods)
            .Where(h => h.BusinessId == businessId)
            .ToListAsync();
        if (hours.Count == 0)
            return result;

        var exceptions = await _context.BusinessScheduleExceptions.AsNoTracking()
            .Include(e => e.Periods)
            .Where(e => e.BusinessId == businessId && e.Date >= from && e.Date <= to)
            .ToListAsync();

        var weekly = hours.GroupBy(h => h.DayOfWeek).ToDictionary(g => g.Key, g => g.First());

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var exception = exceptions.FirstOrDefault(e => e.Date == date);
            if (exception != null)
            {
                result[date] = exception.IsClosed
                    ? new List<(int, int)>()
                    : exception.Periods.Select(p => (p.StartMinute, p.EndMinute)).OrderBy(p => p.Item1).ToList();
                continue;
            }

            if (weekly.TryGetValue((int)date.DayOfWeek, out var row))
            {
                result[date] = row.IsClosed
                    ? new List<(int, int)>()
                    : row.Periods.Select(p => (p.StartMinute, p.EndMinute)).OrderBy(p => p.Item1).ToList();
            }
            else
            {
                result[date] = new List<(int, int)>();
            }
        }

        return result;
    }
}
