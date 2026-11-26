using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Application.Services;

/// <summary>
/// Booking from the public storefront — see <see cref="IPublicBookingService"/>.
///
/// <para><b>Order of decisions, and why.</b> The business comes first (from the request and the
/// tenant host), because everything else is scoped to it. The customer is resolved <i>within that
/// business</i> next, so the appointment always belongs to a business-specific customer record.
/// Only then is the appointment created — as a normal booking, because a storefront booking must
/// not be held hostage by a payment nobody has reviewed yet. The payment is recorded after, with
/// any pasted confirmation attached as evidence awaiting the owner's decision.</para>
///
/// <para><b>Who the customer is.</b> An authenticated <b>customer</b> token is the customer, and
/// their existing relationships are left alone. A business or staff token is <b>not</b> a customer
/// identity: an owner booking from their own storefront is a visitor like anyone else, and using
/// their admin account as the customer would conflate two very different things. Anonymous
/// visitors are matched to an existing customer of this business by phone or email, and otherwise
/// get a new record tagged <see cref="CustomerType.Guest"/>.</para>
///
/// <para><b>What is deliberately not read.</b> No cross-business customer data: matching happens
/// only against customers already enrolled with the business being booked.</para>
/// </summary>
public sealed class PublicBookingService : IPublicBookingService
{
    /// <summary>The module that owns booking; a storefront only offers what the business bought.</summary>
    private const string ModuleKey = "appointments";

    private const int MaxNameLength = 100;

    private static readonly Regex KenyanPhone = new(@"^(?:\+?254|0)?[17]\d{8}$", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    private readonly ApplicationDbContext _db;
    private readonly IAppointmentService _appointments;
    private readonly IPaymentService _payments;
    private readonly IModuleEntitlementService _modules;
    private readonly ITenantContext? _tenant;
    private readonly ILogger<PublicBookingService> _logger;

    /// <summary>
    /// Temporary slot claims. Optional in the constructor so a unit test keeps compiling; when
    /// absent, one is built over the same context (the service is stateless).
    /// </summary>
    private BookingHoldService? _holds;

    /// <summary>The hold service, resolved once.</summary>
    private BookingHoldService Holds => _holds ??= new BookingHoldService(
        _db,
        new AppointmentAvailabilityService(
            _db, NullLogger<AppointmentAvailabilityService>.Instance, readContexts: _readContexts),
        _readContexts,
        NullLogger<BookingHoldService>.Instance);

    /// <summary>
    /// The canonical customer↔business writer. Optional so a unit test can construct the service
    /// without one; it is then built over the same DbContext (the service is stateless).
    /// </summary>
    private ICustomerEnrollmentService? _enrollments;

    /// <summary>
    /// The one-read booking context (business, catalogue, roster, schedule, diary). Optional
    /// so a unit test can construct the service without container options; it then falls back
    /// to the same reads sequentially over the same context.
    /// </summary>
    private readonly BookingReadContextLoader _readContexts;

    public PublicBookingService(
        ApplicationDbContext db,
        IAppointmentService appointments,
        IPaymentService payments,
        IModuleEntitlementService modules,
        ILogger<PublicBookingService> logger,
        BookingHoldService? holds = null,
        ITenantContext? tenant = null,
        ICustomerEnrollmentService? enrollments = null,
        BookingReadContextLoader? readContexts = null)
    {
        _db = db;
        _appointments = appointments;
        _payments = payments;
        _modules = modules;
        _logger = logger;
        _holds = holds;
        _tenant = tenant;
        _enrollments = enrollments;
        _readContexts = readContexts ?? new BookingReadContextLoader(db);
    }

    private static ApiResponse<PublicBookingResponse> Fail(string code, string message) =>
        ApiResponse<PublicBookingResponse>.Fail(code, message);

    private static bool IsCustomerRole(string? role) =>
        string.Equals(role, "Customer", StringComparison.OrdinalIgnoreCase);

    /// <summary>The digits a phone number is compared by, so 07XX and 2547XX are one number.</summary>
    private static string PhoneKey(string? phone) =>
        new((phone ?? string.Empty).Where(char.IsDigit).ToArray());

    /// <summary>The last nine digits — what a Kenyan mobile number is unique by.</summary>
    private static string PhoneSuffix(string? phone)
    {
        var digits = PhoneKey(phone);
        return digits.Length <= 9 ? digits : digits[^9..];
    }

    public async Task<ApiResponse<PublicBookingResponse>> BookAsync(
        Guid? callerUserId, string? callerRole, Guid? tenantBusinessId,
        PublicBookingRequest request, CancellationToken cancellationToken = default)
    {
        // ── The business, and nothing else's business ────────────
        // On a tenant host the body must name that tenant: a request naming another business is
        // refused rather than trusted, so a storefront cannot be used to book elsewhere.
        if (tenantBusinessId is Guid activeTenant && request.BusinessId != activeTenant)
            return Fail("FORBIDDEN", "This business is not available on this site.");

        // ── What the customer typed ──────────────────────────────
        var invalid = ValidateCustomer(request);
        if (invalid != null) return invalid;

        if (request.ServiceIds == null || request.ServiceIds.Length == 0)
            return Fail("VALIDATION_ERROR", "Choose at least one service.");

        // ── ONE parallel read wave ───────────────────────────────
        // Business, payment configuration, catalogue, roster, working windows, diary and the
        // reminder ladder. This is the change that took the booking off the ten-second floor:
        // the reads no longer queue behind each other at ~250ms per round trip, and the
        // booking write below then validates the whole interval in memory.
        //
        // The window is the booking page's own horizon (14 days) plus timezone slack: the
        // roster for the requested local date has to be in this snapshot, or the write would
        // have to ask the database again for a day it already knows about.
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var readWatch = System.Diagnostics.Stopwatch.StartNew();
        var read = await _readContexts.LoadAsync(
            request.BusinessId,
            request.ServiceIds,
            utcToday.AddDays(-2),
            utcToday.AddDays(16),
            includePaymentConfig: true,
            includeReminderPolicy: true,
            customerEmail: request.Email,
            customerPhone: request.PhoneNumber,
            bookingRequestKey: request.IdempotencyKey,
            ct: cancellationToken);
        readWatch.Stop();
        var readMs = readWatch.ElapsedMilliseconds;

        if (read == null)
            return Fail("BUSINESS_NOT_FOUND", "Business not found.");

        // A booking further out than the snapshot window (the booking page offers 14 days)
        // still has to be judged against that day's real roster. Re-read for the requested
        // date alone rather than refusing a booking the business would accept.
        var bookingTimeZone = BookingTime.ResolveTimeZone(read.Business.TimeZoneId);
        var requestedLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(request.ScheduledAt, DateTimeKind.Utc), bookingTimeZone));

