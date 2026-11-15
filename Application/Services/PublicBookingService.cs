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
    /// The canonical customer↔business writer. Optional so a unit test can construct the service
    /// without one; it is then built over the same DbContext (the service is stateless).
    /// </summary>
    private ICustomerEnrollmentService? _enrollments;

    public PublicBookingService(
        ApplicationDbContext db,
        IAppointmentService appointments,
        IPaymentService payments,
        IModuleEntitlementService modules,
        ILogger<PublicBookingService> logger,
        ITenantContext? tenant = null,
        ICustomerEnrollmentService? enrollments = null)
    {
        _db = db;
        _appointments = appointments;
        _payments = payments;
        _modules = modules;
        _logger = logger;
        _tenant = tenant;
        _enrollments = enrollments;
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

        var business = await _db.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == request.BusinessId, cancellationToken);
        if (business == null)
            return Fail("BUSINESS_NOT_FOUND", "Business not found.");

        if (!await _modules.IsModuleEnabledAsync(business.Id, ModuleKey))
            return Fail("MODULE_DISABLED", "This business does not take bookings through Punched.");

        // ── What the customer typed ──────────────────────────────
        var invalid = ValidateCustomer(request);
        if (invalid != null) return invalid;

        // ── The business's own payment policy ────────────────────
        var config = await _db.BusinessPaymentConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.BusinessId == business.Id, cancellationToken);

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
        var resolved = await ResolveCustomerAsync(
            business.Id,
            request,
            authenticatedAsCustomer ? callerUserId : null,
            cancellationToken);
        if (!resolved.Success)
            return Fail(resolved.Error!.Code, resolved.Error.Message);

        var customer = resolved.Data!;

        // ── The appointment: booked now, before any money is discussed ──
        var created = await _appointments.CreateAppointmentAsync(
            customer.User.Id, "Customer",
            new CreateAppointmentRequest
            {
                BusinessId = business.Id,
                ServiceIds = request.ServiceIds,
                StaffUserId = request.StaffUserId,
                ScheduledAt = request.ScheduledAt,
                Note = request.Note,
                // Exactly the option the policy just resolved, so the appointment path applies the
                // terms the customer was shown.
                PaymentOption = policy.Option,
            },
            isGuest: !authenticatedAsCustomer);

        if (!created.Success)
            return Fail(created.Error!.Code, created.Error.Message);

        var appointment = created.Data!;

        // ── The payment: recorded as evidence awaiting the owner's decision ──
        var warnings = new List<string>();
        PaymentResponse? payment = null;

        if (policy.Option == StorefrontPaymentOptions.PayUpfront)
            payment = await CreateBookingFeePaymentAsync(
                appointment, config, request, customer.User.Id, warnings, cancellationToken);

        var summary = await SummaryAsync(appointment.Id, customer.User.Id);

        _logger.LogInformation(
            "Public booking created. BusinessId={BusinessId} AppointmentId={AppointmentId} " +
            "CustomerId={CustomerId} CustomerType={CustomerType} Option={Option} PaymentId={PaymentId} Status={Status}",
            business.Id, appointment.Id, customer.User.Id, customer.Type, policy.Option,
            payment?.Id, payment?.Status ?? "none");

        return ApiResponse<PublicBookingResponse>.Ok(
            Describe(appointment, payment, summary, warnings));
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

        var match = await _db.Users
            .Where(u => u.Role == UserRole.Customer && !u.IsDeleted
                && ((u.Email != null && u.Email.ToLower() == loweredEmail)
                    || (u.PhoneNumber != null
                        && (u.PhoneNumber == phone
                            || (suffix.Length == 9 && u.PhoneNumber.EndsWith(suffix)))
                        && _db.CustomerBusinessEnrollments.Any(e =>
                            e.CustomerId == u.Id && e.BusinessId == businessId))))
            .OrderBy(u => u.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (match != null)
        {
            // The relationship already exists, so it is reused — and never re-tagged downward: a
            // Registered customer stays Registered even when they booked without signing in.
            var enrolled = await EnsureEnrollmentAsync(businessId, match.Id, CustomerType.Guest, cancellationToken);
            if (!enrolled.Success)
                return ApiResponse<ResolvedCustomer>.Fail(enrolled.Error!.Code, enrolled.Error.Message);

            // The relationship row is the authority on what kind of relationship it is — read it
            // back rather than inferring it, so "Guest" here always means the row says Guest.
            var type = await EnrollmentTypeAsync(businessId, match.Id, cancellationToken);

            _logger.LogInformation(
                "Public booking reused an existing customer. BusinessId={BusinessId} CustomerId={CustomerId} Type={Type}",
                businessId, match.Id, type);

            return ApiResponse<ResolvedCustomer>.Ok(new ResolvedCustomer(match, type));
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
