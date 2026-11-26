using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Temporarily reserves a staff member's interval while a booking fee is paid.
///
/// <para><b>Why a hold exists.</b> Money takes minutes; a slot does not wait. Without a claim, two
/// customers can go and pay for the same 10:00 while the faster one's message is being typed, and
/// whoever submits second finds the time gone after they have already sent money. A hold makes the
/// interval unbookable for everyone else for a bounded window, and releases it by itself if the
/// payment never happens.</para>
///
/// <para><b>What a hold is not.</b> It is not an appointment: it has no customer record, no status
/// history, no calendar entry and no commission. It stops blocking the moment it lapses, so an
/// abandoned payment attempt leaves nothing behind in the business's diary.</para>
///
/// <para><b>Concurrency.</b> Two customers can pass the availability read at the same instant, so
/// the claim is written under the same staff-time advisory lock the booking write uses, and the
/// overlap is re-checked inside that transaction. The loser is answered with a conflict rather than
/// being allowed to hold an interval somebody else just claimed.</para>
/// </summary>
public sealed class BookingHoldService
{
    /// <summary>How long a slot is held while the customer pays, when nothing else is configured.</summary>
    public const int DefaultHoldMinutes = 10;

    private const int MinHoldMinutes = 2;
    private const int MaxHoldMinutes = 30;

    private readonly ApplicationDbContext _context;
    private readonly AppointmentAvailabilityService _availability;
    private readonly BookingReadContextLoader _readContexts;
    private readonly ILogger<BookingHoldService> _logger;

    public BookingHoldService(
        ApplicationDbContext context,
        AppointmentAvailabilityService availability,
        BookingReadContextLoader readContexts,
        ILogger<BookingHoldService> logger)
    {
        _context = context;
        _availability = availability;
        _readContexts = readContexts;
        _logger = logger;
    }

    /// <summary>
    /// The hold window in effect. Configuration is clamped: a hold shorter than two minutes cannot
    /// be paid inside, and one longer than half an hour would strand a business's best hours.
    /// </summary>
    public static TimeSpan HoldWindow(int? configuredMinutes) =>
        TimeSpan.FromMinutes(Math.Clamp(
            configuredMinutes ?? DefaultHoldMinutes, MinHoldMinutes, MaxHoldMinutes));

    private static ApiResponse<BookingHoldResponse> Fail(string code, string message) =>
        ApiResponse<BookingHoldResponse>.Fail(code, message);

    /// <summary>True when the hold is still claiming its interval.</summary>
    public static bool IsLive(AppointmentSlotHold hold) =>
        string.Equals(hold.Status, AppointmentSlotHoldStatuses.Active, StringComparison.Ordinal)
        && hold.ExpiresAtUtc > DateTime.UtcNow;