        if (requestedLocalDate < utcToday.AddDays(-2) || requestedLocalDate > utcToday.AddDays(16))
        {
            read = await _readContexts.LoadAsync(
                request.BusinessId,
                request.ServiceIds,
                requestedLocalDate.AddDays(-1),
                requestedLocalDate.AddDays(1),
                includePaymentConfig: true,
                includeReminderPolicy: true,
                customerEmail: request.Email,
                customerPhone: request.PhoneNumber,
                bookingRequestKey: request.IdempotencyKey,
                ct: cancellationToken);

            if (read == null)
                return Fail("BUSINESS_NOT_FOUND", "Business not found.");
        }

        var business = read.Business;

        // The entitlement check is memory-cached (and warmed by the loader's Staff check), so
        // it costs nothing on the critical path once the business has been seen.
        if (!await _modules.IsModuleEnabledAsync(business.Id, ModuleKey))
            return Fail("MODULE_DISABLED", "This business does not take bookings through Punched.");

        // ── The business's own payment policy ────────────────────
        var config = read.PaymentConfig;

        var policy = StorefrontPaymentOptions.Resolve(
            config?.PayUpfrontEnabled ?? true,
            config?.PayAfterServiceEnabled ?? false,
            request.PaymentOption);
        if (!policy.Ok)
            return Fail(policy.ErrorCode!, policy.ErrorMessage!);

        // ── The customer record for THIS business ────────────────
        // Only a customer token identifies a customer. Anything else — no token, an owner's token,
        // a staff token, a customer of some other business — is resolved from what was typed.
        var authenticatedAsCustomer = IsCustomerRole(callerRole);
        var customerWatch = System.Diagnostics.Stopwatch.StartNew();
        var resolved = await ResolveCustomerAsync(
            business.Id,
            request,
            authenticatedAsCustomer ? callerUserId : null,
            read.CustomerMatch,
            cancellationToken);
        customerWatch.Stop();
        var customerMs = customerWatch.ElapsedMilliseconds;
        if (!resolved.Success)
            return Fail(resolved.Error!.Code, resolved.Error.Message);

        var customer = resolved.Data!;

        // The write path's view of the same party. Display data only — authorization was
        // already decided above.
        var bookingCustomer = new BookingCustomer(
            customer.User.Id,
            customer.User.FullName,
            customer.User.Email,
            customer.User.PhoneNumber,
            customer.User.AvatarUrl,
            customer.Type == CustomerType.Guest ? "guest" : "registered");

