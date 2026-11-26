using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Mappings;
using PunchedApi.Application.DomainEvents;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Scheduling;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Owns all appointment/booking logic: creation, on-behalf booking, reschedule, status
/// transitions, and role-scoped reads. Caller identity + role are always passed in; the
/// caller's tenant is resolved server-side and never trusted from request DTOs.
/// </summary>
public class AppointmentService : IAppointmentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ApplicationDbContext _context;
    private readonly AppointmentAvailabilityService _availability;
    private readonly IPermissionService _permissionService;
    private readonly INotificationService _notifications;
    private readonly IDomainEventPublisher _domainEventPublisher;
    private readonly ILogger<AppointmentService> _logger;

    /// <summary>
    /// Optional commission ledger. Optional so the many existing construction
    /// sites (including the test suite) keep compiling unchanged; when it is
    /// absent the appointment lifecycle simply accrues nothing.
    /// </summary>
    private readonly ICommissionService? _commissions;

    /// <summary>Active tenant (null in unit tests / root host) — scoping only, never a grant.</summary>
    private readonly ITenantContext? _tenant;

    /// <summary>
    /// Clock for the gates that decide whether a booking is allowed (lead time, "the
    /// new time must be in the future", proposal expiry). Injectable so those rules
    /// can be tested deterministically; audit timestamps and job scheduling keep
    /// using the real wall clock.
    /// </summary>
    private readonly TimeProvider _time;

    /// <summary>
    /// Module entitlement resolver. The Staff module is OPTIONAL, so every path
    /// that could assign a staff member to an appointment (create, on-behalf,
    /// reschedule, filter) consults this to enforce the entitlement server-side.
    /// Optional so every existing construction site (including the test suite)
    /// keeps compiling; when absent the staff module is treated as enabled.
    /// </summary>
    private readonly IModuleEntitlementService? _moduleEntitlements;

    /// <summary>
    /// The single provider-eligibility resolver. Used here only to ask the one question this
    /// service owns — <i>may a request name a provider?</i> — so the booking write path shares
    /// the policy with availability instead of re-deriving it. Optional, like the entitlement
    /// resolver, so existing construction sites keep working.
    /// </summary>
    private readonly IProviderEligibilityService? _providerEligibility;

    public AppointmentService(
        IUnitOfWork unitOfWork,
        ApplicationDbContext context,
        AppointmentAvailabilityService availability,
        IPermissionService permissionService,
        INotificationService notifications,
        IDomainEventPublisher domainEventPublisher,
        ILogger<AppointmentService> logger,
        ITenantContext? tenant = null,
        ICommissionService? commissions = null,
        TimeProvider? timeProvider = null,
        IModuleEntitlementService? moduleEntitlements = null,
        IProviderEligibilityService? providerEligibility = null)
    {
        _unitOfWork = unitOfWork;
        _context = context;
        _availability = availability;
        _permissionService = permissionService;
        _notifications = notifications;
        _domainEventPublisher = domainEventPublisher;
        _logger = logger;
        _tenant = tenant;
        _commissions = commissions;
        _time = timeProvider ?? TimeProvider.System;
        _moduleEntitlements = moduleEntitlements;
        _providerEligibility = providerEligibility;
    }

    /// <summary>
    /// May a booking request name its provider? Decided by the one provider-eligibility
    /// policy — never by a local re-interpretation of the module flag.
    /// </summary>
    private async Task<bool> AllowsExplicitStaffSelectionAsync(Guid businessId)
    {
        if (_providerEligibility != null)
        {
            var model = await _providerEligibility.ResolveModelAsync(businessId);
            return ProviderEligibilityPolicy.AllowsExplicitProviderSelection(model);
        }

        // No resolver wired (unit tests): the policy still decides, from the entitlement
        // flag and the operating mode it can see.
        var operatingMode = await _context.Businesses.AsNoTracking()
            .Where(b => b.Id == businessId)
            .Select(b => b.OperatingMode)
            .FirstOrDefaultAsync();

        var staffEnabled = await IsStaffModuleEnabledAsync(businessId);
        var fallbackModel = ProviderEligibilityPolicy.ResolveModel(operatingMode, staffEnabled, hasBookableStaff: true);
        return ProviderEligibilityPolicy.AllowsExplicitProviderSelection(fallbackModel);
    }

    /// <summary>Current UTC time, from the injected clock (booking decisions only).</summary>
    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>Active tenant business id, or null on the platform root (legacy scoping).</summary>
    private Guid? TenantBusinessId => _tenant?.IsActive == true ? _tenant.BusinessId : null;

    /// <summary>
    /// True when the business may use the optional Staff module.
    ///
    /// <para>This gates <b>new</b> staff assignment and staff filtering only. It
    /// never touches historical data: an appointment created while the module was
    /// enabled keeps its <c>StaffUserId</c>, and the dashboard still resolves the
    /// assigned member's name/avatar — the requirement is that the module's
    /// absence removes the *controls*, not the record.</para>
    ///
    /// <para>Resolved through the platform's real entitlement system
    /// (<see cref="IModuleEntitlementService"/>), never a hardcoded flag. When no
    /// resolver is wired in (unit tests), the module is treated as enabled so the
    /// pre-existing behaviour is unchanged.</para>
    /// </summary>
    private async Task<bool> IsStaffModuleEnabledAsync(Guid businessId)
    {
        if (_moduleEntitlements == null) return true;
        return await _moduleEntitlements.IsModuleEnabledAsync(businessId, "staff");
    }

    /// <summary>True when the identity is ACTIVE staff of the business (membership-sourced).</summary>
    private Task<bool> IsStaffOfBusinessAsync(Guid userId, Guid businessId) =>
        _context.BusinessMemberships.AsNoTracking().AnyAsync(m =>
            m.UserId == userId &&
            m.BusinessId == businessId &&
            m.Role == BusinessMembershipRole.Staff &&
            m.Status == BusinessMembershipStatus.Active);

    /// <summary>The identity's ACTIVE staff business, or null.</summary>
    private Task<Guid?> GetActiveStaffBusinessIdAsync(Guid userId) =>
        _context.BusinessMemberships.AsNoTracking()
            .Where(m => m.UserId == userId &&
                        m.Role == BusinessMembershipRole.Staff &&
                        m.Status == BusinessMembershipStatus.Active)
            .Select(m => (Guid?)m.BusinessId)
            .FirstOrDefaultAsync();

    private Task<string> GetBusinessNameAsync(Guid businessId) => _context.Businesses
        .AsNoTracking()
        .Where(business => business.Id == businessId)
        .Select(business => business.Name)
        .FirstOrDefaultAsync()!;

    private async Task SendNotificationSafelyAsync(NotificationRequest request)
    {
        try
        {
            await _notifications.SendAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification {NotificationType} could not be queued for recipient {RecipientUserId}.",
                request.Type, request.RecipientUserId);
        }
    }

    /// <summary>
    /// Queues a notification intent inside the caller's open unit of work so it commits
    /// atomically with the domain change (Phase 1 intent-level outbox). Notification
    /// problems are logged and never fail the domain operation.
    /// </summary>
    private async Task QueueNotificationSafelyAsync(NotificationRequest request)
    {
        try
        {
            await _notifications.QueueAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification {NotificationType} could not be queued atomically for recipient {RecipientUserId}.",
                request.Type, request.RecipientUserId);
        }
    }

    /// <summary>
    /// Queues a batch of notification intents inside the caller's open unit of work. Like
    /// <see cref="QueueNotificationSafelyAsync"/>, a notification problem is logged and never
    /// fails the domain operation.
    /// </summary>
    private async Task QueueNotificationsSafelyAsync(IReadOnlyList<NotificationRequest> requests)
    {
        if (requests.Count == 0) return;

        try
        {
            await _notifications.QueueManyAsync(requests);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Notification intents could not be queued atomically for {Count} recipient(s).",
                requests.Count);
        }
    }

    /// <summary>
    /// Fine-grained permission gate for staff-initiated bookings.
    ///
    /// <para>The permission lives in the business's RBAC (<c>appointments.create</c>):
    /// an owner grants it to a role, and the member holds it through their role
    /// assignment. Reading the business permission rather than a hard-coded persona
    /// check is what lets a business decide whether its service professionals may
    /// book customers in, instead of the platform deciding for them.</para>
    ///
    /// <para>Distinct from MODULE_DISABLED — this is a permission failure, not an
    /// entitlement one.</para>
    /// </summary>
    private async Task<ApiResponse<AppointmentResponse>?> StaffCreatePermissionGuardAsync(
        Guid callerUserId, Guid businessId)
        => await _permissionService.HasBusinessPermissionAsync(callerUserId, businessId, "appointments.create")
            ? null
            : ApiResponse<AppointmentResponse>.Fail(
                "FORBIDDEN",
                "You do not have permission to create appointments (appointments.create required).");

    /// <summary>
    /// Which member a staff-initiated booking may occupy.
    ///
    /// <para>By default a member books <b>themselves</b>. Assigning somebody else is a
    /// management action and needs <c>appointments.manage</c>, so one service
    /// professional cannot quietly fill a colleague's diary. The business owner and
    /// the front desk hold that permission.</para>
    /// </summary>
    private async Task<(Guid? StaffUserId, ApiResponse<AppointmentResponse>? Error)>
        ResolveStaffCreateAssignmentAsync(Guid callerUserId, Guid businessId, Guid? requestedStaffUserId)
    {
        var target = requestedStaffUserId ?? callerUserId;

        if (target != callerUserId)
        {
            var mayAssignOthers = await _permissionService.HasBusinessPermissionAsync(
                callerUserId, businessId, "appointments.manage");

            if (!mayAssignOthers)
                return (null, ApiResponse<AppointmentResponse>.Fail(
                    "FORBIDDEN", "You can only create appointments for yourself."));
        }

        return (target, null);
    }

    // ═══════════════════════════════════════════════════════════
    //  AVAILABILITY
    // ═══════════════════════════════════════════════════════════

    public async Task<ApiResponse<List<AvailabilitySlotResponse>>> GetAvailableSlotsAsync(
        Guid userId, string role, Guid businessId, AvailabilityQueryRequest request)
    {
        return await _availability.GetAvailableSlotsAsync(
            request.BusinessId, request.ServiceIds, request.StaffUserId, request.StartDate, request.EndDate);
    }

    public async Task<ApiResponse<AvailabilityCalendarResponse>> GetAvailabilityCalendarAsync(
        Guid userId, string role, Guid businessId, AvailabilityQueryRequest request)
    {
        return await _availability.GetAvailabilityCalendarAsync(
            request.BusinessId, request.ServiceIds, request.StaffUserId, request.StartDate, request.EndDate);
    }

    // ═══════════════════════════════════════════════════════════
    //  STOREFRONT FAST PATH
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Books a storefront appointment from a context that has <b>already been read</b>.
    ///
    /// <para>This is the critical path a customer waits on. The caller (the public booking
    /// service) has resolved the business, the catalogue, the roster, the roster's working
    /// windows and the diary in one parallel read wave, so this method validates the whole
    /// requested interval in memory and then does exactly one write transaction: lock →
    /// re-check → insert (appointment, service lines, history, notifications, reminders,
    /// domain event and any booking-fee row) → commit.</para>
    ///
    /// <para>It never trusts a client figure. The duration is the sum of the catalogue's
    /// own durations, the price snapshot is the catalogue's own price, the staff member is
    /// one of the server's eligible providers, and the interval must fit entirely inside
    /// that member's effective working window with no overlap against a reserving
    /// appointment.</para>
    /// </summary>
    public async Task<ApiResponse<AppointmentResponse>> CreateStorefrontAppointmentAsync(
        BookingReadContext read,
        Guid customerId,
        BookingCustomer customer,
        Guid[] serviceIds,
        Guid? staffUserId,
        DateTime scheduledAt,
        string? note,
        bool payAfterService,
        string? bookingRequestKey,
        Func<Guid, Payment?>? bookingFeeFactory = null,
        CancellationToken cancellationToken = default,
        string? initialStatus = null,
        Guid? convertHoldId = null)
    {
        var distinctRequested = serviceIds.Distinct().ToArray();

        // A retry of the same booking intent is answered with the booking that already
        // exists, BEFORE availability is re-checked: the interval is now held by the
        // customer's own appointment, so a fresh check would call their booking a conflict.
        if (read.ExistingBooking is Appointment existing)
        {
            if (existing.CustomerId != customerId)
                return ApiResponse<AppointmentResponse>.Fail(
                    "DUPLICATE_BOOKING_KEY",
                    "That booking key has already been used. Start a new booking.");

            return ApiResponse<AppointmentResponse>.Ok(
                await BuildCreatedResponseAsync(existing, customer, existing.CreatedAt));
        }

        var services = read.Services
            .Where(s => distinctRequested.Contains(s.Id))
            .OrderBy(s => Array.IndexOf(distinctRequested, s.Id))
            .ToList();

        if (services.Count != distinctRequested.Length)
            return ApiResponse<AppointmentResponse>.Fail(
                "SERVICE_NOT_FOUND", "One or more services were not found in this business.");

        var totalMinutes = services.Sum(s => s.DurationMinutes);
        if (totalMinutes <= 0)
            return ApiResponse<AppointmentResponse>.Fail(
                "VALIDATION_ERROR", "The selected services have no duration configured.");

        var startUtc = NormalizeUtc(scheduledAt);
        var endUtc = startUtc.AddMinutes(totalMinutes);

        var business = read.Business;

        // Lead time is the customer's rule, not the operator's: a storefront booking cannot
        // be made in the past or inside the business's minimum notice.
        if (startUtc < DateTime.UtcNow.AddMinutes(Math.Max(0, business.BookingLeadTimeMinutes)))
            return ApiResponse<AppointmentResponse>.Fail(
                AppointmentConflicts.SlotUnavailable,
                "That time has already passed or is too soon to book.");

        var tz = BookingTime.ResolveTimeZone(business.TimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(startUtc, tz);
        var localDate = DateOnly.FromDateTime(local);
        var startMinute = (int)local.TimeOfDay.TotalMinutes;

        var eligible = read.Providers.ToList();
        if (eligible.Count == 0)
            return ApiResponse<AppointmentResponse>.Fail(
                "STAFF_NOT_AVAILABLE", "No staff member performs all of the selected services.");

        if (staffUserId is Guid pinned)
        {
            eligible = eligible.Where(p => p.Id == pinned).ToList();
            if (eligible.Count == 0)
                return ApiResponse<AppointmentResponse>.Fail(
                    "STAFF_NOT_AVAILABLE", "Staff member is not available for all requested services.");
        }

        Guid? chosenStaff = null;
        var bestLoad = int.MaxValue;
        var sawOffDuty = false;
        var sawBooked = false;

        foreach (var staff in eligible)
        {
            // The ENTIRE interval must sit inside one working window — not just its start.
            var windows = read.WindowsFor(staff.Id, localDate);
            if (!windows.Any(w => startMinute >= w.Start && startMinute + totalMinutes <= w.End))
            {
                sawOffDuty = true;
                continue;
            }

            var busy = read.BusyFor(staff.Id);
            // The hold this booking converts is not a competing claim: it IS this booking's claim,
            // already handed over, so it must not read as somebody else's reservation.
            if (convertHoldId is Guid convertingHold)
                busy = busy.Where(b => b.HoldId != convertingHold).ToList();

            if (busy.Any(b => AppointmentReservations.Overlaps(b.StartUtc, b.EndUtc, startUtc, endUtc)))
            {
                sawBooked = true;
                continue;
            }

            if (busy.Count < bestLoad)
            {
                bestLoad = busy.Count;
                chosenStaff = staff.Id;
            }
        }

        if (chosenStaff == null)
            return ApiResponse<AppointmentResponse>.Fail(
                AppointmentConflicts.SlotUnavailable,
                // Off-duty is a different answer from "somebody booked it": the customer
                // should know whether to try another time on the same day or another day.
                sawBooked
                    ? AppointmentConflicts.SlotUnavailableMessage
                    : sawOffDuty
                        ? "Nobody is working at that time. Please pick another slot."
                        : "That slot is not bookable.");

        return await InsertAppointmentTransactionallyAsync(
            business.Id, customerId, chosenStaff, startUtc, endUtc, services, note,
            customerId, Guid.Empty,
            payAfterService: payAfterService,
            business: business,
            customer: customer,
            reminderOffsetMinutesJson: read.ReminderOffsetMinutesJson,
            bookingFeeFactory: bookingFeeFactory,
            bookingRequestKey: bookingRequestKey,
            skipWinBackCancel: read.CustomerMatch == null,
            initialStatus: initialStatus,
            convertHoldId: convertHoldId);
    }

    // ═══════════════════════════════════════════════════════════
    //  CREATE
    // ═══════════════════════════════════════════════════════════

    public async Task<ApiResponse<AppointmentResponse>> CreateAppointmentAsync(
        Guid callerUserId, string role, CreateAppointmentRequest request, bool isGuest = false)
    {
        // Tenant narrowing: on a tenant host the request body must target the active tenant.
        if (TenantBusinessId is Guid activeTenantId && request.BusinessId != activeTenantId)
            return ApiResponse<AppointmentResponse>.Fail("FORBIDDEN", "This business is not available on this site.");

        // Resolve the caller's tenant business id.
        Guid businessId;
        // Which member the booking will occupy. A staff caller is pinned to themselves
        // unless they hold appointments.manage; a customer's explicit choice is honoured.
        var staffForBooking = request.StaffUserId;
        if (IsRole(role, "Business"))
        {
            var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.OwnerId == callerUserId);
            if (business == null)
                return ApiResponse<AppointmentResponse>.Fail("NOT_FOUND", "No business found for this account.");
            if (business.Id != request.BusinessId)
                return ApiResponse<AppointmentResponse>.Fail("FORBIDDEN", "You are not authorized for this business.");
            businessId = business.Id;
        }
        else if (IsRole(role, "Staff"))
        {
            var createGuard = await StaffCreatePermissionGuardAsync(callerUserId, request.BusinessId);
            if (createGuard != null) return createGuard;

            if (!await IsStaffOfBusinessAsync(callerUserId, request.BusinessId))
                return ApiResponse<AppointmentResponse>.Fail("FORBIDDEN", "You are not authorized for this business.");
            businessId = request.BusinessId;

            var assignment = await ResolveStaffCreateAssignmentAsync(
                callerUserId, businessId, request.StaffUserId);
            if (assignment.Error != null) return assignment.Error;
            staffForBooking = assignment.StaffUserId;
        }
        else
        {
            var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.Id == request.BusinessId);
            if (business == null)
                return ApiResponse<AppointmentResponse>.Fail("BUSINESS_NOT_FOUND", "Business not found.");
            businessId = business.Id;
            // Database-first enrollment gate: customers book only where enrolled.
            var enrollment = await _context.CustomerBusinessEnrollments.FirstOrDefaultAsync(e =>
                e.CustomerId == callerUserId && e.BusinessId == businessId &&
                e.Status == CustomerBusinessEnrollmentStatus.Active);
            if (enrollment == null)
                return ApiResponse<AppointmentResponse>.Fail("NOT_ENROLLED", "Enroll in this business before booking.");

            // A booking made while signed in confirms that a real account stands behind
            // the relationship, so a guest enrollment becomes Registered. A guest token
            // (isGuest) never promotes — a guest stays a guest until they sign up.
            if (!isGuest && enrollment.Type == CustomerType.Guest)
            {
                enrollment.Type = CustomerType.Registered;
                enrollment.UpdatedAt = DateTime.UtcNow;
                _unitOfWork.CustomerBusinessEnrollments.Update(enrollment);
                await _unitOfWork.SaveChangesAsync();
            }
        }

        // Customer self-service booking is governed by the business's storefront payment
        // policy. An owner or staff member booking on behalf is not: they are standing at
        // the counter and decide how money is taken themselves (the on-behalf request's
        // own `payAfterService`), so this check applies only to the customer path.
        var payingAfterService = false;
        if (!IsRole(role, "Business") && !IsRole(role, "Staff"))
        {
            // Absent configuration means a business that has never opened the payment
            // screen: the defaults apply (Payment Included on, Pay After Service off).
            var policyConfig = await _context.BusinessPaymentConfigs.AsNoTracking()
                .FirstOrDefaultAsync(c => c.BusinessId == businessId);

            var policy = StorefrontPaymentOptions.Resolve(
                policyConfig?.PayUpfrontEnabled ?? true,
                policyConfig?.PayAfterServiceEnabled ?? false,
                request.PaymentOption);

            if (!policy.Ok)
                return ApiResponse<AppointmentResponse>.Fail(policy.ErrorCode!, policy.ErrorMessage!);

            payingAfterService = policy.Option == StorefrontPaymentOptions.PayAfterService;
        }

        var (services, svcError, svcMsg) = await ValidateServicesAsync(businessId, request.ServiceIds);
        if (svcError != null)
            return ApiResponse<AppointmentResponse>.Fail(svcError, svcMsg!);

        var totalMinutes = services.Sum(s => s.DurationMinutes);

        var (staff, staffError, staffMsg) = await ResolveStaffAsync(businessId, staffForBooking, request.ServiceIds);
        if (staffError != null)
            return ApiResponse<AppointmentResponse>.Fail(staffError, staffMsg!);

        // The slot must still be on the bookable grid. When the customer picked
        // "any available" this also assigns a concrete staff member, so the
        // appointment actually occupies somebody's diary.
        var scheduledAt = NormalizeUtc(request.ScheduledAt);
        var resolution = await _availability.ResolveBookingStaffAsync(
            businessId, request.ServiceIds, staff?.Id, scheduledAt, totalMinutes,
            excludeAppointmentId: null, enforceLeadTime: IsRole(role, "Customer"));
        if (!resolution.Ok)
            return ApiResponse<AppointmentResponse>.Fail(resolution.ErrorCode!, resolution.ErrorMessage!);

        // CustomerId is always forced to the caller for self-service booking.
        var customerId = callerUserId;
        var endAt = scheduledAt.AddMinutes(totalMinutes);

        return await InsertAppointmentTransactionallyAsync(
            businessId, customerId, resolution.StaffUserId, scheduledAt, endAt, services, request.Note,
            callerUserId, Guid.Empty,
            // "Pay after service" writes the appointment's outstanding payment inside the
            // same transaction, so a storefront booking that owes money can never exist
            // without the payment record that says what it owes.
            payAfterService: payingAfterService);
    }

    public async Task<ApiResponse<AppointmentResponse>> CreateAppointmentOnBehalfAsync(
        Guid callerUserId, string role, CreateAppointmentOnBehalfRequest request)
    {
        // Tenant narrowing: on a tenant host the request body must target the active tenant.
        if (TenantBusinessId is Guid activeTenantId && request.BusinessId != activeTenantId)
            return ApiResponse<AppointmentResponse>.Fail("FORBIDDEN", "This business is not available on this site.");

        Guid businessId;
        Business? business = null;
        // Which member the booking will occupy (see CreateAppointmentAsync).
        var staffForBooking = request.StaffUserId;
        if (IsRole(role, "Business"))
        {
            // Loaded once and carried the rest of the way: the availability resolver,
            // the notification payload and the response all used to re-read this same
            // row, which on a remote database was three extra round trips per booking.
            business = await _context.Businesses.AsNoTracking()
                .FirstOrDefaultAsync(b => b.OwnerId == callerUserId);
            if (business == null)
                return ApiResponse<AppointmentResponse>.Fail("NOT_FOUND", "No business found for this account.");
            if (business.Id != request.BusinessId)
                return ApiResponse<AppointmentResponse>.Fail("FORBIDDEN", "You are not authorized for this business.");
            businessId = business.Id;
        }
        else if (IsRole(role, "Staff"))
        {
            var createGuard = await StaffCreatePermissionGuardAsync(callerUserId, request.BusinessId);
            if (createGuard != null) return createGuard;

            if (!await IsStaffOfBusinessAsync(callerUserId, request.BusinessId))
                return ApiResponse<AppointmentResponse>.Fail("FORBIDDEN", "You are not authorized for this business.");
            businessId = request.BusinessId;

            var assignment = await ResolveStaffCreateAssignmentAsync(
                callerUserId, businessId, request.StaffUserId);
            if (assignment.Error != null) return assignment.Error;
            staffForBooking = assignment.StaffUserId;
        }
        else
        {
            return ApiResponse<AppointmentResponse>.Fail("FORBIDDEN", "Only Business or Staff may book on behalf of a customer.");
        }

        var (services, svcError, svcMsg) = await ValidateServicesAsync(businessId, request.ServiceIds);
        if (svcError != null)
            return ApiResponse<AppointmentResponse>.Fail(svcError, svcMsg!);

        var totalMinutes = services.Sum(s => s.DurationMinutes);

        // Customer and enrollment in ONE query. The customer must be a Customer-role,
        // non-deleted user (global filter excludes deleted) who holds an active
        // enrollment in the booked business, so a real customer id from another
        // business is still refused here (IDOR). The enrollment's `Type` comes back
        // with the same row, which is what the response needs to say Guest or
        // Registered without asking again.
        var customerRow = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == request.CustomerId && u.Role == UserRole.Customer)
            .Select(u => new
            {
                u.Id,
                u.FullName,
                u.Email,
                u.PhoneNumber,
                u.AvatarUrl,
                Enrollment = _context.CustomerBusinessEnrollments
                    .Where(e => e.CustomerId == u.Id && e.BusinessId == businessId)
                    .Select(e => new { e.Status, e.Type })
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync();

        if (customerRow?.Enrollment is null ||
            customerRow.Enrollment.Status != CustomerBusinessEnrollmentStatus.Active)
            return ApiResponse<AppointmentResponse>.Fail("CUSTOMER_NOT_FOUND", "Customer not found.");

        var customer = new BookingCustomer(
            customerRow.Id,
            customerRow.FullName,
            customerRow.Email,
            customerRow.PhoneNumber,
            customerRow.AvatarUrl,
            customerRow.Enrollment.Type == CustomerType.Guest ? "guest" : "registered");

        var (staff, staffError, staffMsg) = await ResolveStaffAsync(businessId, staffForBooking, request.ServiceIds);
        if (staffError != null)
            return ApiResponse<AppointmentResponse>.Fail(staffError, staffMsg!);

        // Owners/staff booking on behalf may back-date (walk-ins), so lead time is
        // not enforced — but the slot still has to be on somebody's working grid.
        var scheduledAt = NormalizeUtc(request.ScheduledAt);
        var resolution = await _availability.ResolveBookingStaffAsync(
            businessId, request.ServiceIds, staff?.Id, scheduledAt, totalMinutes,
            excludeAppointmentId: null, enforceLeadTime: false, business: business);
        if (!resolution.Ok)
            return ApiResponse<AppointmentResponse>.Fail(resolution.ErrorCode!, resolution.ErrorMessage!);

        var endAt = scheduledAt.AddMinutes(totalMinutes);

        return await InsertAppointmentTransactionallyAsync(
            businessId, request.CustomerId, resolution.StaffUserId, scheduledAt, endAt, services, request.Note,
            callerUserId, Guid.Empty, request.PayAfterService,
            business: business, customer: customer);
    }

    // ═══════════════════════════════════════════════════════════
    //  RESCHEDULE
    // ═══════════════════════════════════════════════════════════

    public async Task<ApiResponse<AppointmentResponse>> RescheduleAsync(
        Guid callerUserId, string role, Guid appointmentId, RescheduleAppointmentRequest request)
    {
        var appointment = await LoadAsync(appointmentId);
        if (appointment == null)
            return ApiResponse<AppointmentResponse>.Fail("NOT_FOUND", "Appointment not found.");

        var ownershipError = await AssertOwnershipAsync(callerUserId, role, appointment);
        if (ownershipError != null)
            return ownershipError;

        // Staff may reschedule their OWN assigned appointments (ownership is
        // already scoped to StaffUserId == caller). Only creation stays gated.

        // Customers never modify the appointment directly — they submit a
        // reschedule REQUEST that the business must confirm (approve/reject).
        if (IsRole(role, "Customer"))
            return await CreateRescheduleRequestAsync(callerUserId, appointment, request);

        // Determine effective services (replace when provided, else keep current).
        Guid[] serviceIds;
        if (request.ServiceIds != null && request.ServiceIds.Length > 0)
            serviceIds = request.ServiceIds;
        else
            serviceIds = appointment.Resources.Select(r => r.ServiceCatalogItemId).ToArray();

        var (services, svcError, svcMsg) = await ValidateServicesAsync(appointment.BusinessId, serviceIds);
        if (svcError != null)
            return ApiResponse<AppointmentResponse>.Fail(svcError, svcMsg!);

        var totalMinutes = services.Sum(s => s.DurationMinutes);

        var requestedStaffId = request.StaffUserId ?? appointment.StaffUserId;
        var (staff, staffError, staffMsg) = await ResolveStaffAsync(appointment.BusinessId, requestedStaffId, serviceIds);
        if (staffError != null)
            return ApiResponse<AppointmentResponse>.Fail(staffError, staffMsg!);

        var scheduledAt = NormalizeUtc(request.ScheduledAt);
        var resolution = await _availability.ResolveBookingStaffAsync(
            appointment.BusinessId, serviceIds, requestedStaffId, scheduledAt, totalMinutes,
            excludeAppointmentId: appointmentId, enforceLeadTime: false);
        if (!resolution.Ok)
            return ApiResponse<AppointmentResponse>.Fail(resolution.ErrorCode!, resolution.ErrorMessage!);

        var effectiveStaffId = resolution.StaffUserId;
        var endAt = scheduledAt.AddMinutes(totalMinutes);
        var reservingStatuses = AppointmentReservations.ReservingStatuses.ToArray();

        await using (var transaction = await _context.Database.BeginTransactionAsync())
        {
            try
            {
                // Same serialisation as a fresh booking: a reschedule competes for the
                // new staff member's interval with every other booking request.
                await StaffTimeLock.AcquireAsync(_context, appointment.BusinessId, effectiveStaffId);

                if (effectiveStaffId.HasValue)
                {
                    // The appointment being moved is excluded from its own check, so a
                    // reschedule never conflicts with the interval it is leaving.
                    var overlaps = await _context.Appointments
                        .Where(a => a.BusinessId == appointment.BusinessId && a.StaffUserId == effectiveStaffId.Value
                            && reservingStatuses.Contains(a.Status)
                            && a.ScheduledAt < endAt && a.EndAt > scheduledAt && a.Id != appointmentId)
                        .AnyAsync();
                    if (overlaps)
                    {
                        await RollbackQuietlyAsync(transaction);
                        return ApiResponse<AppointmentResponse>.Fail(
                            AppointmentConflicts.StaffTimeUnavailable,
                            AppointmentConflicts.StaffTimeUnavailableMessage);
                    }
                }

                appointment.ScheduledAt = scheduledAt;
                appointment.EndAt = endAt;
                appointment.StaffUserId = effectiveStaffId;
                appointment.Version++;

                if (request.ServiceIds != null && request.ServiceIds.Length > 0)
                {
                    _context.AppointmentResources.RemoveRange(appointment.Resources);
                    var sortOrder = 0;
                    var newResources = new List<AppointmentResource>();
                    foreach (var svc in services)
                    {
                        newResources.Add(new AppointmentResource
                        {
                            Id = Guid.NewGuid(),
                            AppointmentId = appointment.Id,
                            ServiceCatalogItemId = svc.Id,
                            Name = svc.Name,
                            DurationMinutes = svc.DurationMinutes,
                            Price = svc.Price ?? 0,
                            SortOrder = sortOrder++,
                            CreatedAt = DateTime.UtcNow
                        });
                    }

                    // Replace the collection reference wholesale (instead of mutating in place) and
                    // add the snapshots explicitly so EF tracks them as Added rather than Modified.
                    appointment.Resources = newResources;
                    _context.AppointmentResources.AddRange(newResources);
                }

                _unitOfWork.Appointments.Update(appointment);
                // Phase 2: the rewritten reminder ladder + rescheduled event join the same
                // transaction as the time change.
                await RewriteReminderJobsAsync(appointment, _context, default);
                await PublishAppointmentRescheduledAsync(appointment, default);
                await _unitOfWork.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex) when (AppointmentConflicts.IsSlotConflict(ex))
            {
                // The exclusion constraint backstop fired — the new interval was taken
                // between the availability check and this save. The original booking is
                // preserved because the whole move is one transaction.
                AppointmentConflicts.LogConflict(_logger, ex, appointment.BusinessId, effectiveStaffId);
                await RollbackQuietlyAsync(transaction);
                return ApiResponse<AppointmentResponse>.Fail(
                    AppointmentConflicts.StaffTimeUnavailable,
                    AppointmentConflicts.StaffTimeUnavailableMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reschedule appointment {AppointmentId}.", appointmentId);
                await RollbackQuietlyAsync(transaction);
                return ApiResponse<AppointmentResponse>.Fail("RESCHEDULE_FAILED", "Failed to reschedule appointment.");
            }
        }

        // Outside the transaction: the move is committed, so shaping the response can
        // no longer turn a successful reschedule into a reported failure.
        var updated = await LoadAsync(appointmentId);
        return ApiResponse<AppointmentResponse>.Ok(await ToResponseAsync(updated!));
    }

    // ═══════════════════════════════════════════════════════════
    //  RESCHEDULE REQUESTS (customer → business confirmation)
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Customer-initiated reschedule: validates the proposal (real availability),
    /// stores it as a PENDING RescheduleRequest, and notifies the business owner.
    /// The appointment itself is left untouched until the business confirms.
    /// </summary>
    private async Task<ApiResponse<AppointmentResponse>> CreateRescheduleRequestAsync(
        Guid customerId, Appointment appointment, RescheduleAppointmentRequest request)
    {
        if (request.ScheduledAt <= UtcNow)
            return ApiResponse<AppointmentResponse>.Fail("VALIDATION_ERROR", "The new time must be in the future.");

        // Only one open proposal per appointment.
        var open = await _context.RescheduleRequests
            .Where(r => r.AppointmentId == appointment.Id && r.Status == "pending")
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync();
        if (open != null)
        {
            var sameTime = Math.Abs((open.ProposedScheduledAt - request.ScheduledAt).TotalMinutes) < 1;
            if (!sameTime)
                return ApiResponse<AppointmentResponse>.Fail(
                    "RESCHEDULE_PENDING",
                    "A reschedule request is already awaiting confirmation. Wait for the business decision.");
            return ApiResponse<AppointmentResponse>.Ok(await ToResponseAsync(appointment));
        }

        // Determine effective services (replace when provided, else keep current).
        Guid[] serviceIds;
        if (request.ServiceIds != null && request.ServiceIds.Length > 0)
            serviceIds = request.ServiceIds;
        else
            serviceIds = appointment.Resources.Select(r => r.ServiceCatalogItemId).ToArray();

        var (services, svcError, svcMsg) = await ValidateServicesAsync(appointment.BusinessId, serviceIds);
        if (svcError != null)
            return ApiResponse<AppointmentResponse>.Fail(svcError, svcMsg!);

        var requestedStaffId = request.StaffUserId ?? appointment.StaffUserId;
        var (staff, staffError, staffMsg) = await ResolveStaffAsync(appointment.BusinessId, requestedStaffId, serviceIds);
        if (staffError != null)
            return ApiResponse<AppointmentResponse>.Fail(staffError, staffMsg!);

        var totalMinutes = services.Sum(s => s.DurationMinutes);
        var scheduledAt = NormalizeUtc(request.ScheduledAt);
        var endAt = scheduledAt.AddMinutes(totalMinutes);

        // Real availability: the proposal must land on the bookable grid and not
        // collide with another booking, otherwise the owner gets an unapprovable request.
        var resolution = await _availability.ResolveBookingStaffAsync(
            appointment.BusinessId, serviceIds, requestedStaffId, scheduledAt, totalMinutes,
            excludeAppointmentId: appointment.Id, enforceLeadTime: true);
        if (!resolution.Ok)
            return ApiResponse<AppointmentResponse>.Fail(resolution.ErrorCode!, resolution.ErrorMessage!);

        var effectiveStaffId = resolution.StaffUserId;

        var snapshot = services
            .Select((s, i) => new AppointmentServiceSnapshot
            {
                ServiceCatalogItemId = s.Id,
                Name = s.Name,
                DurationMinutes = s.DurationMinutes,
                Price = s.Price ?? 0,
                SortOrder = i
            })
            .ToList();

        var rescheduleRequest = new RescheduleRequest
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            BusinessId = appointment.BusinessId,
            RequestedByUserId = customerId,
            RequestedByParty = RescheduleRequestedByParty.Customer,
            RequestReason = string.IsNullOrWhiteSpace(request.Note) ? null : Truncate(request.Note.Trim(), 300),
            OriginalScheduledAt = appointment.ScheduledAt,
            OriginalEndAt = appointment.EndAt,
            OriginalStaffUserId = appointment.StaffUserId,
            ProposedScheduledAt = scheduledAt,
            ProposedEndAt = endAt,
            ProposedStaffUserId = effectiveStaffId,
            ProposedServicesJson = System.Text.Json.JsonSerializer.Serialize(snapshot),
            Status = "pending",
            ExpiresAt = DateTime.UtcNow.AddDays(RescheduleRequestExpiryDays),
            UpdatedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        await _context.RescheduleRequests.AddAsync(rescheduleRequest);
        await _unitOfWork.SaveChangesAsync();

        // Notify the business owner for confirmation (resolved from the canonical
        // Owner membership, never from the denormalized Business.OwnerId pointer).
        var ownerId = await _context.BusinessMemberships
            .Where(m => m.BusinessId == appointment.BusinessId &&
                        m.Role == BusinessMembershipRole.Owner &&
                        m.Status == BusinessMembershipStatus.Active)
            .Select(m => (Guid?)m.UserId)
            .FirstOrDefaultAsync();
        if (ownerId != null && ownerId != Guid.Empty)
        {
            await SendNotificationSafelyAsync(new NotificationRequest(
                "appointment.reschedule_requested",
                ownerId.Value,
                appointment.BusinessId,
                new Dictionary<string, object?>
                {
                    ["businessName"] = await GetBusinessNameAsync(appointment.BusinessId),
                    ["scheduledAt"] = rescheduleRequest.ProposedScheduledAt.ToString("O"),
                    ["appointmentId"] = appointment.Id
                },
                $"appt:{rescheduleRequest.Id}:reschedule-requested"));
        }

        return ApiResponse<AppointmentResponse>.Ok(await ToResponseAsync(appointment));
    }

    /// <summary>Business owner approves the pending reschedule request and the changes are applied.</summary>
    public async Task<ApiResponse<AppointmentResponse>> ApproveRescheduleRequestAsync(
        Guid callerUserId, string role, Guid appointmentId)
    {
        if (!IsRole(role, "Business") && !IsRole(role, "Staff"))
            return ApiResponse<AppointmentResponse>.Fail(
                "FORBIDDEN", "Only the business or its staff can confirm a reschedule request.");

        var appointment = await LoadAsync(appointmentId);
        if (appointment == null)
            return ApiResponse<AppointmentResponse>.Fail("NOT_FOUND", "Appointment not found.");

        var ownershipError = await AssertOwnershipAsync(callerUserId, role, appointment);
        if (ownershipError != null)
            return ownershipError;

        if (!await _permissionService.HasBusinessPermissionAsync(
                callerUserId, appointment.BusinessId, "appointments.approve_reschedule"))
            return ApiResponse<AppointmentResponse>.Fail(
                "FORBIDDEN", "You are not allowed to approve reschedule requests (appointments.approve_reschedule required).");

        var rescheduleRequest = await LatestPendingRequestAsync(appointmentId);
        if (rescheduleRequest == null)
            return ApiResponse<AppointmentResponse>.Fail("NOT_FOUND", "No pending reschedule request for this appointment.");

        // A proposal that outlived its window is retired, not applied.
        if (HasExpired(rescheduleRequest, UtcNow))
        {
            rescheduleRequest.Status = WireStatus(RescheduleRequestStatus.Expired);
            rescheduleRequest.ResolvedAt = DateTime.UtcNow;
            rescheduleRequest.UpdatedAt = DateTime.UtcNow;
            rescheduleRequest.Version++;
            await _unitOfWork.SaveChangesAsync();
            return ApiResponse<AppointmentResponse>.Fail(
                "RESCHEDULE_EXPIRED", "This reschedule request has expired; the customer must submit a new one.");
        }

        var snapshot = System.Text.Json.JsonSerializer.Deserialize<List<AppointmentServiceSnapshot>>(
            rescheduleRequest.ProposedServicesJson) ?? new List<AppointmentServiceSnapshot>();

        // The slot may have been taken since the customer proposed it.
        var proposedMinutes = (int)(rescheduleRequest.ProposedEndAt - rescheduleRequest.ProposedScheduledAt).TotalMinutes;
        var proposedServiceIds = snapshot.Count > 0
            ? snapshot.Select(s => s.ServiceCatalogItemId).ToArray()
            : appointment.Resources.Select(r => r.ServiceCatalogItemId).ToArray();
        var recheck = await _availability.ResolveBookingStaffAsync(
            appointment.BusinessId, proposedServiceIds, rescheduleRequest.ProposedStaffUserId,
            rescheduleRequest.ProposedScheduledAt, proposedMinutes,
            excludeAppointmentId: appointment.Id, enforceLeadTime: false);
        if (!recheck.Ok)
            return ApiResponse<AppointmentResponse>.Fail(recheck.ErrorCode!, recheck.ErrorMessage!);

        appointment.ScheduledAt = rescheduleRequest.ProposedScheduledAt;
        appointment.EndAt = rescheduleRequest.ProposedEndAt;
        appointment.StaffUserId = recheck.StaffUserId;
        appointment.Version++;

        if (snapshot.Count > 0)
        {
            _context.AppointmentResources.RemoveRange(appointment.Resources);
            var newResources = snapshot.Select((s, i) => new AppointmentResource
            {
                Id = Guid.NewGuid(),
                AppointmentId = appointment.Id,
                ServiceCatalogItemId = s.ServiceCatalogItemId,
                Name = s.Name,
                DurationMinutes = s.DurationMinutes,
                Price = s.Price,
                SortOrder = i,
                CreatedAt = DateTime.UtcNow
            }).ToList();
            appointment.Resources = newResources;
            _context.AppointmentResources.AddRange(newResources);
        }

        _unitOfWork.Appointments.Update(appointment);

        rescheduleRequest.Status = WireStatus(RescheduleRequestStatus.Approved);
        rescheduleRequest.ResolvedAt = DateTime.UtcNow;
        rescheduleRequest.ResolvedByUserId = callerUserId;
        rescheduleRequest.DecisionNotes = "Approved.";
        rescheduleRequest.UpdatedAt = DateTime.UtcNow;
        rescheduleRequest.Version++;

        await _unitOfWork.AppointmentStatusHistory.AddAsync(new AppointmentStatusHistory
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            Status = appointment.Status,
            ChangedAt = DateTime.UtcNow,
            ChangedByUserId = callerUserId,
            Note = "Reschedule request approved",
            CreatedAt = DateTime.UtcNow
        });

        // Phase 2: the rewritten reminder ladder + rescheduled event join the same
        // SaveChanges as the approval.
        await RewriteReminderJobsAsync(appointment, _context, default);
        await PublishAppointmentRescheduledAsync(appointment, default);

        // The customer notice is queued INSIDE this unit of work (intent-level
        // outbox), so a notification outage can never leave a rescheduled
        // appointment whose owner was never told.
        await QueueNotificationSafelyAsync(new NotificationRequest(
            "appointment.reschedule_approved",
            appointment.CustomerId,
            appointment.BusinessId,
            new Dictionary<string, object?>
            {
                ["businessName"] = await GetBusinessNameAsync(appointment.BusinessId),
                ["scheduledAt"] = appointment.ScheduledAt.ToString("O"),
                ["appointmentId"] = appointment.Id
            },
            $"appt:{rescheduleRequest.Id}:reschedule-approved"));

        // The proposal is applied inside a transaction under the staff member's
        // booking lock, exactly like a direct reschedule: approving is a write that
        // competes for the same interval, and the old interval is only released when
        // the new one commits (both live in one row).
        await using (var transaction = await _context.Database.BeginTransactionAsync())
        {
            try
            {
                await StaffTimeLock.AcquireAsync(_context, appointment.BusinessId, appointment.StaffUserId);

                var reservingStatuses = AppointmentReservations.ReservingStatuses.ToArray();
                var approvedStart = appointment.ScheduledAt;
                var approvedEnd = appointment.EndAt;

                if (appointment.StaffUserId.HasValue)
                {
                    var overlaps = await _context.Appointments
                        .Where(a => a.BusinessId == appointment.BusinessId
                            && a.StaffUserId == appointment.StaffUserId.Value
                            && reservingStatuses.Contains(a.Status)
                            && a.ScheduledAt < approvedEnd && a.EndAt > approvedStart
                            && a.Id != appointment.Id)
                        .AnyAsync();
                    if (overlaps)
                    {
                        await RollbackQuietlyAsync(transaction);
                        return ApiResponse<AppointmentResponse>.Fail(
                            AppointmentConflicts.StaffTimeUnavailable,
                            AppointmentConflicts.StaffTimeUnavailableMessage);
                    }
                }

                await _unitOfWork.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex) when (AppointmentConflicts.IsSlotConflict(ex))
            {
                AppointmentConflicts.LogConflict(_logger, ex, appointment.BusinessId, appointment.StaffUserId);
                await RollbackQuietlyAsync(transaction);
                return ApiResponse<AppointmentResponse>.Fail(
                    AppointmentConflicts.StaffTimeUnavailable,
                    AppointmentConflicts.StaffTimeUnavailableMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply the approved reschedule for appointment {AppointmentId}.", appointmentId);
                await RollbackQuietlyAsync(transaction);
                return ApiResponse<AppointmentResponse>.Fail(
                    "RESCHEDULE_FAILED", "Failed to apply the reschedule request.");
            }
        }

        // Built after the commit: the appointment now carries the approved interval.
        var updated = await LoadAsync(appointmentId);
        return ApiResponse<AppointmentResponse>.Ok(await ToResponseAsync(updated!));
    }

    /// <summary>Business owner rejects the pending reschedule request; the appointment is unchanged.</summary>
    public async Task<ApiResponse<AppointmentResponse>> RejectRescheduleRequestAsync(
        Guid callerUserId, string role, Guid appointmentId, string? reason = null)
    {
        if (!IsRole(role, "Business") && !IsRole(role, "Staff"))
            return ApiResponse<AppointmentResponse>.Fail(
                "FORBIDDEN", "Only the business or its staff can reject a reschedule request.");

        var appointment = await LoadAsync(appointmentId);
        if (appointment == null)
            return ApiResponse<AppointmentResponse>.Fail("NOT_FOUND", "Appointment not found.");

        var ownershipError = await AssertOwnershipAsync(callerUserId, role, appointment);
        if (ownershipError != null)
            return ownershipError;

        if (!await _permissionService.HasBusinessPermissionAsync(
                callerUserId, appointment.BusinessId, "appointments.approve_reschedule"))
            return ApiResponse<AppointmentResponse>.Fail(
                "FORBIDDEN", "You are not allowed to reject reschedule requests (appointments.approve_reschedule required).");

        var rescheduleRequest = await LatestPendingRequestAsync(appointmentId);
        if (rescheduleRequest == null)
            return ApiResponse<AppointmentResponse>.Fail("NOT_FOUND", "No pending reschedule request for this appointment.");

        if (HasExpired(rescheduleRequest, UtcNow))
        {
            rescheduleRequest.Status = WireStatus(RescheduleRequestStatus.Expired);
            rescheduleRequest.ResolvedAt = DateTime.UtcNow;
            rescheduleRequest.UpdatedAt = DateTime.UtcNow;
            rescheduleRequest.Version++;
            await _unitOfWork.SaveChangesAsync();
            return ApiResponse<AppointmentResponse>.Fail(
                "RESCHEDULE_EXPIRED", "This reschedule request has expired; the customer must submit a new one.");
        }

        rescheduleRequest.Status = WireStatus(RescheduleRequestStatus.Rejected);
        rescheduleRequest.ResolvedAt = DateTime.UtcNow;
        rescheduleRequest.ResolvedByUserId = callerUserId;
        rescheduleRequest.DecisionNotes = string.IsNullOrWhiteSpace(reason) ? "Rejected." : Truncate(reason.Trim(), 500);
        rescheduleRequest.UpdatedAt = DateTime.UtcNow;
        rescheduleRequest.Version++;

        // The customer notice joins the decision transaction (intent-level outbox).
        await QueueNotificationSafelyAsync(new NotificationRequest(
            "appointment.reschedule_rejected",
            appointment.CustomerId,
            appointment.BusinessId,
            new Dictionary<string, object?>
            {
                ["businessName"] = await GetBusinessNameAsync(appointment.BusinessId),
                ["scheduledAt"] = rescheduleRequest.ProposedScheduledAt.ToString("O"),
                ["appointmentId"] = appointment.Id
            },
            $"appt:{rescheduleRequest.Id}:reschedule-rejected"));

        await _unitOfWork.SaveChangesAsync();

        return ApiResponse<AppointmentResponse>.Ok(await ToResponseAsync(appointment));
    }

    /// <summary>
    /// The requester withdraws their own pending proposal. Only the party that
    /// raised it (or the business) may withdraw; the appointment is unchanged.
    /// </summary>
    public async Task<ApiResponse<AppointmentResponse>> WithdrawRescheduleRequestAsync(
        Guid callerUserId, string role, Guid appointmentId)
    {
        var appointment = await LoadAsync(appointmentId);
        if (appointment == null)
            return ApiResponse<AppointmentResponse>.Fail("NOT_FOUND", "Appointment not found.");

        var ownershipError = await AssertOwnershipAsync(callerUserId, role, appointment);
        if (ownershipError != null)
            return ownershipError;

        var rescheduleRequest = await LatestPendingRequestAsync(appointmentId);
        if (rescheduleRequest == null)
            return ApiResponse<AppointmentResponse>.Fail("NOT_FOUND", "No pending reschedule request for this appointment.");

        // A customer may only withdraw their OWN proposal; business/staff may
        // withdraw any proposal on an appointment they own.
        if (IsRole(role, "Customer") && rescheduleRequest.RequestedByUserId != callerUserId)
            return ApiResponse<AppointmentResponse>.Fail("FORBIDDEN", "You can only withdraw your own reschedule request.");

        var now = DateTime.UtcNow;
        rescheduleRequest.Status = WireStatus(RescheduleRequestStatus.Withdrawn);
        rescheduleRequest.ResolvedAt = now;
        rescheduleRequest.ResolvedByUserId = callerUserId;
        rescheduleRequest.DecisionNotes = "Withdrawn by the requester.";
        rescheduleRequest.UpdatedAt = now;
        rescheduleRequest.Version++;

        // Tell the OTHER side: a customer withdrawing informs the business; a
        // business withdrawing informs the customer.
        var recipient = IsRole(role, "Customer")
            ? await ResolveOwnerUserIdAsync(appointment.BusinessId)
            : appointment.CustomerId;

        if (recipient is Guid recipientId && recipientId != Guid.Empty && recipientId != callerUserId)
        {
            await QueueNotificationSafelyAsync(new NotificationRequest(
                "appointment.reschedule_withdrawn",
                recipientId,
                appointment.BusinessId,
                new Dictionary<string, object?>
                {
                    ["businessName"] = await GetBusinessNameAsync(appointment.BusinessId),
                    ["scheduledAt"] = rescheduleRequest.ProposedScheduledAt.ToString("O"),
                    ["appointmentId"] = appointment.Id
                },
                $"appt:{rescheduleRequest.Id}:reschedule-withdrawn:{recipientId}"));
        }

        await _unitOfWork.SaveChangesAsync();

        return ApiResponse<AppointmentResponse>.Ok(await ToResponseAsync(appointment));
    }

    /// <summary>The canonical active Owner of the business, or null.</summary>
    private async Task<Guid?> ResolveOwnerUserIdAsync(Guid businessId) =>
        await _context.BusinessMemberships
            .Where(m => m.BusinessId == businessId &&
                        m.Role == BusinessMembershipRole.Owner &&
                        m.Status == BusinessMembershipStatus.Active)
            .Select(m => (Guid?)m.UserId)
            .FirstOrDefaultAsync();

    /// <summary>
    /// Moves every undecided reschedule request whose <c>ExpiresAt</c> has passed
    /// to <c>expired</c> and tells both parties.
    ///
    /// <para>This is the only writer of the <c>expired</c> state. It is driven by
    /// <see cref="RescheduleExpiryWorker"/> and is deliberately idempotent: the
    /// selection is filtered on <c>status = 'pending'</c>, so a second sweep
    /// (or a concurrent instance) finds nothing left to do and cannot
    /// double-notify. The appointment itself is untouched — an expired proposal
    /// simply leaves the original schedule in force.</para>
    ///
    /// <para>Returns the number of requests expired.</para>
    /// </summary>
    public async Task<int> ExpireStaleRescheduleRequestsAsync(CancellationToken cancellationToken = default)
    {
        var now = UtcNow;
        var pendingWire = WireStatus(RescheduleRequestStatus.Pending);
        var expiredWire = WireStatus(RescheduleRequestStatus.Expired);

        var stale = await _context.RescheduleRequests
            .Where(r => r.Status == pendingWire && r.ExpiresAt != null && r.ExpiresAt < now)
            .OrderBy(r => r.ExpiresAt)
            .Take(500)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0) return 0;

        // One batched appointment lookup instead of one query per request.
        var appointmentIds = stale.Select(r => r.AppointmentId).Distinct().ToList();
        var appointments = await _context.Appointments
            .Where(a => appointmentIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        var expiredCount = 0;
        foreach (var request in stale)
        {
            // Re-check on the tracked instance: a decision may have landed
            // between the selection and this loop.
            if (!string.Equals(request.Status, pendingWire, StringComparison.Ordinal)) continue;

            request.Status = expiredWire;
            request.ResolvedAt = now;
            request.DecisionNotes = "Expired before a decision was recorded.";
            request.UpdatedAt = now;
            request.Version++;

            if (!appointments.TryGetValue(request.AppointmentId, out var appointment)) continue;

            var businessName = await GetBusinessNameAsync(appointment.BusinessId);
            var data = new Dictionary<string, object?>
            {
                ["businessName"] = businessName,
                ["scheduledAt"] = request.ProposedScheduledAt.ToString("O"),
                ["appointmentId"] = appointment.Id
            };

            // Both sides are told: the requester learns their proposal lapsed and
            // the business learns the slot it was holding is free again.
            var ownerId = await ResolveOwnerUserIdAsync(appointment.BusinessId);
            var recipients = new List<Guid> { appointment.CustomerId };
            if (ownerId is Guid owner && owner != Guid.Empty) recipients.Add(owner);

            foreach (var recipient in recipients.Distinct())
            {
                if (recipient == Guid.Empty) continue;

                await QueueNotificationSafelyAsync(new NotificationRequest(
                    "appointment.reschedule_expired",
                    recipient,
                    appointment.BusinessId,
                    data,
                    $"appt:{request.Id}:reschedule-expired:{recipient}"));
            }

            expiredCount++;
        }

        if (expiredCount > 0)
        {
            await _unitOfWork.SaveChangesAsync();

            _logger.LogInformation(
                "Expired {Count} stale reschedule request(s).", expiredCount);
        }

        return expiredCount;
    }

    // ═══════════════════════════════════════════════════════════
    //  STATUS TRANSITIONS
    // ═══════════════════════════════════════════════════════════

    public async Task<ApiResponse<AppointmentResponse>> CancelAsync(
        Guid callerUserId, string role, Guid appointmentId, CancelAppointmentRequest request)
    {
        var appointment = await LoadAsync(appointmentId);
        if (appointment == null)
            return ApiResponse<AppointmentResponse>.Fail("NOT_FOUND", "Appointment not found.");

        var ownershipError = await AssertOwnershipAsync(callerUserId, role, appointment);
        if (ownershipError != null)
            return ownershipError;

        // Staff may cancel their OWN assigned appointments (ownership is already
        // scoped to StaffUserId == caller). Only creation stays gated.

        return await TransitionAsync(appointment, "cancelled", callerUserId, request.Note, role, staffOrOwnerOnly: false);
    }

    public async Task<ApiResponse<AppointmentResponse>> ConfirmAsync(
        Guid callerUserId, string role, Guid appointmentId)
    {
        var appointment = await LoadAsync(appointmentId);
        if (appointment == null)
            return ApiResponse<AppointmentResponse>.Fail("NOT_FOUND", "Appointment not found.");

        var ownershipError = await AssertOwnershipAsync(callerUserId, role, appointment);
        if (ownershipError != null)
            return ownershipError;

        return await TransitionAsync(appointment, "confirmed", callerUserId, null, role, staffOrOwnerOnly: true);
    }

    public async Task<ApiResponse<AppointmentResponse>> CompleteAsync(
        Guid callerUserId, string role, Guid appointmentId)
    {
        var appointment = await LoadAsync(appointmentId);
        if (appointment == null)
            return ApiResponse<AppointmentResponse>.Fail("NOT_FOUND", "Appointment not found.");

        var ownershipError = await AssertOwnershipAsync(callerUserId, role, appointment);
        if (ownershipError != null)
            return ownershipError;

        return await TransitionAsync(appointment, "completed", callerUserId, null, role, staffOrOwnerOnly: true);
    }

    public async Task<ApiResponse<AppointmentResponse>> MarkNoShowAsync(
        Guid callerUserId, string role, Guid appointmentId)
    {
        var appointment = await LoadAsync(appointmentId);
        if (appointment == null)
            return ApiResponse<AppointmentResponse>.Fail("NOT_FOUND", "Appointment not found.");

        var ownershipError = await AssertOwnershipAsync(callerUserId, role, appointment);
        if (ownershipError != null)
            return ownershipError;

        return await TransitionAsync(appointment, "no_show", callerUserId, null, role, staffOrOwnerOnly: true);
    }


    /// <summary>
    /// Toggles the customer's reminder opt-out on an appointment. Opting out
    /// cancels the appointment's pending reminder jobs; opting back in recreates
    /// the still-future ones. Runs inside the caller's unit of work.
    /// </summary>
    public async Task<ApiResponse<AppointmentResponse>> ToggleReminderOptOutAsync(
        Guid callerUserId, string role, Guid appointmentId, bool optOut)
    {
        var appointment = await LoadAsync(appointmentId);
        if (appointment == null)
            return ApiResponse<AppointmentResponse>.Fail("NOT_FOUND", "Appointment not found.");

        var ownershipError = await AssertOwnershipAsync(callerUserId, role, appointment);
        if (ownershipError != null)
            return ownershipError;

        appointment.ReminderOptOut = optOut;

        if (optOut)
        {
            await CancelPendingJobsAsync(appointment.Id, _context, default);
        }
        else
        {
            await CreatePendingReminderJobsAsync(appointment, _context, default);
        }

        _unitOfWork.Appointments.Update(appointment);
        await _unitOfWork.SaveChangesAsync();

        return ApiResponse<AppointmentResponse>.Ok(await ToResponseAsync(appointment));
    }

    // ═══════════════════════════════════════════════════════════
    //  QUERIES
    // ═══════════════════════════════════════════════════════════

    public async Task<ApiResponse<PaginatedResponse<AppointmentResponse>>> GetCustomerAppointmentsAsync(
        Guid customerId, CustomerAppointmentsQueryRequest request)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize < 1 ? 20 : (request.PageSize > 100 ? 100 : request.PageSize);

        var query = _context.Appointments
            .AsNoTracking()
            .Include(a => a.Resources)
            .Where(a => a.CustomerId == customerId);
        if (TenantBusinessId is Guid tenantId)
            query = query.Where(a => a.BusinessId == tenantId);

        if (request.BusinessId.HasValue)
            query = query.Where(a => a.BusinessId == request.BusinessId.Value);
        if (request.StaffUserId.HasValue)
            query = query.Where(a => a.StaffUserId == request.StaffUserId.Value);
        if (request.ServiceId.HasValue)
            query = query.Where(a => a.Resources.Any(r => r.ServiceCatalogItemId == request.ServiceId.Value));
        if (!string.IsNullOrWhiteSpace(request.Status))
            query = query.Where(a => a.Status == request.Status);
        if (request.From.HasValue)
            query = query.Where(a => a.ScheduledAt >= request.From.Value);
        if (request.To.HasValue)
            query = query.Where(a => a.ScheduledAt <= request.To.Value);

        query = request.SortBy switch
        {
            "oldest" => query.OrderBy(a => a.ScheduledAt),
            "upcoming" => query
                .OrderByDescending(a => a.ScheduledAt >= DateTime.UtcNow)
                .ThenBy(a => a.ScheduledAt),
            _ => query.OrderByDescending(a => a.ScheduledAt), // "newest" (default)
        };

        var total = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var latestChanges = await GetLatestStatusChangesAsync(items.Select(a => a.Id));
        var pendingRequests = await GetPendingRescheduleRequestsAsync(items.Select(a => a.Id));
        var customerTypes = await GetCustomerTypesAsync(items);

        var responses = items
            .Select(a =>
            {
                var r = MapResponse(a, latestChanges);
                r.CustomerType = customerTypes.GetValueOrDefault((a.CustomerId, a.BusinessId), "registered");
                r.PendingReschedule = MapPendingReschedule(pendingRequests.GetValueOrDefault(a.Id));
                return r;
            })
            .ToList();

        return ApiResponse<PaginatedResponse<AppointmentResponse>>.Ok(
            new PaginatedResponse<AppointmentResponse>
            {
                Items = responses,
                TotalCount = total,
                Page = page,
                PageSize = pageSize
            });
    }

    /// <summary>
    /// Distinct filter facets for the customer appointment filter UI — cheap DB projections
    /// (businessId/name, staffUserId/name, service id/name) drawn only from the customer's
    /// own appointments. No full-list load in the browser.
    /// </summary>
    public async Task<ApiResponse<CustomerAppointmentFiltersResponse>> GetCustomerAppointmentFiltersAsync(Guid customerId)
    {
        var filterQuery = _context.Appointments
            .AsNoTracking()
            .Where(a => a.CustomerId == customerId);
        if (TenantBusinessId is Guid tenantId)
            filterQuery = filterQuery.Where(a => a.BusinessId == tenantId);

        var appointments = await filterQuery
            .Select(a => new { a.BusinessId, a.StaffUserId, a.Id })
            .ToListAsync();

        var businessIds = appointments.Select(a => a.BusinessId).Distinct().ToList();
        var staffIds = appointments.Where(a => a.StaffUserId.HasValue)
                                   .Select(a => a.StaffUserId!.Value).Distinct().ToList();

        var businessNames = (await _context.Businesses.AsNoTracking()
            .Where(b => businessIds.Contains(b.Id))
            .Select(b => new { b.Id, b.Name })
            .ToListAsync())
            .Select(r => new AppointmentFilterOption { Id = r.Id, Name = r.Name })
            .ToList();

        var staffNames = (await _context.Users.AsNoTracking()
            .Where(u => staffIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName })
            .ToListAsync())
            .Select(r => new AppointmentFilterOption { Id = r.Id, Name = r.FullName })
            .ToList();

        var resources = await _context.AppointmentResources.AsNoTracking()
            .Where(r => appointments.Select(a => a.Id).Contains(r.AppointmentId))
            .Select(r => new { r.ServiceCatalogItemId, r.Name })
            .ToListAsync();

        var services = resources
            .GroupBy(r => r.ServiceCatalogItemId)
            .Select(g => new AppointmentFilterOption { Id = g.Key, Name = g.First().Name })
            .OrderBy(s => s.Name)
            .ToList();

        return ApiResponse<CustomerAppointmentFiltersResponse>.Ok(
            new CustomerAppointmentFiltersResponse
            {
                Businesses = businessNames.OrderBy(b => b.Name).ToList(),
                Staff = staffNames.OrderBy(s => s.Name).ToList(),
                Services = services
            });
    }

    public async Task<ApiResponse<AppointmentResponse>> GetAppointmentAsync(
        Guid callerUserId, string role, Guid appointmentId)
    {
        var appointment = await LoadAsync(appointmentId);
        if (appointment == null)
            return ApiResponse<AppointmentResponse>.Fail("NOT_FOUND", "Appointment not found.");

        var ownershipError = await AssertOwnershipAsync(callerUserId, role, appointment);
        if (ownershipError != null)
            return ownershipError;

        return ApiResponse<AppointmentResponse>.Ok(await ToResponseAsync(appointment));
    }

    public async Task<ApiResponse<AppointmentResponse>> UpdateNoteAsync(
        Guid callerUserId, string role, Guid appointmentId, string? note)
    {
        var appointment = await LoadAsync(appointmentId);
        if (appointment == null)
            return ApiResponse<AppointmentResponse>.Fail("NOT_FOUND", "Appointment not found.");

        var ownershipError = await AssertOwnershipAsync(callerUserId, role, appointment);
        if (ownershipError != null)
            return ownershipError;

        // Owners and staff may keep notes; customers may not write into the
        // business's working record.
        if (!IsRole(role, "Business") && !IsRole(role, "Staff"))
            return ApiResponse<AppointmentResponse>.Fail(
                "FORBIDDEN", "Only Business or Staff may edit an appointment note.");

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed != null && trimmed.Length > 500)
            trimmed = trimmed[..500];

        var previous = appointment.Note;
        if (string.Equals(previous, trimmed, StringComparison.Ordinal))
            // Nothing changed: no history row, no version bump, no side effect.
            return ApiResponse<AppointmentResponse>.Ok(await ToResponseAsync(appointment));

        appointment.Note = trimmed;
        appointment.Version++;
        _unitOfWork.Appointments.Update(appointment);

        // The edit is part of the appointment's auditable trail, exactly like a
        // status change, so the detail screen's activity feed stays complete.
        await _unitOfWork.AppointmentStatusHistory.AddAsync(new AppointmentStatusHistory
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            Status = appointment.Status,
            ChangedAt = DateTime.UtcNow,
            ChangedByUserId = callerUserId,
            Note = trimmed == null ? "Note cleared" : Truncate($"Note updated: {trimmed}", 300),
            CreatedAt = DateTime.UtcNow
        });

        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation(
            "Appointment {AppointmentId} note updated by {UserId}.", appointment.Id, callerUserId);

        return ApiResponse<AppointmentResponse>.Ok(await ToResponseAsync(appointment));
    }

    public async Task<ApiResponse<List<AppointmentActivityItemResponse>>> GetActivityAsync(
        Guid callerUserId, string role, Guid appointmentId)
    {
        var appointment = await LoadAsync(appointmentId);
        if (appointment == null)
            return ApiResponse<List<AppointmentActivityItemResponse>>.Fail(
                "NOT_FOUND", "Appointment not found.");

        var ownershipError = await AssertOwnershipAsync(callerUserId, role, appointment);
        if (ownershipError != null)
            return ApiResponse<List<AppointmentActivityItemResponse>>.Fail(
                ownershipError.Error?.Code ?? "FORBIDDEN",
                ownershipError.Error?.Message ?? "Not authorized to access this appointment.");

        // Actor names are joined in one projection so the feed reads "Confirmed
        // by Chris Mwangi" without an N+1 across the history rows.
        var rows = await _context.AppointmentStatusHistory
            .AsNoTracking()
            .Where(h => h.AppointmentId == appointment.Id)
            .OrderByDescending(h => h.ChangedAt)
            .Select(h => new
            {
                h.Id,
                h.Status,
                h.ChangedAt,
                h.ChangedByUserId,
                h.Note
            })
            .ToListAsync();

        var actorIds = rows
            .Where(r => r.ChangedByUserId.HasValue)
            .Select(r => r.ChangedByUserId!.Value)
            .Distinct()
            .ToList();

        var actorNames = actorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Users
                .AsNoTracking()
                .Where(u => actorIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName })
                .ToDictionaryAsync(u => u.Id, u => u.FullName);

        var activity = rows.Select(r => new AppointmentActivityItemResponse
        {
            Id = r.Id,
            Status = r.Status,
            ChangedAt = r.ChangedAt,
            ChangedByUserId = r.ChangedByUserId,
            ChangedByName = r.ChangedByUserId.HasValue &&
                            actorNames.TryGetValue(r.ChangedByUserId.Value, out var name)
                ? name
                : null,
            Note = r.Note
        }).ToList();

        return ApiResponse<List<AppointmentActivityItemResponse>>.Ok(activity);
    }

    public async Task<ApiResponse<PaginatedResponse<AppointmentResponse>>> GetBusinessAppointmentsAsync(
        Guid ownerUserId, string? status, DateTime? from, DateTime? to, Guid? staffUserId, Guid? customerId, Guid? serviceId, int page, int pageSize)
    {
        var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.OwnerId == ownerUserId);
        if (business == null)
            return ApiResponse<PaginatedResponse<AppointmentResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? 20 : (pageSize > 100 ? 100 : pageSize);

        var query = _context.Appointments
            .Include(a => a.Resources)
            .Where(a => a.BusinessId == business.Id);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(a => a.Status == status);
        if (from.HasValue)
            query = query.Where(a => a.ScheduledAt >= from.Value);
        if (to.HasValue)
            query = query.Where(a => a.ScheduledAt <= to.Value);
        // The Staff filter is honoured only while the optional module is on.
        if (staffUserId.HasValue && await IsStaffModuleEnabledAsync(business.Id))
            query = query.Where(a => a.StaffUserId == staffUserId.Value);
        if (customerId.HasValue)
            query = query.Where(a => a.CustomerId == customerId.Value);
        if (serviceId.HasValue)
            query = query.Where(a => a.Resources.Any(r => r.ServiceCatalogItemId == serviceId.Value));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.ScheduledAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var latestChanges = await GetLatestStatusChangesAsync(items.Select(a => a.Id));
        var pendingRequests = await GetPendingRescheduleRequestsAsync(items.Select(a => a.Id));
        var customerTypes = await GetCustomerTypesAsync(items);
        var responses = items
            .Select(a =>
            {
                var r = MapResponse(a, latestChanges);
                r.CustomerType = customerTypes.GetValueOrDefault((a.CustomerId, a.BusinessId), "registered");
                r.PendingReschedule = MapPendingReschedule(pendingRequests.GetValueOrDefault(a.Id));
                return r;
            })
            .ToList();


        return ApiResponse<PaginatedResponse<AppointmentResponse>>.Ok(new PaginatedResponse<AppointmentResponse>
        {
            Items = responses,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        });
    }

    public async Task<ApiResponse<AppointmentDashboardResponse>> GetAppointmentDashboardAsync(
        Guid ownerUserId,
        DateTime? from,
        DateTime? to,
        Guid? staffUserId,
        Guid? customerId,
        Guid? serviceId,
        string? status,
        string? search,
        string sortBy,
        string sortDirection,
        int page,
        int pageSize,
        Guid[]? staffIds = null,
        Guid[]? customerIds = null,
        Guid[]? serviceIds = null,
        string? paymentStatus = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Resolve the owner's business (server-side scoping only; never trusted from a request DTO).
        var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.OwnerId == ownerUserId);
        if (business == null)
            return ApiResponse<AppointmentDashboardResponse>.Fail("NOT_FOUND", "No business found for this account.");

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // 2. Base query. Read-only projection work (the rows are mapped by hand below),
        //    so no change tracker: a page of appointments is cheaper to materialise and
        //    the identity map is not needed for anything on this path.
        var query = _context.Appointments
            .AsNoTracking()
            .Include(a => a.Resources)
            .Where(a => a.BusinessId == business.Id);

        // 2b. Optional Staff module. When it is off, the staff filter/search must
        // be inert on the server too: a client that sends `staffId` cannot filter
        // by a control the business no longer has.
        var staffModuleEnabled = await IsStaffModuleEnabledAsync(business.Id);

        // 3. Filters. The multi-select arrays are merged with the single-value
        //    parameters the older callers still send, so one filter can never be
        //    silently dropped because a client used the other shape.
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(a => a.Status == status);
        if (from.HasValue)
            query = query.Where(a => a.ScheduledAt >= from.Value);
        if (to.HasValue)
            query = query.Where(a => a.ScheduledAt <= to.Value);

        var staffFilter = Merge(staffIds, staffUserId);
        if (staffModuleEnabled && staffFilter.Length > 0)
            query = query.Where(a => a.StaffUserId != null && staffFilter.Contains(a.StaffUserId.Value));

        var customerFilter = Merge(customerIds, customerId);
        if (customerFilter.Length > 0)
            query = query.Where(a => customerFilter.Contains(a.CustomerId));

        var serviceFilter = Merge(serviceIds, serviceId);
        if (serviceFilter.Length > 0)
            query = query.Where(a => a.Resources.Any(r => serviceFilter.Contains(r.ServiceCatalogItemId)));

        // 3b. Payment-verification filter. Payment is not a column on the appointment —
        //     one appointment can hold several rows (an upfront booking fee, a later
        //     balance, a refund) — so this is answered from the LEDGER. The appointment
        //     ids are taken first, then only the scalars the rule needs (a booked price,
        //     a payment status, an amount) are read and summed in ONE pass here in the
        //     service. No appointment and no payment entity is materialised, and nothing
        //     is filtered in the browser. (The sum is done in C# rather than SQL because
        //     SUM over a decimal column is not portable across providers.)
        var paymentFilter = NormalizePaymentFilter(paymentStatus);
        if (paymentFilter is not null)
        {
            var candidateIds = await query.Select(a => a.Id).ToListAsync(cancellationToken);
            var ledger = await BuildPaymentTotalsAsync(candidateIds, cancellationToken);

            var matchingIds = ledger
                .Where(entry => PaymentFilterMatches(
                    paymentFilter, entry.Value.Expected, entry.Value.Settled,
                    entry.Value.Awaiting, entry.Value.Failed, entry.Value.Refunded))
                .Select(entry => entry.Key)
                .ToList();

            query = query.Where(a => matchingIds.Contains(a.Id));
        }

        // 4. Search. Everything is decided by the database in ONE round trip: each
        //    criterion becomes a correlated EXISTS/IN subquery, so no id list is
        //    pulled into memory (the previous shape fetched every matching user in
        //    the platform, then sent the ids back as an IN clause).
        //
        //    `ToLower().Contains(…)` is used rather than `ToLowerInvariant()`: EF has
        //    no SQL translation for the invariant overloads, so a search used to throw
        //    "could not be translated" and fail the whole list request with a 500.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var needle = term.ToLowerInvariant();
            // Phones are stored normalised (2547…), so a term typed the way a Kenyan
            // number is written ("0712 345 678") has to be compared as digits. Nine or
            // more digits are matched as a suffix — that covers every national form of
            // the same number — and shorter digit runs as a substring.
            var phoneDigits = new string(term.Where(char.IsDigit).ToArray());
            var phoneContains = phoneDigits.Length is >= 3 and < 9 ? phoneDigits : string.Empty;
            var phoneSuffix = phoneDigits.Length >= 9 ? phoneDigits[^9..] : string.Empty;

            var exactId = AppointmentReferences.TryParseId(term);
            var hasPrefixRange = AppointmentReferences.TryParsePrefixRange(term, out var range);

            query = query.Where(a =>
                // The customer on the booking — name, email or phone.
                _context.Users.Any(u => u.Id == a.CustomerId &&
                    (u.FullName.ToLower().Contains(needle) ||
                     (u.Email != null && u.Email.ToLower().Contains(needle)) ||
                     (u.PhoneNumber != null && (u.PhoneNumber.Contains(term) ||
                                                (phoneContains != "" && u.PhoneNumber.Contains(phoneContains)) ||
                                                (phoneSuffix != "" && u.PhoneNumber.EndsWith(phoneSuffix))))))
                // The booked services, by their snapshot name.
                || a.Resources.Any(r => r.Name.ToLower().Contains(needle))
                // The assigned professional. Only a criterion while the Staff module is
                // on: with it off, a term never matches on a staff member's name.
                || (staffModuleEnabled && a.StaffUserId != null && _context.Users.Any(u => u.Id == a.StaffUserId &&
                    (u.FullName.ToLower().Contains(needle) ||
                     (u.Email != null && u.Email.ToLower().Contains(needle)) ||
                     (u.PhoneNumber != null && u.PhoneNumber.Contains(term)))))
                // The appointment's own reference (`AP-3F2A9C41`, or just `3F2A`), or the
                // whole id when it was pasted.
                || (exactId != null && a.Id == exactId)
                || (hasPrefixRange && a.Id >= range.Low && a.Id < range.High));
        }

        // 5. Sorting. `scheduledAt` (the calendar/schedule order) and `createdAt` (the
        //    "what came in just now" order) are both native SQL. Id breaks ties so a
        //    page boundary can never repeat or skip a row.
        var direction = sortDirection.Trim().ToLowerInvariant() == "asc" ? "asc" : "desc";
        var sortKey = sortBy.Trim().ToLowerInvariant();
        query = sortKey switch
        {
            "status" => direction == "asc"
                ? query.OrderBy(a => a.Status).ThenBy(a => a.Id)
                : query.OrderByDescending(a => a.Status).ThenByDescending(a => a.Id),
            "createdat" or "recent" or "created" => direction == "asc"
                ? query.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                : query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id),
            _ => direction == "asc"
                ? query.OrderBy(a => a.ScheduledAt).ThenBy(a => a.Id)
                : query.OrderByDescending(a => a.ScheduledAt).ThenByDescending(a => a.Id)
        };

        // 6. Count + paginated items (with resources and service names already included).
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // 7. Batch resolve customer / staff names (Appointment has no User navigation).
        var pageCustomerIds = items.Select(a => a.CustomerId).Distinct().ToList();
        var pageStaffIds = items.Select(a => a.StaffUserId).Where(g => g.HasValue).Distinct().ToList();

        var customerLookup = (await _context.Users.AsNoTracking()
            .Where(u => pageCustomerIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, u.Email, u.PhoneNumber, u.AvatarUrl })
            .ToListAsync(cancellationToken))
            .GroupBy(u => u.Id)
            .ToDictionary(g => g.Key, g => g.First());

        // How each customer belongs to this business — Guest or Registered — in one
        // query, so the roster rows can show the badge without a per-row lookup.
        var customerTypeLookup = (await _context.CustomerBusinessEnrollments.AsNoTracking()
            .Where(e => e.BusinessId == business.Id && pageCustomerIds.Contains(e.CustomerId))
            .Select(e => new { e.CustomerId, e.Type })
            .ToListAsync(cancellationToken))
            .ToDictionary(e => e.CustomerId, e => e.Type == CustomerType.Guest ? "guest" : "registered");

        var staffLookup = (await _context.Users.AsNoTracking()
            .Where(u => pageStaffIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, u.Email, u.PhoneNumber, u.AvatarUrl })
            .ToListAsync(cancellationToken))
            .GroupBy(u => u.Id)
            .ToDictionary(g => g.Key, g => g.First());

        var appointmentIds = items.Select(a => a.Id).ToList();
        var serviceLookup = (await _context.AppointmentResources.AsNoTracking()
            .Where(r => appointmentIds.Contains(r.AppointmentId))
            .Join(_context.ServiceCatalogItems,
                r => r.ServiceCatalogItemId,
                s => s.Id,
                (r, s) => new { r.AppointmentId, s.Name })
            .ToListAsync(cancellationToken))
            .GroupBy(x => x.AppointmentId)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.Name)));

        // 8b. The page's payment ledger, aggregated for the rows on screen. The booked
        //     prices are already on the loaded items, so the aggregation does not need to
        //     read them again — one query fewer per list read. The same ledger rules as
        //     the filter: settled means provider-confirmed (`Success`) or owner-verified
        //     (`Verified`); a claim awaiting verification, a failed/expired/cancelled
        //     attempt, a refund/reversal and an outstanding balance row (`Created`) are
        //     all excluded.
        var bookedTotals = items.ToDictionary(a => a.Id, a => a.Resources.Sum(r => r.Price));
        var paymentTotals = await BuildPaymentTotalsAsync(appointmentIds, cancellationToken, bookedTotals);

        // 8c. Project the paginated appointment items.
        var appointments = items.Select(a =>
        {
            var totals = paymentTotals.GetValueOrDefault(a.Id);
            return new AppointmentDashboardItemResponse
            {
                Id = a.Id,
                BusinessId = a.BusinessId,
                ScheduledAt = a.ScheduledAt,
                EndAt = a.EndAt,
                Status = a.Status,
                ServiceId = a.Resources.FirstOrDefault()?.ServiceCatalogItemId ?? Guid.Empty,
                ServiceName = a.Resources.FirstOrDefault()?.Name ?? string.Empty,
                CustomerId = a.CustomerId,
                CustomerType = customerTypeLookup.GetValueOrDefault(a.CustomerId, "registered"),
                CustomerName = customerLookup.TryGetValue(a.CustomerId, out var c) ? c.FullName : string.Empty,
                CustomerEmail = customerLookup.TryGetValue(a.CustomerId, out c) ? c.Email : null,
                CustomerPhone = customerLookup.TryGetValue(a.CustomerId, out c) ? c.PhoneNumber : null,
                CustomerAvatarUrl = customerLookup.TryGetValue(a.CustomerId, out c) ? c.AvatarUrl : null,
                StaffUserId = a.StaffUserId,
                StaffName = a.StaffUserId.HasValue && staffLookup.TryGetValue(a.StaffUserId.Value, out var s) ? s.FullName : null,
                StaffEmail = a.StaffUserId.HasValue && staffLookup.TryGetValue(a.StaffUserId.Value, out s) ? s.Email : null,
                StaffAvatarUrl = a.StaffUserId.HasValue && staffLookup.TryGetValue(a.StaffUserId.Value, out s) ? s.AvatarUrl : null,
                CreatedAt = a.CreatedAt,
                UpdatedAt = a.CreatedAt,
                PaymentStatus = ClassifyPayment(totals.Expected, totals.Settled, totals.Awaiting, totals.Failed, totals.Refunded),
                AmountPaid = totals.Settled,
                Balance = Math.Max(0m, totals.Expected - totals.Settled)
            };
        }).ToList();

        // 9. Reference data for the filter drawer. Staff and services only: both are bounded by
        //    what the business sells, and both are needed to render the drawer without a second
        //    call. Customers are NOT here — that list grows without bound, so the customer
        //    pickers search it server-side instead (see AppointmentDashboardResponse).
        //    The staff bucket is only built — and only paid for — when the optional Staff module
        //    is on. With it off the dashboard returns an empty list.
        var staff = staffModuleEnabled
            ? await _context.Users.AsNoTracking()
                .Where(u => _context.BusinessMemberships.Any(m =>
                    m.UserId == u.Id &&
                    m.BusinessId == business.Id &&
                    m.Role == BusinessMembershipRole.Staff &&
                    m.Status == BusinessMembershipStatus.Active))
                .OrderBy(u => u.FullName)
                .Select(u => new StaffDashboardItemResponse
                {
                    UserId = u.Id,
                    FullName = u.FullName,
                    Email = u.Email,
                    AvatarUrl = u.AvatarUrl,
                    PhoneNumber = u.PhoneNumber
                })
                .ToListAsync(cancellationToken)
            : new List<StaffDashboardItemResponse>();

        var services = await _context.ServiceCatalogItems.AsNoTracking()
            .Where(s => s.BusinessId == business.Id)
            .OrderBy(s => s.Name)
            .Select(s => new ServiceDashboardItemResponse
            {
                Id = s.Id,
                Name = s.Name,
                Description = s.Description,
                DurationMinutes = s.DurationMinutes,
                Price = s.Price ?? 0,
                IsActive = s.IsActive,
                Showcase = s.Showcase
            })
            .ToListAsync(cancellationToken);

        // 10. Assemble the response.
        var totalPages = total > 0 ? (int)Math.Ceiling(total / (double)pageSize) : 1;
        return ApiResponse<AppointmentDashboardResponse>.Ok(new AppointmentDashboardResponse
        {
            Meta = new PaginationMeta
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = total,
                TotalPages = totalPages
            },
            Appointments = appointments,
            Staff = staff,
            StaffModuleEnabled = staffModuleEnabled,
            Services = services
        });
    }

    public async Task<ApiResponse<PaginatedResponse<AppointmentResponse>>> GetStaffAppointmentsAsync(
        Guid staffUserId, string? status, string? search, string? priceFilter, string? sort,
        DateTime? from, DateTime? to, int page, int pageSize)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? 20 : (pageSize > 100 ? 100 : pageSize);

        var query = _context.Appointments
            .AsNoTracking()
            .Include(a => a.Resources)
            .Where(a => a.StaffUserId == staffUserId);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(a => a.Status == status);
        if (from.HasValue)
            query = query.Where(a => a.ScheduledAt >= from.Value);
        if (to.HasValue)
            query = query.Where(a => a.ScheduledAt <= to.Value);

        // Search across the customer name and the booked service names.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var matchingCustomerIds = await _context.Users.AsNoTracking()
                .Where(u => u.FullName.ToLower().Contains(term.ToLower()))
                .Select(u => u.Id)
                .ToListAsync();

            query = query.Where(a =>
                matchingCustomerIds.Contains(a.CustomerId) ||
                a.Resources.Any(r => r.Name.ToLower().Contains(term.ToLower())));
        }

        // Server-side price band filter (sum of booked service prices).
        var price = priceFilter?.Trim();
        if (price == "free")
            query = query.Where(a => a.Resources.Sum(r => r.Price) == 0);
        else if (price == "under-1000")
            query = query.Where(a => a.Resources.Sum(r => r.Price) > 0 && a.Resources.Sum(r => r.Price) < 1000);
        else if (price == "1000-5000")
            query = query.Where(a => a.Resources.Sum(r => r.Price) >= 1000 && a.Resources.Sum(r => r.Price) <= 5000);
        else if (price == "over-5000")
            query = query.Where(a => a.Resources.Sum(r => r.Price) > 5000);

        query = sort switch
        {
            "time-desc" => query.OrderByDescending(a => a.ScheduledAt),
            "price-desc" => query.OrderByDescending(a => a.Resources.Sum(r => r.Price)),
            "price-asc" => query.OrderBy(a => a.Resources.Sum(r => r.Price)),
            _ => query.OrderBy(a => a.ScheduledAt), // "time-asc" (default)
        };

        var total = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var latestChanges = await GetLatestStatusChangesAsync(items.Select(a => a.Id));
        var pendingRequests = await GetPendingRescheduleRequestsAsync(items.Select(a => a.Id));
        var customerTypes = await GetCustomerTypesAsync(items);
        var responses = items
            .Select(a =>
            {
                var r = MapResponse(a, latestChanges);
                r.CustomerType = customerTypes.GetValueOrDefault((a.CustomerId, a.BusinessId), "registered");
                r.PendingReschedule = MapPendingReschedule(pendingRequests.GetValueOrDefault(a.Id));
                return r;
            })
            .ToList();

        return ApiResponse<PaginatedResponse<AppointmentResponse>>.Ok(new PaginatedResponse<AppointmentResponse>
        {
            Items = responses,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        });
    }

    /// <summary>
    /// Staff: book an appointment for themselves at their linked business.
    /// StaffUserId and CustomerId are forced to the caller (never another staff
    /// member), services/staff are validated, and the availability engine — the
    /// backend source of truth — must return the slot as bookable, with the
    /// same lead-time rules customers are subject to.
    /// </summary>
    public async Task<ApiResponse<AppointmentResponse>> CreateStaffSelfAppointmentAsync(
        Guid staffUserId, CreateAppointmentRequest request)
    {
        var staffBusinessId = await GetActiveStaffBusinessIdAsync(staffUserId);
        if (staffBusinessId == null)
            return ApiResponse<AppointmentResponse>.Fail("NOT_LINKED", "You are not linked to any business.");

        var businessId = staffBusinessId.Value;

        var (services, svcError, svcMsg) = await ValidateServicesAsync(businessId, request.ServiceIds);
        if (svcError != null)
            return ApiResponse<AppointmentResponse>.Fail(svcError, svcMsg!);

        var totalMinutes = services.Sum(s => s.DurationMinutes);

        // Staff is ALWAYS the caller — the request can never assign another staff member.
        var (_, staffError, staffMsg) = await ResolveStaffAsync(businessId, staffUserId, request.ServiceIds);
        if (staffError != null)
            return ApiResponse<AppointmentResponse>.Fail(staffError, staffMsg!);

        var scheduledAt = NormalizeUtc(request.ScheduledAt);
        var resolution = await _availability.ResolveBookingStaffAsync(
            businessId, request.ServiceIds, staffUserId, scheduledAt, totalMinutes,
            excludeAppointmentId: null, enforceLeadTime: true);
        if (!resolution.Ok)
            return ApiResponse<AppointmentResponse>.Fail(resolution.ErrorCode!, resolution.ErrorMessage!);

        // The staff member books themselves as the customer of the appointment.
        var endAt = scheduledAt.AddMinutes(totalMinutes);

        return await InsertAppointmentTransactionallyAsync(
            businessId, staffUserId, staffUserId, scheduledAt, endAt, services, request.Note, staffUserId, Guid.Empty);
    }

    /// <summary>
    /// Staff: the appointments a specific customer has at the staff member's
    /// own linked business (read-only history used by the staff customer
    /// detail page). Reuses the customer appointment query, scoped to the
    /// caller's business — no cross-tenant leakage.
    /// </summary>
    public async Task<ApiResponse<PaginatedResponse<AppointmentResponse>>> GetStaffCustomerAppointmentsAsync(
        Guid staffUserId, Guid customerId, int page, int pageSize)
    {
        var staffBusinessId = await GetActiveStaffBusinessIdAsync(staffUserId);

        if (staffBusinessId == null)
            return ApiResponse<PaginatedResponse<AppointmentResponse>>.Fail(
                "NOT_LINKED", "You are not linked to any business.");

        return await GetCustomerAppointmentsAsync(customerId, new CustomerAppointmentsQueryRequest
        {
            BusinessId = staffBusinessId.Value,
            Page = page,
            PageSize = pageSize,
            SortBy = "upcoming"
        });
    }

    // ═══════════════════════════════════════════════════════════
    //  PRIVATE HELPERS
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// The status a new booking starts in.
    ///
    /// <para><c>booked</c> is the ordinary case: a booking is confirmed by the business's own rules
    /// about time and availability, and whether money has been verified is a separate fact about
    /// money, reported on the payment and shown next to the appointment.</para>
    ///
    /// <para>It becomes <c>pending_payment</c> only when the customer's booking fee has been
    /// <i>submitted</i> and is awaiting the business's verification. That is a different fact from
    /// "booked": the slot is held by a paying customer whose money nobody has checked yet, and the
    /// lifecycle must show it as such rather than as a confirmed appointment. Verification then
    /// moves it to <c>confirmed</c> (see <c>PaymentService.AdvanceAppointmentAfterVerificationAsync</c>);
    /// a rejection or a lapsed reservation releases it.</para>
    /// </summary>
    private static string InitialStatusFor(string? requested) =>
        string.Equals(requested, AppointmentStatuses.PendingPayment, StringComparison.Ordinal)
            ? AppointmentStatuses.PendingPayment
            : AppointmentStatuses.Booked;

    private async Task<ApiResponse<AppointmentResponse>> InsertAppointmentTransactionallyAsync(
        Guid businessId, Guid customerId, Guid? staffUserId, DateTime scheduledAt, DateTime endAt,
        List<ServiceCatalogItem> services, string? note, Guid changedByUserId, Guid excludeId,
        bool payAfterService = false,
        Business? business = null,
        BookingCustomer? customer = null,
        // Pre-read reminder ladder (the booking fast path reads it in its parallel wave).
        string? reminderOffsetMinutesJson = null,
        // A booking-fee row written in the SAME transaction as the appointment. The factory
        // receives the appointment id, so the fee can only ever point at the row it pays for.
        Func<Guid, Payment?>? bookingFeeFactory = null,
        // The client's booking-intent key: a repeat returns the booking that already exists.
        string? bookingRequestKey = null,
        // A customer with no prior relationship cannot have pending win-back jobs, so the
        // reactivation sweep is skipped for them (one round trip saved on a first booking).
        bool skipWinBackCancel = false,
        // The status the booking should start in (see InitialStatusFor).
        string? initialStatus = null,
        // The hold this booking converts. Consumed in the SAME transaction, so a booking can never
        // exist while its hold still claims the interval, and a replayed hold can never book twice.
        Guid? convertHoldId = null)
    {
        var reservingStatuses = AppointmentReservations.ReservingStatuses.ToArray();
        Guid appointmentId;
        DateTime statusChangedAt = DateTime.UtcNow;

        await using (var transaction = await _context.Database.BeginTransactionAsync())
        {
            var writeWatch = System.Diagnostics.Stopwatch.StartNew();
            long lockMs = 0, keyMs = 0, overlapMs = 0, prepareMs = 0, saveMs = 0;
            try
            {
                // Serialise bookings for this staff member BEFORE re-checking, so two
                // simultaneous requests cannot both pass the check. The loser waits
                // here for the winner's commit and then sees the new row below.
                await StaffTimeLock.AcquireAsync(_context, businessId, staffUserId);
                lockMs = writeWatch.ElapsedMilliseconds;

                // The hold being converted is re-read under the lock and must still be live. The
                // frontend's countdown is a courtesy; this is the authority: an expired or already
                // consumed hold cannot become a booking, however the request was formed.
                AppointmentSlotHold? hold = null;
                if (convertHoldId is Guid holdId)
                {
                    hold = await _context.AppointmentSlotHolds
                        .FirstOrDefaultAsync(h => h.Id == holdId && h.BusinessId == businessId);

                    if (hold == null
                        || !string.Equals(hold.Status, AppointmentSlotHoldStatuses.Active, StringComparison.Ordinal)
                        || hold.ExpiresAtUtc <= DateTime.UtcNow)
                    {
                        await RollbackQuietlyAsync(transaction);
                        return ApiResponse<AppointmentResponse>.Fail(
                            "RESERVATION_EXPIRED",
                            "Your held slot has expired. Please choose a time again.");
                    }

                    // The hold is the appointment's own interval: the request cannot move it.
                    if (hold.StaffUserId != staffUserId
                        || hold.StartsAtUtc != scheduledAt
                        || hold.EndsAtUtc != endAt)
                    {
                        await RollbackQuietlyAsync(transaction);
                        return ApiResponse<AppointmentResponse>.Fail(
                            "RESERVATION_MISMATCH",
                            "That booking does not match the slot that was held. Please start again.");
                    }
                }

                // Idempotency is answered by the snapshot the booking was validated
                // against (the existing booking for this intent key) and, if two identical
                // submissions race past that read, by the filtered unique index on
                // (business_id, booking_request_key) — handled below as a replay. No extra
                // round trip is spent asking again here.

                if (staffUserId.HasValue)
                {
                    // Revalidated inside the transaction and under the lock. The
                    // half-open interval test (existingStart < requestedEnd &&
                    // existingEnd > requestedStart) is the same rule the database
                    // exclusion constraint enforces, so back-to-back bookings stay
                    // legal while every genuine overlap is refused.
                    var overlaps = await _context.Appointments
                        .Where(a => a.BusinessId == businessId && a.StaffUserId == staffUserId.Value
                            && reservingStatuses.Contains(a.Status)
                            && a.ScheduledAt < endAt && a.EndAt > scheduledAt && a.Id != excludeId)
                        .AnyAsync();
                    if (overlaps)
                    {
                        await RollbackQuietlyAsync(transaction);
                        return ApiResponse<AppointmentResponse>.Fail(
                            AppointmentConflicts.StaffTimeUnavailable,
                            AppointmentConflicts.StaffTimeUnavailableMessage);
                    }

                    overlapMs = writeWatch.ElapsedMilliseconds - lockMs - keyMs;
                }

                var appointment = new Appointment
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    CustomerId = customerId,
                    StaffUserId = staffUserId,
                    ScheduledAt = scheduledAt,
                    EndAt = endAt,
                    Status = InitialStatusFor(initialStatus),
                    CreatedAt = DateTime.UtcNow,
                    BookingRequestKey = string.IsNullOrWhiteSpace(bookingRequestKey) ? null : bookingRequestKey
                };

                // The hold becomes this booking in the same transaction: the claim on the interval
                // hands over from the reservation to the appointment, so neither a gap nor a
                // double-claim can exist between them.
                if (hold != null)
                {
                    hold.Status = AppointmentSlotHoldStatuses.Converted;
                    hold.ConvertedAppointmentId = appointment.Id;
                    hold.UpdatedAt = DateTime.UtcNow;
                }

                var sortOrder = 0;
                foreach (var svc in services)
                {
                    appointment.Resources.Add(new AppointmentResource
                    {
                        Id = Guid.NewGuid(),
                        AppointmentId = appointment.Id,
                        ServiceCatalogItemId = svc.Id,
                        Name = svc.Name,
                        DurationMinutes = svc.DurationMinutes,
                        Price = svc.Price ?? 0,
                        SortOrder = sortOrder++,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                await _unitOfWork.Appointments.AddAsync(appointment);

                // A booking fee travels with the appointment: both rows are written by the one
                // SaveChanges inside this transaction, so a paid-for slot can never exist
                // without the payment that says what it is for.
                if (bookingFeeFactory?.Invoke(appointment.Id) is Payment bookingFee)
                    await _context.Payments.AddAsync(bookingFee);

                // "Pay after service": the appointment's whole value becomes an outstanding
                // payment, written inside the SAME transaction as the row it belongs to. If it
                // cannot be written, the booking does not exist either — the two can never
                // disagree about what is owed. Because the amount is the server's own sum of the
                // service snapshots, the operator cannot set it.
                if (payAfterService)
                {
                    var outstandingAmount = services.Sum(s => s.Price ?? 0);
                    if (outstandingAmount > 0)
                        await _context.Payments.AddAsync(PaymentOutstanding.Create(
                            businessId,
                            customerId,
                            appointment.Id,
                            outstandingAmount,
                            changedByUserId == Guid.Empty ? null : changedByUserId,
                            DateTime.UtcNow));
                }

                var historyChangedAt = DateTime.UtcNow;
                await _unitOfWork.AppointmentStatusHistory.AddAsync(new AppointmentStatusHistory
                {
                    Id = Guid.NewGuid(),
                    AppointmentId = appointment.Id,
                    Status = appointment.Status,
                    ChangedAt = historyChangedAt,
                    ChangedByUserId = changedByUserId,
                    Note = note,
                    CreatedAt = historyChangedAt
                });

                // Phase 1: the booking-confirmation intent joins this transaction — it exists if
                // and only if the appointment row does (intent-level outbox). Both notices are
                // queued in ONE batch: the customer's confirmation and the assigned
                // professional's, so the tenant-guard and idempotency reads are paid once.
                var businessName = business?.Name ?? await GetBusinessNameAsync(appointment.BusinessId);
                var bookingIntents = new List<NotificationRequest>
            {
                new(
                    "appointment.booked",
                    appointment.CustomerId,
                    appointment.BusinessId,
                    new Dictionary<string, object?>
                    {
                        ["businessName"] = businessName,
                        ["scheduledAt"] = appointment.ScheduledAt.ToString("O"),
                        ["appointmentId"] = appointment.Id
                    },
                    $"appt:{appointment.Id}:booked")
            };

                // The assigned professional is told the slot is theirs. Same transaction as the
                // row, so the notice can never outlive a rollback.
                if (appointment.StaffUserId is Guid assignedStaffId && assignedStaffId != Guid.Empty)
                {
                    bookingIntents.Add(new NotificationRequest(
                        "appointment.staff_assigned",
                        assignedStaffId,
                        appointment.BusinessId,
                        new Dictionary<string, object?>
                        {
                            ["businessName"] = businessName,
                            ["scheduledAt"] = appointment.ScheduledAt.ToString("O"),
                            ["appointmentId"] = appointment.Id
                        },
                        $"appt:{appointment.Id}:staff-assigned:{assignedStaffId}"));
                }

                // A booking whose fee is awaiting verification needs the business to act, so the
                // owner is told in the same transaction that creates the row. Only for that status:
                // an ordinary booking needs no decision, and telling the owner about their own
                // walk-in would be noise.
                if (string.Equals(appointment.Status, AppointmentStatuses.PendingPayment, StringComparison.Ordinal)
                    && business?.OwnerId is Guid ownerId
                    && ownerId != Guid.Empty
                    && ownerId != appointment.CustomerId)
                {
                    bookingIntents.Add(new NotificationRequest(
                        "appointment.booking_requested",
                        ownerId,
                        appointment.BusinessId,
                        new Dictionary<string, object?>
                        {
                            ["businessName"] = businessName,
                            ["scheduledAt"] = appointment.ScheduledAt.ToString("O"),
                            ["appointmentId"] = appointment.Id
                        },
                        $"appt:{appointment.Id}:booking-requested:{ownerId}"));
                }

                await QueueNotificationsSafelyAsync(bookingIntents);

                // Phase 2: the durable created-event and the reminder ladder join the same
                // transaction — a crash can delay reminders but never lose them. The ladder JSON
                // was read with the booking context; a row created in this transaction cannot
                // already have queued jobs, so the dedupe lookup is skipped.
                await PublishAppointmentCreatedAsync(appointment, business, default);
                await CreatePendingReminderJobsAsync(
                    appointment, _context, default, reminderOffsetMinutesJson, skipExistingCheck: true);

                // Reactivation: cancel any pending customer.lapsed jobs for this customer
                // at this business so they stop receiving lapse nudges. A first-time customer
                // has no such jobs by definition.
                if (!skipWinBackCancel)
                {
                    await CancelPendingWinBackJobsAsync(
                        appointment.CustomerId, appointment.BusinessId, _context, default);
                }

                prepareMs = writeWatch.ElapsedMilliseconds - lockMs - keyMs - overlapMs;
                await _unitOfWork.SaveChangesAsync();
                saveMs = writeWatch.ElapsedMilliseconds - lockMs - keyMs - overlapMs - prepareMs;
                var commitWatch = System.Diagnostics.Stopwatch.StartNew();
                await transaction.CommitAsync();
                commitWatch.Stop();

                appointmentId = appointment.Id;
                statusChangedAt = historyChangedAt;

                _logger.LogInformation(
                    "BookingWriteTiming AppointmentId={AppointmentId} LockMs={LockMs} KeyMs={KeyMs} " +
                    "OverlapMs={OverlapMs} PrepareMs={PrepareMs} SaveMs={SaveMs} CommitMs={CommitMs} TotalMs={TotalMs}",
                    appointment.Id, lockMs, keyMs, overlapMs, prepareMs, saveMs,
                    commitWatch.ElapsedMilliseconds, writeWatch.ElapsedMilliseconds);
            }
            catch (Exception ex) when (AppointmentConflicts.IsBookingKeyConflict(ex))
            {
                // Two identical submissions raced past the in-transaction lookup; the unique
                // index let exactly one row through. Return that row — the customer's booking
                // was made once, which is what they asked for.
                await RollbackQuietlyAsync(transaction);

                var replay = await _context.Appointments.AsNoTracking()
                    .Include(a => a.Resources)
                    .FirstOrDefaultAsync(a =>
                        a.BusinessId == businessId && a.BookingRequestKey == bookingRequestKey);

                if (replay == null)
                    return ApiResponse<AppointmentResponse>.Fail(
                        "BOOKING_FAILED", "Failed to create appointment.");

                return ApiResponse<AppointmentResponse>.Ok(customer != null
                    ? await BuildCreatedResponseAsync(replay, customer, replay.CreatedAt)
                    : await ToResponseAsync(replay));
            }
            catch (Exception ex) when (AppointmentConflicts.IsSlotConflict(ex))
            {
                // The exclusion-constraint backstop fired: the interval was taken
                // between this request's check and its insert.
                AppointmentConflicts.LogConflict(_logger, ex, businessId, staffUserId);
                await RollbackQuietlyAsync(transaction);
                return ApiResponse<AppointmentResponse>.Fail(
                    AppointmentConflicts.StaffTimeUnavailable,
                    AppointmentConflicts.StaffTimeUnavailableMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create an appointment for business {BusinessId}.", businessId);
                await RollbackQuietlyAsync(transaction);
                return ApiResponse<AppointmentResponse>.Fail("BOOKING_FAILED", "Failed to create appointment.");
            }
        }

        // Built AFTER the transaction closed on purpose: a failure while shaping the
        // response can no longer be reported as a booking failure, because the
        // booking is already committed.
        //
        // Shaped from what the request already resolved — the appointment entity the
        // context still tracks (with its service lines), the business and the customer
        // read on the way in — instead of re-reading the row, its history, its pending
        // reschedule, its people and its enrollment. Six round trips that told the
        // caller nothing it had not just written.
        if (customer != null)
        {
            var committed = _context.Appointments.Local.FirstOrDefault(a => a.Id == appointmentId);
            if (committed != null)
                return ApiResponse<AppointmentResponse>.Ok(await BuildCreatedResponseAsync(
                    committed, customer, statusChangedAt));
        }

        var reloaded = await LoadAsync(appointmentId);
        return ApiResponse<AppointmentResponse>.Ok(await ToResponseAsync(reloaded!));
    }

    /// <summary>
    /// The response for an appointment the caller just created, assembled from data
    /// already in hand.
    /// </summary>
    /// <remarks>
    /// Two fields are derived rather than queried, and deliberately so:
    /// <c>UpdatedAt</c> is the status-history row written moments ago in the same
    /// transaction, and <c>PendingReschedule</c> is empty because an appointment that
    /// did not exist a moment ago cannot have a pending reschedule proposal — the
    /// query that looks for one could only ever return nothing.
    /// </remarks>
    private async Task<AppointmentResponse> BuildCreatedResponseAsync(
        Appointment appointment, BookingCustomer customer, DateTime statusChangedAt)
    {
        var response = appointment.ToAppointmentResponse();
        response.UpdatedAt = statusChangedAt;
        response.PendingReschedule = null;

        response.CustomerName = customer.FullName;
        response.CustomerEmail = customer.Email;
        response.CustomerPhone = customer.PhoneNumber;
        response.CustomerAvatarUrl = customer.AvatarUrl;
        response.CustomerType = customer.CustomerType;

        // An assigned professional who is not the customer is the only person this
        // response still has to look up, and only when the request did not already
        // load them (a caller-supplied staff member is tracked; a resolver-picked one
        // is not). One targeted query, or none at all.
        if (appointment.StaffUserId is Guid staffId && staffId != appointment.CustomerId)
        {
            var tracked = _context.Users.Local.FirstOrDefault(u => u.Id == staffId);
            var staff = tracked ?? await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == staffId);

            if (staff != null)
            {
                response.StaffName = staff.FullName;
                response.StaffEmail = staff.Email;
                response.StaffAvatarUrl = staff.AvatarUrl;
            }
        }

        return response;
    }

    /// <summary>
    /// Rolls back without masking the original failure. A transaction that already
    /// committed (or already rolled back) reports "completed" rather than throwing,
    /// and that must never replace the real error the caller is about to be told.
    /// </summary>
    private static async Task RollbackQuietlyAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync();
        }
        catch (InvalidOperationException)
        {
            // Nothing to undo — the transaction had already finished.
        }
    }

    /// <summary>
    /// Cancels an appointment's pending scheduled jobs (reminders + follow-ups) so a
    /// terminal transition (cancel/complete/no-show) leaves zero orphan jobs. Runs
    /// inside the caller's unit of work.
    /// </summary>
    private static async Task CancelPendingJobsAsync(Guid appointmentId, ApplicationDbContext context, CancellationToken ct)
    {
        await context.ScheduledJobs
            .Where(j => j.AggregateType == "appointment" && j.AggregateId == appointmentId && j.Status == "pending")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.Status, "cancelled")
                .SetProperty(j => j.CancelledAt, DateTime.UtcNow)
                .SetProperty(j => j.UpdatedAt, DateTime.UtcNow)
                .SetProperty(j => j.LockedAt, (DateTime?)null), ct);
    }

    /// <summary>
    /// Cancels pending win-back jobs for a (customer, business) pair across every
    /// cycle. The dedupe key prefix identifies the pair; the suffix carries the
    /// activity anchor + cycle index, so a returning customer cancels only what is
    /// still outstanding (new cycles get new keys).
    /// Runs inside the caller's unit of work.
    /// </summary>
    private static async Task CancelPendingWinBackJobsAsync(
        Guid customerId, Guid businessId, ApplicationDbContext context, CancellationToken ct)
    {
        var prefix = WinBackCyclePolicy.DedupeKeyPrefix(customerId, businessId);
        await context.ScheduledJobs
            .Where(j => j.JobType == WinBackCyclePolicy.JobType
                && j.BusinessId == businessId
                && j.Status == "pending"
                && j.DedupeKey.StartsWith(prefix))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.Status, "cancelled")
                .SetProperty(j => j.CancelledAt, DateTime.UtcNow)
                .SetProperty(j => j.UpdatedAt, DateTime.UtcNow)
                .SetProperty(j => j.LockedAt, (DateTime?)null), ct);
    }

    /// <summary>
    /// Schedules the post-completion follow-up (T+1 day) inside the caller's unit of
    /// work. Stable dedupe key makes repeat completions a no-op.
    /// </summary>
    private static async Task ScheduleFollowUpAsync(Appointment appointment, ApplicationDbContext context, CancellationToken ct)
    {
        var key = $"appt:{appointment.Id}:follow-up";
        var existing = await context.ScheduledJobs
            .AnyAsync(j => j.DedupeKey == key, ct);
        if (existing) return;

        await context.ScheduledJobs.AddAsync(new ScheduledJob
        {
            JobType = "appointment.follow_up",
            DedupeKey = key,
            AggregateType = "appointment",
            AggregateId = appointment.Id,
            BusinessId = appointment.BusinessId,
            PayloadJson = "{}",
            ScheduledForUtc = DateTime.UtcNow.AddDays(1),
            Status = "pending",
            Attempts = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }, ct);
    }

    /// <summary>
    /// Rewrites an appointment's pending reminder ladder for a new start time (stable
    /// dedupe keys: same key, new due time). Jobs whose slot already passed are
    /// cancelled instead of rescheduled. Runs inside the caller's unit of work.
    /// </summary>
    private async Task RewriteReminderJobsAsync(Appointment appointment, ApplicationDbContext context, CancellationToken ct)
    {
        // Per-appointment opt-out: cancel all pending reminder jobs, create none.
        if (appointment.ReminderOptOut)
        {
            await CancelPendingJobsAsync(appointment.Id, context, ct);
            return;
        }

        var policy = await context.BusinessReminderPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.BusinessId == appointment.BusinessId, ct)
            ?? new BusinessReminderPolicy();
        var offsets = ParseOffsetMinutes(policy.OffsetMinutesJson);
        var now = DateTime.UtcNow;

        var pending = await context.ScheduledJobs
            .Where(j => j.JobType == "appointment.reminder"
                && j.AggregateType == "appointment"
                && j.AggregateId == appointment.Id
                && j.Status == "pending")
            .ToListAsync(ct);

        var wanted = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var offsetMinutes in offsets)
        {
            var due = appointment.ScheduledAt.AddMinutes(-offsetMinutes);
            if (due <= now.AddMinutes(5)) continue; // minimum lead time: skip stale slots
            wanted[$"appt:{appointment.Id}:r-{offsetMinutes}m"] = due;
        }

        foreach (var job in pending)
        {
            if (wanted.TryGetValue(job.DedupeKey, out var due))
            {
                job.ScheduledForUtc = due;
                job.UpdatedAt = now;
                wanted.Remove(job.DedupeKey);
            }
            else
            {
                job.Status = "cancelled";
                job.CancelledAt = now;
                job.UpdatedAt = now;
                job.LockedAt = null;
            }
        }

        foreach (var (key, due) in wanted)
        {
            await context.ScheduledJobs.AddAsync(new ScheduledJob
            {
                JobType = "appointment.reminder",
                DedupeKey = key,
                AggregateType = "appointment",
                AggregateId = appointment.Id,
                BusinessId = appointment.BusinessId,
                PayloadJson = "{}",
                ScheduledForUtc = due,
                Status = "pending",
                Attempts = 0,
                CreatedAt = now,
                UpdatedAt = now
            }, ct);
        }
    }

    /// <summary>
    /// Creates the reminder jobs for the new appointment (default ladder
    /// <c>[1440,60]</c> minutes before <see cref="Appointment.ScheduledAt"/>). Jobs
    /// reuse the globally unique <c>dedupe_key</c>, so rebooking rewrites the due
    /// times instead of accumulating duplicate jobs. Failures are non-fatal to the
    /// booking itself — the scheduler claims and delivers them.
    /// </summary>
    private async Task<bool> CreatePendingReminderJobsAsync(
        Appointment appointment, ApplicationDbContext context, CancellationToken ct,
        string? reminderOffsetMinutesJson = null, bool skipExistingCheck = false)
    {
        // Per-appointment opt-out: never create reminder jobs for opted-out
        // appointments. The scheduler worker also re-checks at dispatch time
        // as a second line of defence.
        if (appointment.ReminderOptOut) return false;

        var offsetsJson = reminderOffsetMinutesJson;
        if (offsetsJson == null)
        {
            var policy = await context.BusinessReminderPolicies
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.BusinessId == appointment.BusinessId, ct)
                ?? new BusinessReminderPolicy();
            offsetsJson = policy.OffsetMinutesJson;
        }

        var offsets = ParseOffsetMinutes(offsetsJson);
        var now = DateTime.UtcNow;

        // The due times this appointment wants, before anything is asked of the
        // database, so the "is it already queued?" question is ONE query for the whole
        // ladder instead of one per rung.
        var wanted = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var offsetMinutes in offsets)
        {
            var due = appointment.ScheduledAt.AddMinutes(-offsetMinutes);
            if (due <= now.AddMinutes(5)) continue; // minimum lead time: skip stale slots
            wanted[$"appt:{appointment.Id}:r-{offsetMinutes}m"] = due;
        }

        if (wanted.Count == 0) return false;

        var keys = wanted.Keys.ToList();
        var queued = skipExistingCheck
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await context.ScheduledJobs
                .AsNoTracking()
                .Where(j => keys.Contains(j.DedupeKey) &&
                            j.AggregateType == "appointment" &&
                            j.AggregateId == appointment.Id &&
                            j.Status == "pending")
                .Select(j => j.DedupeKey)
                .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        var created = false;
        foreach (var (key, due) in wanted)
        {
            if (queued.Contains(key)) continue;

            await context.ScheduledJobs.AddAsync(new ScheduledJob
            {
                JobType = "appointment.reminder",
                DedupeKey = key,
                AggregateType = "appointment",
                AggregateId = appointment.Id,
                BusinessId = appointment.BusinessId,
                PayloadJson = "{}",
                ScheduledForUtc = due,
                Status = "pending",
                Attempts = 0,
                CreatedAt = now,
                UpdatedAt = now
            }, ct);
            created = true;
        }

        return created;
    }

    private async Task<ApiResponse<AppointmentResponse>> TransitionAsync(
        Appointment appointment, string toStatus, Guid changedByUserId, string? note, string role, bool staffOrOwnerOnly)
    {
        // ── 1. Lifecycle validation (server-side, authoritative) ────────────
        if (!AppointmentStatusMachine.CanTransition(appointment.Status, toStatus))
            return ApiResponse<AppointmentResponse>.Fail(
                "INVALID_STATUS_TRANSITION",
                AppointmentStatusMachine.DescribeRefusal(appointment.Status, toStatus));

        // ── 2. Idempotent repeat ────────────────────────────────────────────
        // Asking for the status the appointment already has is not an error and
        // MUST NOT repeat side effects: no second history row, no second
        // notification, no second commission. This is what makes a retried
        // request (or a double-tapped button) safe.
        if (string.Equals(appointment.Status, toStatus, StringComparison.Ordinal))
            return ApiResponse<AppointmentResponse>.Ok(await ToResponseAsync(appointment));

        // ── 3. Actor authorization ──────────────────────────────────────────
        if (staffOrOwnerOnly && !IsRole(role, "Business") && !IsRole(role, "Staff"))
            return ApiResponse<AppointmentResponse>.Fail("FORBIDDEN", "Only Business or Staff may perform this action.");

        if (IsRole(role, "Business") || IsRole(role, "Staff"))
        {
            var required = AppointmentStatusMachine.RequiredPermission(toStatus);
            if (required != null &&
                !await _permissionService.HasBusinessPermissionAsync(changedByUserId, appointment.BusinessId, required))
                return ApiResponse<AppointmentResponse>.Fail(
                    "FORBIDDEN",
                    $"You do not have permission to move this appointment to '{toStatus}' ({required} required).");
        }

        // ── 4. Apply ────────────────────────────────────────────────────────
        appointment.Status = toStatus;
        appointment.Version++;
        _unitOfWork.Appointments.Update(appointment);

        await _unitOfWork.AppointmentStatusHistory.AddAsync(new AppointmentStatusHistory
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            Status = toStatus,
            ChangedAt = DateTime.UtcNow,
            ChangedByUserId = changedByUserId,
            Note = note,
            CreatedAt = DateTime.UtcNow
        });

        // A terminal status retires any open reschedule proposal: leaving it
        // pending would offer a decision that can no longer be applied.
        if (AppointmentStatuses.IsTerminal(toStatus))
            await ResolvePendingRescheduleRequestsAsync(
                appointment,
                changedByUserId,
                toStatus == AppointmentStatuses.Cancelled
                    ? RescheduleRequestStatus.Withdrawn
                    : RescheduleRequestStatus.Rejected,
                $"Appointment became '{toStatus}'.");

        if (toStatus == AppointmentStatuses.Cancelled)
        {
            // Phase 1: the cancellation notice joins this SaveChanges — it exists if and
            // only if the status change does (intent-level outbox).
            await QueueNotificationSafelyAsync(new NotificationRequest(
                "appointment.cancelled",
                appointment.CustomerId,
                appointment.BusinessId,
                new Dictionary<string, object?>
                {
                    ["businessName"] = await GetBusinessNameAsync(appointment.BusinessId),
                    ["scheduledAt"] = appointment.ScheduledAt.ToString("O"),
                    ["appointmentId"] = appointment.Id
                },
                $"appt:{appointment.Id}:cancelled"));

            await PublishAppointmentCancelledAsync(appointment, default);
            // Phase 2: terminal states cancel the appointment's pending jobs so no
            // stale reminder can fire after a cancel/complete/no-show.
            await CancelPendingJobsAsync(appointment.Id, _context, default);
        }

        if (toStatus == AppointmentStatuses.Completed)
        {
            await PublishAppointmentCompletedAsync(appointment, default);
            await CancelPendingJobsAsync(appointment.Id, _context, default);
            // Attending is the strongest reactivation signal: stop any pending
            // win-back nudge for this (customer, business) pair. The new
            // appointment also moved the cycle anchor, so future nudges get a
            // fresh dedupe key.
            await CancelPendingWinBackJobsAsync(
                appointment.CustomerId, appointment.BusinessId, _context, default);
            await ScheduleFollowUpAsync(appointment, _context, default);
        }

        if (toStatus == AppointmentStatuses.NoShow)
        {
            await PublishAppointmentNoShowAsync(appointment, default);
            await QueueNotificationSafelyAsync(new NotificationRequest(
                "appointment.no_show_notice",
                appointment.CustomerId,
                appointment.BusinessId,
                new Dictionary<string, object?>
                {
                    ["businessName"] = await GetBusinessNameAsync(appointment.BusinessId),
                    ["scheduledAt"] = appointment.ScheduledAt.ToString("O"),
                    ["appointmentId"] = appointment.Id
                },
                $"appt:{appointment.Id}:no-show"));
            await CancelPendingJobsAsync(appointment.Id, _context, default);
        }

        if (toStatus == AppointmentStatuses.Confirmed)
        {
            await PublishAppointmentConfirmedAsync(appointment, default);
        }

        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request changed this appointment between the read and the
            // write. The loser is told so plainly instead of silently winning a
            // race it lost (which is what would otherwise duplicate a
            // notification, a commission entry or a loyalty stamp).
            _logger.LogWarning(
                "Concurrent modification detected while transitioning appointment {AppointmentId} to '{Status}'.",
                appointment.Id, toStatus);
            return ApiResponse<AppointmentResponse>.Fail(
                "CONCURRENT_MODIFICATION",
                "This appointment was changed by another request. Reload it and try again.");
        }

        // ── 5. Commission accrual ───────────────────────────────────────────
        // Runs after the status change is committed. It is idempotent, so a
        // repeated call cannot produce a second entry, and a commission failure
        // never invalidates the appointment transition itself.
        if (AppointmentStatuses.IsTerminal(toStatus))
            await AccrueCommissionSafelyAsync(appointment.Id);

        return ApiResponse<AppointmentResponse>.Ok(await ToResponseAsync(appointment));
    }

    /// <summary>
    /// Accrues commission for a terminal appointment. Failures are logged and
    /// swallowed: a commission problem must never surface as a failed
    /// appointment transition (the ledger can always be reconciled later).
    /// </summary>
    private async Task AccrueCommissionSafelyAsync(Guid appointmentId)
    {
        if (_commissions == null) return;

        try
        {
            await _commissions.AccrueForAppointmentAsync(appointmentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Commission accrual failed for appointment {AppointmentId}; the appointment transition stands.",
                appointmentId);
        }
    }

    private async Task<(List<ServiceCatalogItem> Services, string? ErrorCode, string? ErrorMessage)> ValidateServicesAsync(
        Guid businessId, Guid[] serviceIds)
    {
        if (serviceIds == null || serviceIds.Length == 0)
            return (new List<ServiceCatalogItem>(), "VALIDATION_ERROR", "At least one service is required.");

        // Duplicates are a client bug, not extra line items: reject so pricing
        // and duration can never be inflated by repeating the same service id.
        if (serviceIds.Distinct().Count() != serviceIds.Length)
            return (new List<ServiceCatalogItem>(), "VALIDATION_ERROR", "Duplicate services are not allowed.");

        var distinctRequested = serviceIds.Distinct().ToArray();
        var services = await _context.ServiceCatalogItems
            .Where(s => distinctRequested.Contains(s.Id) && s.BusinessId == businessId && s.IsActive)
            .ToListAsync();

        if (services.Count != distinctRequested.Length)
            return (services, "SERVICE_NOT_FOUND", "One or more services were not found in this business.");

        return (services, null, null);
    }

    /// <summary>
    /// Publishes <c>appointment.created</c> — the reminder job creation above and
    /// the scheduler's authoritative re-reads both depend on this being durable
    /// (same transaction as the booking).
    /// </summary>
    private async Task PublishAppointmentCreatedAsync(Appointment appointment, Business? business, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["appointmentId"] = appointment.Id,
            ["businessId"] = appointment.BusinessId,
            ["businessName"] = business?.Name ?? "Your business",
            ["scheduledAt"] = appointment.ScheduledAt.ToString("O"),
            ["endAt"] = appointment.EndAt.ToString("O")
        };

        await _domainEventPublisher.PublishAsync(new DomainEventRecord(
            "appointment.created", "appointment", appointment.Id, appointment.BusinessId,
            JsonSerializer.Serialize(payload, PayloadOptions), appointment.ScheduledAt), ct);
    }

    /// <summary>
    /// Publishes <c>appointment.confirmed</c> for the confirmed transition.
    /// </summary>
    private async Task PublishAppointmentConfirmedAsync(Appointment appointment, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["appointmentId"] = appointment.Id,
            ["businessId"] = appointment.BusinessId,
            ["businessName"] = await GetBusinessNameAsync(appointment.BusinessId),
            ["scheduledAt"] = appointment.ScheduledAt.ToString("O"),
            ["endAt"] = appointment.EndAt.ToString("O")
        };

        await _domainEventPublisher.PublishAsync(new DomainEventRecord(
            "appointment.confirmed", "appointment", appointment.Id, appointment.BusinessId,
            JsonSerializer.Serialize(payload, PayloadOptions), DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Publishes <c>appointment.no_show</c> for the no-show transition.
    /// </summary>
    private async Task PublishAppointmentNoShowAsync(Appointment appointment, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["appointmentId"] = appointment.Id,
            ["businessId"] = appointment.BusinessId,
            ["businessName"] = await GetBusinessNameAsync(appointment.BusinessId),
            ["scheduledAt"] = appointment.ScheduledAt.ToString("O")
        };

        await _domainEventPublisher.PublishAsync(new DomainEventRecord(
            "appointment.no_show", "appointment", appointment.Id, appointment.BusinessId,
            JsonSerializer.Serialize(payload, PayloadOptions), DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Publishes <c>appointment.rescheduled</c> after an approved time change.
    /// </summary>
    private async Task PublishAppointmentRescheduledAsync(Appointment appointment, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["appointmentId"] = appointment.Id,
            ["businessId"] = appointment.BusinessId,
            ["businessName"] = await GetBusinessNameAsync(appointment.BusinessId),
            ["scheduledAt"] = appointment.ScheduledAt.ToString("O"),
            ["endAt"] = appointment.EndAt.ToString("O")
        };

        await _domainEventPublisher.PublishAsync(new DomainEventRecord(
            "appointment.rescheduled", "appointment", appointment.Id, appointment.BusinessId,
            JsonSerializer.Serialize(payload, PayloadOptions), DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Publishes <c>appointment.completed</c> for the completed transition.
    /// </summary>
    private async Task PublishAppointmentCompletedAsync(Appointment appointment, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["appointmentId"] = appointment.Id,
            ["businessId"] = appointment.BusinessId,
            ["businessName"] = await GetBusinessNameAsync(appointment.BusinessId),
            ["scheduledAt"] = appointment.ScheduledAt.ToString("O"),
            ["endAt"] = appointment.EndAt.ToString("O"),
            ["completedAt"] = appointment.EndAt.ToString("O")
        };

        await _domainEventPublisher.PublishAsync(new DomainEventRecord(
            "appointment.completed", "appointment", appointment.Id, appointment.BusinessId,
            JsonSerializer.Serialize(payload, PayloadOptions), appointment.EndAt), ct);
    }

    /// <summary>
    /// Publishes <c>appointment.cancelled</c> for the cancelled transition.
    /// </summary>
    private async Task PublishAppointmentCancelledAsync(Appointment appointment, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["appointmentId"] = appointment.Id,
            ["businessId"] = appointment.BusinessId,
            ["businessName"] = await GetBusinessNameAsync(appointment.BusinessId),
            ["scheduledAt"] = appointment.ScheduledAt.ToString("O"),
            ["completedAt"] = (string?)null
        };

        await _domainEventPublisher.PublishAsync(new DomainEventRecord(
            "appointment.cancelled", "appointment", appointment.Id, appointment.BusinessId,
            JsonSerializer.Serialize(payload, PayloadOptions), appointment.EndAt), ct);
    }

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    private static List<int> ParseOffsetMinutes(string json)
    {
        try
        {
            var values = JsonSerializer.Deserialize<List<int>>(json, PayloadOptions) ?? new();
            var parsed = values.Where(m => m > 0).Distinct().OrderBy(m => m).ToList();
            return parsed.Count > 0 ? parsed : new List<int> { 1440, 60 };
        }
        catch
        {
            return new List<int> { 1440, 60 };
        }
    }

    private async Task<(User? Staff, string? ErrorCode, string? ErrorMessage)> ResolveStaffAsync(
        Guid businessId, Guid? staffUserId, Guid[] serviceIds)
    {
        if (!staffUserId.HasValue)
            return (null, null, null);

        // Explicit selection exists only where the product offers it. The single policy
        // decides that from the business's effective provider model, so the storefront,
        // the internal booking form and the availability grid cannot disagree.
        if (!await AllowsExplicitStaffSelectionAsync(businessId))
        {
            return (null, "STAFF_MODULE_DISABLED",
                "Staff selection is not available for this business, so a staff member cannot be assigned.");
        }

        // The provider must be the owner or ACTIVE staff of THIS business.
        if (!await IsProviderMemberAsync(staffUserId.Value, businessId))
            return (null, "STAFF_NOT_FOUND", "Staff member not found in this business.");

        var staff = await _context.Users.FirstOrDefaultAsync(u => u.Id == staffUserId.Value);
        if (staff == null)
            return (null, "STAFF_NOT_FOUND", "Staff member not found in this business.");

        // Authoritative eligibility through the single resolver: the owner in a
        // solo / staff-module-disabled business, or any provider assigned the
        // categories of the requested services. Never re-implemented here.
        if (serviceIds.Length > 0 &&
            !await _availability.IsProviderEligibleAsync(businessId, staffUserId.Value, serviceIds))
        {
            return (null, "STAFF_NOT_AVAILABLE",
                "The selected provider does not offer all of the requested services.");
        }

        return (staff, null, null);
    }

    /// <summary>True when the identity is the owner or ACTIVE staff of the business.</summary>
    private Task<bool> IsProviderMemberAsync(Guid userId, Guid businessId) =>
        _context.BusinessMemberships.AsNoTracking().AnyAsync(m =>
            m.UserId == userId &&
            m.BusinessId == businessId &&
            m.Status == BusinessMembershipStatus.Active &&
            (m.Role == BusinessMembershipRole.Owner || m.Role == BusinessMembershipRole.Staff));

    private Task<RescheduleRequest?> LatestPendingRequestAsync(Guid appointmentId) =>
        _context.RescheduleRequests
            .Where(r => r.AppointmentId == appointmentId && r.Status == "pending")
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync();

    /// <summary>
    /// How long an undecided reschedule proposal stays actionable. After this
    /// window the request expires (by the background sweep, or lazily when a
    /// decision is attempted) instead of lingering forever.
    /// </summary>
    private const int RescheduleRequestExpiryDays = 7;

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// Retires every open reschedule proposal for an appointment. Used when the
    /// appointment reaches a terminal status (nothing can be rescheduled any
    /// more) and when a proposal is withdrawn or expired.
    ///
    /// <para>The request is never deleted: it is moved to a terminal state with
    /// its decision metadata, so the full rescheduling history survives.</para>
    /// </summary>
    private async Task<int> ResolvePendingRescheduleRequestsAsync(
        Appointment appointment,
        Guid? actorUserId,
        RescheduleRequestStatus outcome,
        string? notes)
    {
        var pending = await _context.RescheduleRequests
            .Where(r => r.AppointmentId == appointment.Id && r.Status == "pending")
            .ToListAsync();
        if (pending.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var request in pending)
        {
            request.Status = WireStatus(outcome);
            request.ResolvedAt = now;
            request.ResolvedByUserId = actorUserId;
            request.DecisionNotes = notes == null ? null : Truncate(notes, 500);
            request.Version++;
            request.UpdatedAt = now;
        }

        return pending.Count;
    }

    private static string WireStatus(RescheduleRequestStatus status) => status switch
    {
        RescheduleRequestStatus.Approved => "approved",
        RescheduleRequestStatus.Rejected => "rejected",
        RescheduleRequestStatus.Withdrawn => "withdrawn",
        RescheduleRequestStatus.Expired => "expired",
        _ => "pending"
    };

    /// <summary>
    /// True when a pending proposal has passed its expiry instant. Approval and
    /// rejection refuse such a request rather than applying a stale decision.
    /// </summary>
    private static bool HasExpired(RescheduleRequest request, DateTime now) =>
        request.ExpiresAt.HasValue && request.ExpiresAt.Value <= now;

    /// <summary>
    /// The complete rescheduling history for an appointment, newest first. Every
    /// proposal is preserved — including rejected, withdrawn and expired ones —
    /// so the record of previous schedule changes is never overwritten.
    /// </summary>
    public async Task<ApiResponse<List<AppointmentRescheduleHistoryItemResponse>>> GetRescheduleHistoryAsync(
        Guid callerUserId, string role, Guid appointmentId)
    {
        var appointment = await LoadAsync(appointmentId);
        if (appointment == null)
            return ApiResponse<List<AppointmentRescheduleHistoryItemResponse>>.Fail("NOT_FOUND", "Appointment not found.");

        var ownershipError = await AssertOwnershipAsync(callerUserId, role, appointment);
        if (ownershipError != null)
            return ApiResponse<List<AppointmentRescheduleHistoryItemResponse>>.Fail(
                ownershipError.Error!.Code!, ownershipError.Error.Message);

        var requests = await _context.RescheduleRequests
            .AsNoTracking()
            .Where(r => r.AppointmentId == appointmentId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return ApiResponse<List<AppointmentRescheduleHistoryItemResponse>>.Ok(
            requests.Select(MapRescheduleHistoryItem).ToList());
    }

    private static AppointmentRescheduleHistoryItemResponse MapRescheduleHistoryItem(RescheduleRequest request)
    {
        List<AppointmentServiceSnapshot> services;
        try
        {
            services = System.Text.Json.JsonSerializer.Deserialize<List<AppointmentServiceSnapshot>>(
                request.ProposedServicesJson) ?? new List<AppointmentServiceSnapshot>();
        }
        catch
        {
            services = new List<AppointmentServiceSnapshot>();
        }

        return new AppointmentRescheduleHistoryItemResponse
        {
            Id = request.Id,
            Status = request.Status,
            RequestedByUserId = request.RequestedByUserId,
            RequestedByParty = request.RequestedByParty.ToString().ToLowerInvariant(),
            RequestReason = request.RequestReason,
            OriginalScheduledAt = request.OriginalScheduledAt,
            OriginalEndAt = request.OriginalEndAt,
            OriginalStaffUserId = request.OriginalStaffUserId,
            ProposedScheduledAt = request.ProposedScheduledAt,
            ProposedEndAt = request.ProposedEndAt,
            ProposedStaffUserId = request.ProposedStaffUserId,
            ProposedServices = services,
            ExpiresAt = request.ExpiresAt,
            RequestedAt = request.CreatedAt,
            ResolvedAt = request.ResolvedAt,
            ResolvedByUserId = request.ResolvedByUserId,
            DecisionNotes = request.DecisionNotes
        };
    }

    private static AppointmentRescheduleRequestResponse? MapPendingReschedule(RescheduleRequest? request)
    {
        if (request == null) return null;
        List<AppointmentServiceSnapshot> services;
        try
        {
            services = System.Text.Json.JsonSerializer.Deserialize<List<AppointmentServiceSnapshot>>(
                request.ProposedServicesJson) ?? new List<AppointmentServiceSnapshot>();
        }
        catch
        {
            services = new List<AppointmentServiceSnapshot>();
        }

        return new AppointmentRescheduleRequestResponse
        {
            Id = request.Id,
            ProposedScheduledAt = request.ProposedScheduledAt,
            ProposedEndAt = request.ProposedEndAt,
            ProposedStaffUserId = request.ProposedStaffUserId,
            ProposedServices = services,
            RequestedAt = request.CreatedAt
        };
    }

    /// <summary>Batch-loads pending reschedule requests for a set of appointments.</summary>
    private async Task<Dictionary<Guid, RescheduleRequest>> GetPendingRescheduleRequestsAsync(
        IEnumerable<Guid> appointmentIds)
    {
        var ids = appointmentIds.ToList();
        if (ids.Count == 0) return new Dictionary<Guid, RescheduleRequest>();

        var requests = await _context.RescheduleRequests
            .Where(r => ids.Contains(r.AppointmentId) && r.Status == "pending")
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return requests
            .GroupBy(r => r.AppointmentId)
            .ToDictionary(g => g.Key, g => g.First());
    }

    private async Task<ApiResponse<AppointmentResponse>?> AssertOwnershipAsync(
        Guid callerUserId, string role, Appointment appointment)
    {
        if (IsRole(role, "Business"))
        {
            var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(b => b.OwnerId == callerUserId);
            if (business == null)
                return ApiResponse<AppointmentResponse>.Fail("NOT_FOUND", "No business found for this account.");
            if (business.Id != appointment.BusinessId)
                return ApiResponse<AppointmentResponse>.Fail("FORBIDDEN", "Not authorized to access this appointment.");
        }
        else if (IsRole(role, "Staff"))
        {
            if (appointment.StaffUserId != callerUserId)
                return ApiResponse<AppointmentResponse>.Fail("FORBIDDEN", "Not authorized to access this appointment.");
        }
        else
        {
            if (appointment.CustomerId != callerUserId)
                return ApiResponse<AppointmentResponse>.Fail("FORBIDDEN", "Not authorized to access this appointment.");
        }

        return null;
    }

    private async Task<Appointment?> LoadAsync(Guid id)
    {
        // Tenant narrowing: an appointment outside the active tenant is indistinguishable from a missing one.
        var query = _context.Appointments.Include(a => a.Resources).Where(a => a.Id == id);
        if (TenantBusinessId is Guid tenantId)
            query = query.Where(a => a.BusinessId == tenantId);
        return await query.FirstOrDefaultAsync();
    }

    /// <summary>
    /// Maps an appointment to its response, deriving updatedAt from the most recent
    /// AppointmentStatusHistory.ChangedAt (fallback: CreatedAt).
    /// </summary>
    private async Task<AppointmentResponse> ToResponseAsync(Appointment appointment)
    {
        var response = appointment.ToAppointmentResponse();

        var latest = await _context.AppointmentStatusHistory
            .Where(h => h.AppointmentId == appointment.Id)
            .OrderByDescending(h => h.ChangedAt)
            .Select(h => h.ChangedAt)
            .FirstOrDefaultAsync();

        response.UpdatedAt = latest != default ? latest : appointment.CreatedAt;

        var pending = await LatestPendingRequestAsync(appointment.Id);
        response.PendingReschedule = MapPendingReschedule(pending);

        // The appointment row stores only the two user ids; resolve the people it
        // names here so the detail response is self-contained. This is what lets
        // the appointment screen show the customer and staff — name, contact and
        // photo — without reaching into the customer's or the staff member's other
        // appointments.
        var userIds = new List<Guid> { appointment.CustomerId };
        if (appointment.StaffUserId is Guid staffId && staffId != appointment.CustomerId)
            userIds.Add(staffId);

        var people = await _context.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, u.Email, u.PhoneNumber, u.AvatarUrl })
            .ToListAsync();

        var customer = people.FirstOrDefault(u => u.Id == appointment.CustomerId);
        if (customer != null)
        {
            response.CustomerName = customer.FullName;
            response.CustomerEmail = customer.Email;
            response.CustomerPhone = customer.PhoneNumber;
            response.CustomerAvatarUrl = customer.AvatarUrl;
        }

        // How the customer belongs to this business — Guest or Registered — read from
        // the authoritative enrollment row so the appointment detail can say which.
        var customerType = await _context.CustomerBusinessEnrollments.AsNoTracking()
            .Where(e => e.CustomerId == appointment.CustomerId && e.BusinessId == appointment.BusinessId)
            .Select(e => (CustomerType?)e.Type)
            .FirstOrDefaultAsync();
        response.CustomerType = customerType == CustomerType.Guest ? "guest" : "registered";

        if (appointment.StaffUserId is Guid assignedStaff)
        {
            var staff = people.FirstOrDefault(u => u.Id == assignedStaff);
            if (staff != null)
            {
                response.StaffName = staff.FullName;
                response.StaffEmail = staff.Email;
                response.StaffAvatarUrl = staff.AvatarUrl;
            }
        }

        return response;
    }

    /// <summary>
    /// Fetches the latest status-change timestamp for many appointments in a single
    /// grouped query. Used by list endpoints to avoid the per-row N+1 that
    /// ToResponseAsync would otherwise cause.
    /// </summary>
    private async Task<Dictionary<Guid, DateTime>> GetLatestStatusChangesAsync(IEnumerable<Guid> appointmentIds)
    {
        var ids = appointmentIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, DateTime>();

        return await _context.AppointmentStatusHistory
            .Where(h => ids.Contains(h.AppointmentId))
            .GroupBy(h => h.AppointmentId)
            .Select(g => new { AppointmentId = g.Key, Latest = g.Max(h => h.ChangedAt) })
            .ToDictionaryAsync(x => x.AppointmentId, x => x.Latest);
    }

    /// <summary>
    /// Synchronous mapping using pre-fetched latest status changes (from
    /// <see cref="GetLatestStatusChangesAsync"/>). Falls back to CreatedAt when no
    /// history exists yet.
    /// </summary>
    private AppointmentResponse MapResponse(Appointment appointment, Dictionary<Guid, DateTime> latestChanges)
    {
        var response = appointment.ToAppointmentResponse();
        response.UpdatedAt = latestChanges.TryGetValue(appointment.Id, out var latest) && latest != default
            ? latest
            : appointment.CreatedAt;
        return response;
    }

    /// <summary>
    /// (customerId, businessId) → <c>"guest" | "registered"</c> for a batch of
    /// appointments, read in one query so a list never N+1s to learn each customer's
    /// type. Any pair without an enrollment row falls back to <c>registered</c> — the
    /// pre-guest default, so an unexpected gap never mislabels a real account.
    /// </summary>
    private async Task<Dictionary<(Guid CustomerId, Guid BusinessId), string>> GetCustomerTypesAsync(
        IEnumerable<Appointment> appointments)
    {
        var pairs = appointments.Select(a => new { a.CustomerId, a.BusinessId }).Distinct().ToList();
        if (pairs.Count == 0) return new Dictionary<(Guid, Guid), string>();

        var customerIds = pairs.Select(p => p.CustomerId).Distinct().ToList();
        var businessIds = pairs.Select(p => p.BusinessId).Distinct().ToList();

        var rows = await _context.CustomerBusinessEnrollments.AsNoTracking()
            .Where(e => customerIds.Contains(e.CustomerId) && businessIds.Contains(e.BusinessId))
            .Select(e => new { e.CustomerId, e.BusinessId, e.Type })
            .ToListAsync();

        var types = new Dictionary<(Guid, Guid), string>();
        foreach (var row in rows)
            types[(row.CustomerId, row.BusinessId)] =
                row.Type == CustomerType.Guest ? "guest" : "registered";
        return types;
    }

    /// <summary>
    /// Merges a multi-select filter with the single-value parameter older callers still
    /// send, dropping blanks and duplicates. Empty means "no filter", so a selector that
    /// cleared all of its chips widens the list rather than matching nothing.
    /// </summary>
    private static Guid[] Merge(Guid[]? ids, Guid? single)
    {
        var set = new HashSet<Guid>();
        if (ids is { Length: > 0 })
            foreach (var id in ids)
                if (id != Guid.Empty) set.Add(id);
        if (single.HasValue && single.Value != Guid.Empty) set.Add(single.Value);
        return set.ToArray();
    }

    /// <summary>
    /// An appointment's payment ledger, summed down to just what a filter or a badge
    /// needs. Built from the appointment's booked prices and its payment rows (never
    /// stored on the appointment), so it cannot disagree with the payments module.
    /// </summary>
    private readonly record struct PaymentTotals(
        decimal Expected, decimal Settled, bool Awaiting, bool Failed, bool Refunded);

    /// <summary>
    /// The payment ledger for a set of appointments, reduced to the five facts a
    /// payment filter or a row badge needs.
    ///
    /// <para>Two lean reads — the booked prices and the payments — then a single pass
    /// here in the service. Only scalars cross out of the database (an id, a price, a
    /// status, an amount); no appointment and no payment entity is materialised, and
    /// nothing is filtered in the browser. The arithmetic is done in C# rather than SQL
    /// because SUM over a decimal column is not portable across database providers.</para>
    /// </summary>
    private async Task<Dictionary<Guid, PaymentTotals>> BuildPaymentTotalsAsync(
        List<Guid> appointmentIds,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<Guid, decimal>? bookedTotals = null)
    {
        // The read runs even for an empty page, so a list read costs the same number of
        // statements whatever the page holds — the property the query-count guard
        // asserts. An empty `IN` list simply returns no rows.
        var result = new Dictionary<Guid, PaymentTotals>();

        // The caller often already holds the booked prices — the paged items include
        // their service resources — in which case that read is skipped entirely.
        Dictionary<Guid, decimal> expectedByAppointment;
        if (bookedTotals is not null)
        {
            expectedByAppointment = new Dictionary<Guid, decimal>(bookedTotals);
        }
        else
        {
            expectedByAppointment = (await _context.AppointmentResources.AsNoTracking()
                    .Where(r => appointmentIds.Contains(r.AppointmentId))
                    .Select(r => new { r.AppointmentId, r.Price })
                    .ToListAsync(cancellationToken))
                .GroupBy(r => r.AppointmentId)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.Price));
        }

        var paymentsByAppointment = (await _context.Payments.AsNoTracking()
                .Where(p => p.AppointmentId != null && appointmentIds.Contains(p.AppointmentId.Value))
                .Select(p => new { p.AppointmentId, p.Status, p.Amount })
                .ToListAsync(cancellationToken))
            .GroupBy(p => p.AppointmentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var id in appointmentIds)
        {
            var rows = paymentsByAppointment.GetValueOrDefault(id) ?? [];
            result[id] = new PaymentTotals(
                Expected: expectedByAppointment.GetValueOrDefault(id),
                // Only genuinely settled money counts: provider-confirmed or
                // owner-verified. A claim awaiting verification is NOT money.
                Settled: rows.Where(p => PaymentStatuses.IsSettled(p.Status)).Sum(p => p.Amount),
                Awaiting: rows.Any(p => PaymentStatuses.IsAwaitingVerification(p.Status)),
                Failed: rows.Any(p => p.Status is PaymentStatus.Failed or PaymentStatus.Expired or PaymentStatus.Cancelled),
                Refunded: rows.Any(p => p.Status is PaymentStatus.Refunded or PaymentStatus.Reversed));
        }

        return result;
    }

    /// <summary>
    /// Canonicalises an incoming payment-filter value to the six the ledger supports,
    /// or null when the caller asked for no payment filter at all (`all`/blank/unknown).
    /// Accepts the snake_case the queue uses as well as the terse client names.
    /// </summary>
    private static string? NormalizePaymentFilter(string? paymentStatus)
    {
        if (string.IsNullOrWhiteSpace(paymentStatus)) return null;
        var key = paymentStatus.Trim().ToLowerInvariant().Replace('-', '_');
        return key switch
        {
            "verified" or "paid" or "fully_paid" => "verified",
            "partial" or "partially_paid" or "part_paid" => "partial",
            "awaiting" or "awaiting_verification" or "pending" or "submitted" => "awaiting",
            "unpaid" => "unpaid",
            "failed" or "expired" or "failed_expired" => "failed",
            "refunded" or "reversed" or "refunded_reversed" => "refunded",
            _ => null
        };
    }

    /// <summary>
    /// Does one appointment's ledger match the requested payment filter?
    ///
    /// The six states partition cleanly, so each option answers a distinct question:
    /// <list type="bullet">
    /// <item><c>verified</c> — the settled amount covers the expected total.</item>
    /// <item><c>partial</c> — some money settled, but less than the total.</item>
    /// <item><c>awaiting</c> — a real claim is queued for verification.</item>
    /// <item><c>unpaid</c> — nothing settled, nothing awaiting, nothing failed or refunded.</item>
    /// <item><c>failed</c> — a failed/expired/cancelled attempt exists.</item>
    /// <item><c>refunded</c> — a refund or reversal exists.</item>
    /// </list>
    /// </summary>
    private static bool PaymentFilterMatches(
        string filter, decimal expected, decimal settled, bool awaiting, bool failed, bool refunded) =>
        filter switch
        {
            "verified" => expected > 0 && settled >= expected,
            "partial" => settled > 0 && settled < expected,
            "awaiting" => awaiting,
            "unpaid" => settled <= 0 && !awaiting && !failed && !refunded,
            "failed" => failed,
            "refunded" => refunded,
            _ => true
        };

    /// <summary>
    /// The payment state shown on the row, derived from the ledger. Refunds and
    /// reversals win because they describe money that left; otherwise the state is
    /// full / partial / awaiting-verification / unpaid, with a failed attempt noted.
    /// </summary>
    private static string ClassifyPayment(
        decimal expected, decimal settled, bool awaiting, bool failed, bool refunded)
    {
        if (refunded) return "refunded";
        if (expected > 0 && settled >= expected) return "paid";
        if (settled > 0) return "partially_paid";
        if (awaiting) return "awaiting_verification";
        if (failed) return "failed";
        return "unpaid";
    }

    private static bool IsRole(string value, string expected) =>
        string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Appointment times are stored and compared in UTC. Payloads without an
    /// offset are treated as UTC rather than as server-local time.
    /// </summary>
    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