    /// <summary>
    /// Claims the requested interval, or returns the conflict that prevented it.
    /// </summary>
    /// <param name="tenantBusinessId">
    /// The active tenant, when the request arrived on a tenant host. A request naming another
    /// business is refused rather than trusted.
    /// </param>
    public async Task<ApiResponse<BookingHoldResponse>> CreateAsync(
        Guid businessId,
        CreateBookingHoldRequest request,
        Guid? tenantBusinessId,
        int? configuredHoldMinutes,
        CancellationToken ct = default)
    {
        if (tenantBusinessId is Guid activeTenant && activeTenant != businessId)
            return Fail("FORBIDDEN", "This business is not available on this site.");

        if (request.ServiceIds is not { Length: > 0 })
            return Fail("VALIDATION_ERROR", "Choose at least one service.");

        var serviceIds = request.ServiceIds.Distinct().ToArray();
        var key = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim();

        // A resumed booking session (same intent key) keeps the hold it already has, so a refresh
        // or a back-navigation never claims a second interval.
        if (key != null)
        {
            var existing = await _context.AppointmentSlotHolds
                .AsNoTracking()
                .FirstOrDefaultAsync(h => h.BusinessId == businessId && h.IdempotencyKey == key, ct);

            if (existing != null)
            {
                if (IsLive(existing)) return ApiResponse<BookingHoldResponse>.Ok(ToResponse(existing));

                // A lapsed hold cannot be extended: its interval may already belong to somebody
                // else, so the customer is sent back to the grid to choose again.
                return Fail(
                    "RESERVATION_EXPIRED",
                    "The time you were holding is no longer reserved. Please choose a time again.");
            }
        }

        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var read = await _readContexts.LoadAsync(
            businessId, serviceIds, utcToday.AddDays(-2), utcToday.AddDays(16), ct: ct);

        if (read == null) return Fail("BUSINESS_NOT_FOUND", "Business not found.");

        var services = read.Services.Where(s => serviceIds.Contains(s.Id)).ToList();
        if (services.Count != serviceIds.Length)
            return Fail("SERVICE_NOT_FOUND", "One or more services were not found in this business.");

        var totalMinutes = services.Sum(s => s.DurationMinutes);
        if (totalMinutes <= 0)
            return Fail("VALIDATION_ERROR", "The selected services have no duration configured.");

        // The resolver is the same authority the booking write uses, so a slot that cannot be
        // booked cannot be held either — and it now sees other customers' holds as busy.
        var resolution = await _availability.ResolveBookingStaffAsync(
            businessId,
            serviceIds,
            request.StaffUserId,
            request.ScheduledAt,
            totalMinutes,
            excludeAppointmentId: null,
            enforceLeadTime: true,
            business: read.Business);

        if (!resolution.Ok)
        {
            // This is a public endpoint: answer with the storefront's own vocabulary, which says the
            // time is gone without naming a staff member or exposing the roster.
            var code = resolution.ErrorCode == AppointmentConflicts.StaffTimeUnavailable
                ? AppointmentConflicts.SlotUnavailable
                : resolution.ErrorCode ?? AppointmentConflicts.SlotUnavailable;
            var message = resolution.ErrorCode == AppointmentConflicts.StaffTimeUnavailable
                ? AppointmentConflicts.SlotUnavailableMessage
                : resolution.ErrorMessage ?? "That slot is not bookable.";

            return Fail(code, message);
        }

        var staffUserId = resolution.StaffUserId!.Value;
        var startsAt = NormalizeUtc(request.ScheduledAt);
        var endsAt = startsAt.AddMinutes(totalMinutes);

        var hold = new AppointmentSlotHold
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            StaffUserId = staffUserId,
            StartsAtUtc = startsAt,
            EndsAtUtc = endsAt,
            ServiceIdsJson = JsonSerializer.Serialize(serviceIds),
            DurationMinutes = totalMinutes,
            CustomerName = Trim(request.FullName, 100),
            CustomerPhone = Trim(request.PhoneNumber, 40),
            CustomerEmail = Trim(request.Email, 200),
            Note = Trim(request.Note, 500),
            PaymentOption = Trim(request.PaymentOption, 30),
            IdempotencyKey = key,
            ExpiresAtUtc = DateTime.UtcNow.Add(HoldWindow(configuredHoldMinutes)),
            Status = AppointmentSlotHoldStatuses.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        return await SaveUnderLockAsync(hold, key, ct);
    }

    /// <summary>
    /// Writes the claim under the staff member's booking lock, after re-checking the interval
    /// against both the diary and the other live holds.
    /// </summary>
    private async Task<ApiResponse<BookingHoldResponse>> SaveUnderLockAsync(
        AppointmentSlotHold hold, string? key, CancellationToken ct)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(ct);
        try
        {
            await StaffTimeLock.AcquireAsync(_context, hold.BusinessId, hold.StaffUserId, ct);

            var businessId = hold.BusinessId;
            var staffUserId = hold.StaffUserId;
            var startsAt = hold.StartsAtUtc;
            var endsAt = hold.EndsAtUtc;

            var takenByAppointment = await _context.Appointments.AsNoTracking().AnyAsync(a =>
                a.BusinessId == businessId &&
                a.StaffUserId == staffUserId &&
                AppointmentReservations.ReservingStatuses.Contains(a.Status) &&
                a.ScheduledAt < endsAt && a.EndAt > startsAt, ct);

            var now = DateTime.UtcNow;
            var takenByHold = await _context.AppointmentSlotHolds.AsNoTracking().AnyAsync(h =>
                h.BusinessId == businessId &&
                h.StaffUserId == staffUserId &&
                h.Status == AppointmentSlotHoldStatuses.Active &&
                h.ExpiresAtUtc > now &&
                h.StartsAtUtc < endsAt && h.EndsAtUtc > startsAt, ct);

            if (takenByAppointment || takenByHold)
            {
                await transaction.RollbackAsync(ct);
                return Fail(AppointmentConflicts.SlotUnavailable, AppointmentConflicts.SlotUnavailableMessage);
            }

            await _context.AppointmentSlotHolds.AddAsync(hold, ct);
            await _context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex) when (AppointmentConflicts.IsSlotConflict(ex))
        {
            AppointmentConflicts.LogConflict(_logger, ex, hold.BusinessId, hold.StaffUserId);
            await RollbackQuietlyAsync(transaction, ct);
            return Fail(AppointmentConflicts.SlotUnavailable, AppointmentConflicts.SlotUnavailableMessage);
        }
        catch (DbUpdateException ex)
        {
            // A racing request with the same intent key lost to the unique index: answer with the
            // hold the winner created rather than an error the customer cannot act on.
            await RollbackQuietlyAsync(transaction, ct);
            if (key != null)
            {
                var winner = await _context.AppointmentSlotHolds.AsNoTracking()
                    .FirstOrDefaultAsync(h => h.BusinessId == hold.BusinessId && h.IdempotencyKey == key, ct);
                if (winner != null && IsLive(winner))
                    return ApiResponse<BookingHoldResponse>.Ok(ToResponse(winner));
            }

            _logger.LogWarning(ex, "Could not create a slot hold for business {BusinessId}.", hold.BusinessId);
            return Fail("BOOKING_FAILED", "The slot could not be held. Please try again.");
        }