        // ── The money, decided before the write so it lands in the same transaction ──
        // A booking fee is manual M-PESA only: without published details there is no online
        // channel to collect through, which is exactly what the previous path concluded.
        var warnings = new List<string>();
        Payment? bookingFee = null;
        var paysUpfront = policy.Option == StorefrontPaymentOptions.PayUpfront;

        // ── A retry is a replay, not a new booking ───────────────
        // Answered BEFORE the payment rules below: the customer's own booking already holds the
        // interval, so re-validating the reservation would call their completed booking an expired
        // hold. Returning the row they already created is the only honest answer to a double tap.
        if (read.ExistingBooking is Appointment replayOfIntent)
        {
            if (replayOfIntent.CustomerId != customer.User.Id)
                return Fail("DUPLICATE_BOOKING_KEY", "That booking key has already been used. Start a new booking.");
        }

        var serviceTotalForFee = read.Services.Sum(s => s.Price ?? 0m);
        var feeDue = paysUpfront && config is { HasManualMpesaDetails: true }
            ? config.BookingFeeFor(serviceTotalForFee)
            : 0m;

        // ── The payment-first rule ───────────────────────────────
        // When the business charges a mandatory booking fee, the slot is held and the fee's
        // confirmation must be in hand BEFORE the appointment exists. Both halves are enforced
        // here, on the server: the frontend's disabled button is a courtesy, not the rule, and
        // there is no second endpoint that books without them.
        var requiresHeldSlotAndProof = feeDue > 0m && read.ExistingBooking == null;
        AppointmentSlotHold? hold = null;

        if (requiresHeldSlotAndProof)
        {
            if (request.HoldId is not Guid holdId)
                return Fail(
                    "RESERVATION_REQUIRED",
                    "This business asks for a booking fee. Hold your time first, pay, then paste the M-PESA confirmation to finish.");

            hold = await Holds.FindLiveAsync(business.Id, holdId, cancellationToken);
            if (hold == null)
                return Fail(
                    "RESERVATION_EXPIRED",
                    "The time you were holding is no longer reserved. Please choose a time again.");

            if (!hold.ServiceIds.OrderBy(id => id)
                    .SequenceEqual(request.ServiceIds.Distinct().OrderBy(id => id)))
                return Fail(
                    "RESERVATION_MISMATCH",
                    "That booking does not match the services that were held. Please start again.");

            if (string.IsNullOrWhiteSpace(request.MpesaMessage))
                return Fail(
                    "PAYMENT_PROOF_REQUIRED",
                    "Paste the M-PESA confirmation message for your booking fee to finish the booking.");
        }
        else if (request.HoldId is Guid optionalHoldId)
        {
            // No fee is due, so a hold is not required — but one that was taken must still be
            // consumed rather than left claiming the interval the booking now owns.
            hold = await Holds.FindLiveAsync(business.Id, optionalHoldId, cancellationToken);
        }

        // The hold, when there is one, owns the interval: the request cannot move it.
        var staffUserId = hold?.StaffUserId ?? request.StaffUserId;
        var scheduledAt = hold?.StartsAtUtc ?? request.ScheduledAt;

        if (paysUpfront && config is { HasManualMpesaDetails: true })
        {
            var serviceTotal = read.Services.Sum(s => s.Price ?? 0m);
            var fee = config.BookingFeeFor(serviceTotal);
            if (fee > 0)
            {
                bookingFee = new Payment
                {
                    Id = Guid.NewGuid(),
                    BusinessId = business.Id,
                    CustomerId = customer.User.Id,
                    Type = PaymentType.BookingFee,
                    Method = PaymentMethod.Mpesa,
                    Provider = PaymentProviderKind.ManualMpesa,
                    Status = PaymentStatus.Created,
                    Amount = decimal.Round(fee, 2, MidpointRounding.AwayFromZero),
                    Currency = "KES",
                    Reference = PaymentReferences.New(),
                    PhoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber)
                        ? null
                        : new string(request.PhoneNumber.Where(char.IsDigit).ToArray()),
                    CreatedByUserId = customer.User.Id,
                    CreatedAt = DateTime.UtcNow
                };
            }
            // A zero fee is a valid configuration ("nothing up front"), not a failure.
        }

        // ── The appointment ──────────────────────────────────────
        // One transaction: staff-time lock, overlapping-interval re-check, appointment and
        // its service lines, status history, notification intents, reminders, the booking
        // fee above, commit. The client's idempotency key makes a retry a replay.
        var writeWatch = System.Diagnostics.Stopwatch.StartNew();
        var created = await _appointments.CreateStorefrontAppointmentAsync(
            read,
            customer.User.Id,
            bookingCustomer,
            request.ServiceIds,
            staffUserId,
            scheduledAt,
            request.Note,
            payAfterService: policy.Option == StorefrontPaymentOptions.PayAfterService,
            bookingRequestKey: request.IdempotencyKey,
            bookingFeeFactory: appointmentId =>
            {
                if (bookingFee == null) return null;
                bookingFee.AppointmentId = appointmentId;
                return bookingFee;
            },
            cancellationToken: cancellationToken,
            // A submitted booking fee means nobody has verified the money yet, so the appointment
            // waits in pending_payment until the business confirms it. Every other booking is
            // simply booked.
            initialStatus: requiresHeldSlotAndProof
                ? AppointmentStatuses.PendingPayment
                : AppointmentStatuses.Booked,
            convertHoldId: hold?.Id);

        if (!created.Success)
            return Fail(created.Error!.Code, created.Error.Message);

        writeWatch.Stop();
        var writeMs = writeWatch.ElapsedMilliseconds;

        var appointment = created.Data!;

        // A replay (same booking key) found the appointment that already exists; the fee row
        // was written by the FIRST request, so it is not in hand here. Read the money's state
        // instead of guessing it.
        var replayed = bookingFee != null && bookingFee.AppointmentId == Guid.Empty;

        PaymentResponse? payment = !replayed && bookingFee != null
            ? PaymentService.ToResponse(bookingFee)
            : null;

        // A pasted confirmation is evidence for the owner: it moves the payment's state and
        // is never a condition of the booking itself.
        if (payment != null && !string.IsNullOrWhiteSpace(request.MpesaMessage))
        {
            var submitted = await SubmitAsync(bookingFee!.Id, customer.User.Id, request.MpesaMessage!, warnings);
            if (submitted != null) payment = submitted;
        }

        var summary = payment != null
            ? DescribeLocally(read, appointment, payment)
            : await SummaryAsync(appointment.Id, customer.User.Id);

        _logger.LogInformation(
            "Public booking created. BusinessId={BusinessId} AppointmentId={AppointmentId} " +
            "CustomerId={CustomerId} CustomerType={CustomerType} Option={Option} PaymentId={PaymentId} Status={Status}",
            business.Id, appointment.Id, customer.User.Id, customer.Type, policy.Option,
            payment?.Id, payment?.Status ?? "none");

        // Structured timings for the booking pipeline. No customer name, phone, email or
        // payment reference appears in them: only durations and the ids an operator would
        // use to find the request.
        _logger.LogInformation(
            "BookingTiming AppointmentId={AppointmentId} BusinessId={BusinessId} ReadMs={ReadMs} " +
            "CustomerMs={CustomerMs} WriteMs={WriteMs} TotalMs={TotalMs}",
            appointment.Id, business.Id, readMs, customerMs, writeMs,
            readMs + customerMs + writeMs);

        return ApiResponse<PublicBookingResponse>.Ok(
            Describe(appointment, payment, summary, warnings));
    }

    /// <summary>
    /// The payment summary for a booking whose rows this request just wrote: computed from
    /// the same server figures the appointment was priced from, so no extra round trip is
    /// spent asking the database what it was told a moment ago.
    /// </summary>
    private static AppointmentPaymentSummary DescribeLocally(
        BookingReadContext read, AppointmentResponse appointment, PaymentResponse payment)
    {
        var serviceTotal = read.Services.Sum(s => s.Price ?? 0m);
        var fee = read.PaymentConfig?.BookingFeeFor(serviceTotal) ?? 0m;

        return new AppointmentPaymentSummary
        {
            AppointmentId = appointment.Id,
            ServiceTotal = serviceTotal,
            BookingFee = fee,
            BookingFeePercentage = read.PaymentConfig?.BookingFeePercentage ?? 0,
            // Nothing is settled at this point: a booking-fee row is evidence the customer
            // says they paid, not money the business has confirmed.
            AmountPaid = 0m,
            Balance = serviceTotal,
            PaymentStatus = "unpaid",
            VerificationStatus = string.Equals(payment.Status, "submitted", StringComparison.OrdinalIgnoreCase)
                ? "awaiting_verification"
                : "pending",
            Payments = new List<PaymentResponse> { payment }
        };
    }

    public async Task<ApiResponse<PublicBookingResponse>> SubmitPaymentProofAsync(
        Guid businessId, Guid appointmentId, SubmitPublicPaymentProofRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return Fail("VALIDATION_ERROR", "Paste the M-PESA confirmation message.");

        // The appointment id is the capability: a caller who has it may attach a confirmation to
        // that booking and nothing else. It must belong to the business in the route.
        var appointment = await _db.Appointments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == appointmentId && a.BusinessId == businessId, cancellationToken);
        if (appointment == null)
            return Fail("NOT_FOUND", "Appointment not found.");

        var warnings = new List<string>();
        PaymentResponse? payment = null;

        var existing = await _db.Payments
            .Where(p => p.AppointmentId == appointmentId
                && p.BusinessId == businessId
                && p.Type == PaymentType.BookingFee)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing != null && PaymentStatuses.CanAcceptSubmission(existing.Status))
        {
            payment = await SubmitAsync(existing.Id, appointment.CustomerId, request.Message, warnings);
        }
        else if (existing == null)
        {
            // Nothing was opened up front (no fee configured, or the payment was never created).
            // Open it now so the confirmation has something to attach to.
            var created = await _payments.CreatePaymentAsync(
                new CreatePaymentRequest
                {
                    AppointmentId = appointmentId,
                    Method = "mpesa",
                    Type = "booking_fee",
                    IdempotencyKey = request.IdempotencyKey,
                },
                appointment.CustomerId, "Customer", request.IdempotencyKey);

            if (created.Success)
                payment = await SubmitAsync(created.Data!.Id, appointment.CustomerId, request.Message, warnings);
            else
                warnings.Add(created.Error!.Message);
        }
        else
        {
            // The message is not discarded: it is simply already recorded, or closed.
            warnings.Add(PaymentStatuses.IsSettled(existing.Status)
                ? "This booking's fee has already been verified."
                : "A confirmation is already waiting for the business to review.");
        }

        // The appointment is read back, never rebuilt: verification is about money, not bookings.
        var booked = await _appointments.GetAppointmentAsync(appointment.CustomerId, "Customer", appointmentId);
        if (!booked.Success)
            return ApiResponse<PublicBookingResponse>.Fail(booked.Error!.Code, booked.Error.Message);

        var summary = await SummaryAsync(appointmentId, appointment.CustomerId);

        return ApiResponse<PublicBookingResponse>.Ok(
            Describe(booked.Data!, payment, summary, warnings));
    }

    /// <summary>
    /// Hands the pasted message to the payment module, which parses the receipt, records it as the
    /// payment's evidence, moves the payment to awaiting-verification and tells the owner. A
    /// message that cannot be parsed does <b>not</b> fail the booking: it is reported to the
    /// customer and the fee stays outstanding.
    /// </summary>
    private async Task<PaymentResponse?> SubmitAsync(
        Guid paymentId, Guid customerId, string message, List<string> warnings)
    {
        var submitted = await _payments.SubmitMpesaMessageAsync(
            paymentId, new SubmitMpesaMessageRequest { Message = message }, customerId, "Customer");

        if (submitted.Success) return submitted.Data;

        // Malformed paste, a receipt already used elsewhere, or a payment that can no longer take
        // a submission. The booking stands and the customer is told what to do about the money.
        warnings.Add(submitted.Error!.Message);
        _logger.LogInformation(
            "Public payment proof refused. PaymentId={PaymentId} Code={Code}", paymentId, submitted.Error.Code);
        return null;
    }

    // ═══════════════════════════════════════════════════════════
    //  PRIVATE HELPERS
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// The three things a booking cannot be made without: a name to call the person, a number the
    /// business can reach them on, and an address to send the confirmation to.
    /// </summary>
    private static ApiResponse<PublicBookingResponse>? ValidateCustomer(PublicBookingRequest request)
    {
        var name = request.FullName?.Trim() ?? string.Empty;
        if (name.Length < 2 || name.Length > MaxNameLength)
            return Fail("VALIDATION_ERROR", "Enter the name the appointment is for.");

        if (!KenyanPhone.IsMatch((request.PhoneNumber ?? string.Empty).Replace(" ", string.Empty)))
            return Fail("VALIDATION_ERROR", "Enter a valid Kenyan mobile number, for example 0712 345 678.");

        if (!Email.IsMatch(request.Email?.Trim() ?? string.Empty))
            return Fail("VALIDATION_ERROR", "Enter a valid email address so the confirmation can reach you.");

        return null;
    }

    /// <summary>The customer a booking is for, and how their relationship was established.</summary>
    private sealed record ResolvedCustomer(User User, CustomerType Type);

    /// <summary>
    /// Finds the business-specific customer record this booking belongs to, or creates one.
    ///
    /// <para><b>What "the customer record" means here.</b> Punched has one account per email
    /// address (<c>users</c>/<c>user_auth</c>), and one <i>relationship</i> per (account, business) —
    /// the enrollment, which is what carries the Guest or Registered tag. So the business-specific
    /// record the requirement asks for is the enrollment: the same person booking at two businesses
    /// has one account and two relationships, each with its own tag.</para>
    ///
    /// <para><b>Matching is deliberately narrow.</b> An email is matched globally, because the
    /// schema allows only one account per address — refusing to reuse it would mean either refusing
    /// the booking or creating an account that cannot exist. A phone number is matched only where the
    /// business already knows that customer, so a number alone can never attach a stranger's history
    /// to a new visitor. Nothing here reads or writes another business's records.</para>
    ///
    /// <para><b>A signed-in customer is reused as-is</b> — their token is proof of who they are —
    /// and their relationship here is Registered. A brand-new visitor's relationship is
    /// <see cref="CustomerType.Guest"/>.</para>
    /// </summary>
    private async Task<ApiResponse<ResolvedCustomer>> ResolveCustomerAsync(
        Guid businessId, PublicBookingRequest request, Guid? callerUserId,
        BookingCustomerMatch? preloadedMatch,
        CancellationToken cancellationToken)
    {
        var name = request.FullName.Trim();
        var phone = (request.PhoneNumber ?? string.Empty).Replace(" ", string.Empty);
        var email = request.Email.Trim();

        // ── A signed-in customer IS the customer ─────────────────
        if (callerUserId is Guid callerId)
        {
            var caller = await _db.Users.FirstOrDefaultAsync(u => u.Id == callerId && !u.IsDeleted, cancellationToken);
            if (caller != null)
            {
                // Their identity is proven, so the relationship here is Registered — and an
                // existing Guest relationship is upgraded rather than duplicated.
                var enrolled = await EnsureEnrollmentAsync(
                    businessId, caller.Id, CustomerType.Registered, cancellationToken);
                if (!enrolled.Success)
                    return ApiResponse<ResolvedCustomer>.Fail(enrolled.Error!.Code, enrolled.Error.Message);

                // Keep the record reachable by the details they just typed. The email is left
                // alone: that belongs to their account, and this request is not proof of it.
                if (string.IsNullOrWhiteSpace(caller.PhoneNumber)) caller.PhoneNumber = phone;
                if (!string.Equals(caller.FullName, name, StringComparison.Ordinal)) caller.FullName = name;
                await _db.SaveChangesAsync(cancellationToken);

                var relationship = await EnrollmentTypeAsync(businessId, caller.Id, cancellationToken);

                _logger.LogInformation(
                    "Public booking reused the signed-in customer. BusinessId={BusinessId} CustomerId={CustomerId}",
                    businessId, caller.Id);

                return ApiResponse<ResolvedCustomer>.Ok(new ResolvedCustomer(caller, relationship));
            }
        }

        // ── A returning customer of THIS business ────────────────
        // Matching follows the schema's own grain: an EMAIL is a person's account (one row in
        // `users`/`user_auth` per address, so a booking can never create a second account for the
        // same address), while a PHONE is only trusted when the business already knows it. The
        // business-specific record the requirement asks for is the ENROLLMENT below, which is what
        // carries the Guest/Registered tag per business.
        var suffix = PhoneSuffix(phone);
        var loweredEmail = email.ToLowerInvariant();

        var match = preloadedMatch;
        User? matchedUser = null;

        if (match != null)
        {
            if (match.HasActiveEnrollment)
            {
                // The relationship is already active and its own row says what kind it is, so
                // no enrollment write and no read-back is needed: the customer is reused
                // exactly as the snapshot described them.
                matchedUser = new User
                {
                    Id = match.UserId,
                    FullName = match.FullName,
                    Email = match.Email,
                    PhoneNumber = match.PhoneNumber,
                    AvatarUrl = match.AvatarUrl,
                    Role = UserRole.Customer
                };

                var existingType = match.EnrollmentType == CustomerType.Guest
                    ? CustomerType.Guest
                    : CustomerType.Registered;

                _logger.LogInformation(
                    "Public booking reused an existing customer. BusinessId={BusinessId} CustomerId={CustomerId} Type={Type}",
                    businessId, match.UserId, existingType);

                return ApiResponse<ResolvedCustomer>.Ok(new ResolvedCustomer(matchedUser, existingType));
            }

            matchedUser = new User
            {
                Id = match.UserId,
                FullName = match.FullName,
                Email = match.Email,
                PhoneNumber = match.PhoneNumber,
                AvatarUrl = match.AvatarUrl,
                Role = UserRole.Customer
            };
        }

        if (match == null)
            match = await _db.Users
                .Where(u => u.Role == UserRole.Customer && !u.IsDeleted
                    && ((u.Email != null && u.Email.ToLower() == loweredEmail)
                        || (u.PhoneNumber != null
                            && (u.PhoneNumber == phone
                                || (suffix.Length == 9 && u.PhoneNumber.EndsWith(suffix)))
                            && _db.CustomerBusinessEnrollments.Any(e =>
                                e.CustomerId == u.Id && e.BusinessId == businessId))))
                .OrderBy(u => u.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken) is { } found
                ? new BookingCustomerMatch(
                    found.Id, found.FullName, found.Email, found.PhoneNumber, found.AvatarUrl,
                    CustomerType.Guest, CustomerBusinessEnrollmentStatus.Left)
                : null;

        if (match != null)
        {
            // The relationship exists (or was matched by the database just now): reuse it —
            // and never re-tag downward, so a Registered customer stays Registered even when
            // they booked without signing in.
            var enrolled = await EnsureEnrollmentAsync(businessId, match.UserId, CustomerType.Guest, cancellationToken);
            if (!enrolled.Success)
                return ApiResponse<ResolvedCustomer>.Fail(enrolled.Error!.Code, enrolled.Error.Message);

            // The relationship row is the authority on what kind of relationship it is — read it
            // back rather than inferring it, so "Guest" here always means the row says Guest.
            var type = await EnrollmentTypeAsync(businessId, match.UserId, cancellationToken);

            _logger.LogInformation(
                "Public booking reused an existing customer. BusinessId={BusinessId} CustomerId={CustomerId} Type={Type}",
                businessId, match.UserId, type);

            matchedUser ??= new User
            {
                Id = match.UserId,
                FullName = match.FullName,
                Email = match.Email,
                PhoneNumber = match.PhoneNumber,
                AvatarUrl = match.AvatarUrl,
                Role = UserRole.Customer
            };

            return ApiResponse<ResolvedCustomer>.Ok(new ResolvedCustomer(matchedUser, type));
        }

        // ── A new customer, tagged as a guest ────────────────────
        // The password is random and known to nobody: this visitor is not signing in, and can set
        // a real one later through the ordinary password-reset flow if they ever want an account.
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = name,
            PhoneNumber = phone,
            Role = UserRole.Customer,
            CreatedAt = DateTime.UtcNow,
        };
        user.Auth = new UserAuth
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword($"{Guid.NewGuid():N}{Guid.NewGuid():N}", workFactor: 12),
            IsVerified = false,
            CreatedAt = DateTime.UtcNow,
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);

        // The unique (user, business) index makes a racing double-submit harmless: the loser is
        // answered with the relationship the winner created.
        var guestEnrollment = await EnsureEnrollmentAsync(businessId, user.Id, CustomerType.Guest, cancellationToken);
        if (!guestEnrollment.Success)
            return ApiResponse<ResolvedCustomer>.Fail(guestEnrollment.Error!.Code, guestEnrollment.Error.Message);

        _logger.LogInformation(
            "Public booking created a guest customer. BusinessId={BusinessId} CustomerId={CustomerId}",
            businessId, user.Id);

        return ApiResponse<ResolvedCustomer>.Ok(new ResolvedCustomer(user, CustomerType.Guest));
    }

    /// <summary>
    /// The canonical customer↔business relationship, written by its single writer so a storefront
    /// booking is enrolled exactly as any other customer is. Guest never demotes an existing
    /// Registered relationship.
    /// </summary>
    private async Task<ApiResponse<CustomerBusinessDto>> EnsureEnrollmentAsync(
        Guid businessId, Guid userId, CustomerType type, CancellationToken cancellationToken)
    {
        var enrollments = _enrollments ??= new CustomerEnrollmentService(
            new UnitOfWork(_db), _db, NullLogger<CustomerEnrollmentService>.Instance, _tenant);

        return await enrollments.EnrollAsync(userId, businessId, "storefront-booking", type);
    }

    /// <summary>
    /// The kind of relationship this customer now has with this business, read from the row the
    /// enrollment writer just committed.
    /// </summary>
    private async Task<CustomerType> EnrollmentTypeAsync(
        Guid businessId, Guid userId, CancellationToken cancellationToken)
    {
        var type = await _db.CustomerBusinessEnrollments.AsNoTracking()
            .Where(e => e.CustomerId == userId && e.BusinessId == businessId)
            .Select(e => e.Type)
            .FirstOrDefaultAsync(cancellationToken);

        return type == CustomerType.Guest ? CustomerType.Guest : CustomerType.Registered;
    }

    /// <summary>
    /// Records the money the customer offered to pay during booking, and attaches the pasted
    /// confirmation as evidence for the owner.
    ///
    /// <para><b>The booking never depends on this.</b> Every failure here — no channel configured,
    /// no fee, a malformed paste, a receipt used elsewhere — is a warning on an appointment that
    /// already exists and is already booked, because the customer's slot is not the payment's
    /// hostage. Nothing the customer pasted is discarded: a valid message becomes the payment's
    /// parsed evidence, and an unusable one is reported back to the customer, who still holds
    /// their own copy.</para>
    /// </summary>
    private async Task<PaymentResponse?> CreateBookingFeePaymentAsync(
        AppointmentResponse appointment, BusinessPaymentConfig? config, PublicBookingRequest request,
        Guid customerId, List<string> warnings, CancellationToken cancellationToken)
    {
        if (config == null || !config.HasManualMpesaDetails)
        {
            // No online channel is published: there is nothing to collect through the platform.
            // The business settles with the customer directly, which the confirmation says.
            return null;
        }

        var created = await _payments.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                AppointmentId = appointment.Id,
                Method = "mpesa",
                Type = "booking_fee",
                // The customer's own number, so the business can match the payment to them.
                PhoneNumber = request.PhoneNumber,
                IdempotencyKey = request.IdempotencyKey,
            },
            customerId, "Customer", request.IdempotencyKey);

        if (!created.Success)
        {
            // Most often "no fee configured" or "already fully paid", which are answers, not
            // failures. The booking stands either way.
            if (created.Error!.Code != "VALIDATION_ERROR") warnings.Add(created.Error.Message);
            return null;
        }

        var payment = created.Data!;

        if (string.IsNullOrWhiteSpace(request.MpesaMessage)) return payment;

        var submitted = await SubmitAsync(payment.Id, customerId, request.MpesaMessage!, warnings);
        return submitted ?? payment;
    }

    /// <summary>The server's own figures for the appointment, or an empty summary if it cannot read them.</summary>
    private async Task<AppointmentPaymentSummary> SummaryAsync(Guid appointmentId, Guid customerId)
    {
        var summary = await _payments.GetAppointmentPaymentSummaryAsync(appointmentId, customerId, "Customer");
        return summary.Data ?? new AppointmentPaymentSummary { AppointmentId = appointmentId };
    }

    /// <summary>
    /// The confirmation payload: what was booked, what is owed, what the money's state is, and what
    /// the customer should read. The state is derived from the payment module's own figures rather
    /// than invented here, so "pending verification" can never be confused with "paid".
    /// </summary>
    private static PublicBookingResponse Describe(
        AppointmentResponse appointment, PaymentResponse? payment, AppointmentPaymentSummary summary,
        List<string> warnings)
    {
        var awaiting = payment != null && PaymentStatuses.IsAwaitingVerification(
            Enum.TryParse<PaymentStatus>(payment.Status, ignoreCase: true, out var status)
                ? status
                : PaymentStatus.Created);

        if (string.Equals(summary.VerificationStatus, "awaiting_verification", StringComparison.OrdinalIgnoreCase))
            awaiting = true;

        var state = awaiting
            ? PublicPaymentStates.PendingVerification
            : summary.Balance <= 0 && summary.AmountPaid > 0
                ? PublicPaymentStates.Paid
                : summary.AmountPaid > 0
                    ? PublicPaymentStates.PartiallyPaid
                    : summary.Balance > 0
                        ? PublicPaymentStates.Unpaid
                        : PublicPaymentStates.NotDue;

        var due = summary.Balance > 0 ? summary.Balance : 0m;
        var message = state switch
        {
            PublicPaymentStates.PendingVerification =>
                "Your appointment is booked. The M-PESA confirmation you sent is waiting for the "
                + "studio to check it — you'll be told as soon as it is verified.",
            PublicPaymentStates.Paid => "Your appointment is booked and your payment is confirmed.",
            PublicPaymentStates.PartiallyPaid =>
                $"Your appointment is booked. {due:n2} is still to settle.",
            PublicPaymentStates.Unpaid =>
                due > 0
                    ? $"Your appointment is booked. {due:n2} is payable at the studio."
                    : "Your appointment is booked.",
            _ => "Your appointment is booked.",
        };

        return new PublicBookingResponse
        {
            Appointment = appointment,
            Payment = payment,
            Summary = summary,
            PaymentState = state,
            Message = message,
            Warnings = warnings,
        };
    }
}