        _logger.LogInformation(
            "Slot hold created. BusinessId={BusinessId} HoldId={HoldId} StaffUserId={StaffUserId} " +
            "StartsAtUtc={StartsAtUtc} ExpiresAtUtc={ExpiresAtUtc}",
            hold.BusinessId, hold.Id, hold.StaffUserId, hold.StartsAtUtc, hold.ExpiresAtUtc);

        return ApiResponse<BookingHoldResponse>.Ok(ToResponse(hold));
    }

    /// <summary>
    /// The hold's live state. This is what the page reads to resynchronise its countdown: the
    /// deadline is the server's, so a laptop waking from sleep or a clock a few minutes fast cannot
    /// keep a lapsed slot looking held.
    /// </summary>
    public async Task<ApiResponse<BookingHoldResponse>> GetAsync(
        Guid businessId, Guid holdId, CancellationToken ct = default)
    {
        var hold = await _context.AppointmentSlotHolds.AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == holdId && h.BusinessId == businessId, ct);

        if (hold == null) return Fail("NOT_FOUND", "That reservation was not found.");

        return ApiResponse<BookingHoldResponse>.Ok(ToResponse(hold));
    }

    /// <summary>Gives a hold up explicitly, so the slot is free the moment the customer leaves.</summary>
    public async Task<ApiResponse<BookingHoldResponse>> ReleaseAsync(
        Guid businessId, Guid holdId, CancellationToken ct = default)
    {
        var hold = await _context.AppointmentSlotHolds
            .FirstOrDefaultAsync(h => h.Id == holdId && h.BusinessId == businessId, ct);

        if (hold == null) return Fail("NOT_FOUND", "That reservation was not found.");

        if (string.Equals(hold.Status, AppointmentSlotHoldStatuses.Active, StringComparison.Ordinal))
        {
            hold.Status = IsLive(hold)
                ? AppointmentSlotHoldStatuses.Released
                : AppointmentSlotHoldStatuses.Expired;
            hold.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
        }

        return ApiResponse<BookingHoldResponse>.Ok(ToResponse(hold));
    }

    /// <summary>
    /// Reads a hold for the booking write. Returns the row only when it is still a live claim;
    /// null means the caller must refuse the booking.
    /// </summary>
    public async Task<AppointmentSlotHold?> FindLiveAsync(
        Guid businessId, Guid holdId, CancellationToken ct = default)
    {
        var hold = await _context.AppointmentSlotHolds.AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == holdId && h.BusinessId == businessId, ct);

        return hold != null && IsLive(hold) ? hold : null;
    }

    /// <summary>
    /// Marks lapsed holds expired. Called by the housekeeping sweep; correctness never depends on
    /// it, because every read already compares the deadline.
    /// </summary>
    public async Task<int> ExpireLapsedAsync(int batchSize = 200, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var lapsed = await _context.AppointmentSlotHolds
            .Where(h => h.Status == AppointmentSlotHoldStatuses.Active && h.ExpiresAtUtc <= now)
            .OrderBy(h => h.ExpiresAtUtc)
            .Take(batchSize)
            .ToListAsync(ct);

        foreach (var hold in lapsed)
        {
            hold.Status = AppointmentSlotHoldStatuses.Expired;
            hold.UpdatedAt = now;
        }

        if (lapsed.Count > 0)
        {
            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Released {Count} lapsed slot holds.", lapsed.Count);
        }

        return lapsed.Count;
    }

    private static BookingHoldResponse ToResponse(AppointmentSlotHold hold)
    {
        var live = IsLive(hold);
        var remaining = live ? (int)Math.Ceiling((hold.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds) : 0;

        return new BookingHoldResponse
        {
            HoldId = hold.Id,
            BusinessId = hold.BusinessId,
            StaffUserId = hold.StaffUserId,
            ServiceIds = DeserializeServiceIds(hold.ServiceIdsJson),
            ScheduledAt = hold.StartsAtUtc,
            EndAt = hold.EndsAtUtc,
            DurationMinutes = hold.DurationMinutes,
            Status = hold.Status,
            ExpiresAtUtc = hold.ExpiresAtUtc,
            SecondsRemaining = Math.Max(0, remaining),
            IsActive = live,
        };
    }

    private static Guid[] DeserializeServiceIds(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Guid[]>(json) ?? Array.Empty<Guid>();
        }
        catch (JsonException)
        {
            return Array.Empty<Guid>();
        }
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

    private static Task RollbackQuietlyAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, CancellationToken ct) =>
        transaction.RollbackAsync(ct);
}
