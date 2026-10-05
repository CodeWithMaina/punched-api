using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Core payment domain service (provider-independent). Owns the payment lifecycle:
/// creation (server-computed amounts), cash confirmation, STK initiation via
/// <see cref="IPaymentProvider"/> (the Daraja implementation lives behind that seam),
/// retry, cancel, reversal, and dashboard queries.
///
/// Architectural rule: Punched knows about the payment — the business owns the
/// money. No platform fees, splits, or pooled funds exist in this module.
/// </summary>
public partial class PaymentService : IPaymentService
{
    private static readonly TimeSpan CashConfirmGrace = TimeSpan.FromMinutes(5);

    private readonly ApplicationDbContext _context;
    private readonly IUnitOfWork _uow;
    private readonly IEnumerable<IPaymentProvider> _providers;
    private readonly IPermissionService _permissions;
    private readonly PaymentCredentialProtector _protector;
    private readonly IOptions<PaymentOptions> _options;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(
        ApplicationDbContext context,
        IUnitOfWork uow,
        IEnumerable<IPaymentProvider> providers,
        IPermissionService permissions,
        PaymentCredentialProtector protector,
        IOptions<PaymentOptions> options,
        ILogger<PaymentService> logger)
    {
        _context = context;
        _uow = uow;
        _providers = providers;
        _permissions = permissions;
        _protector = protector;
        _options = options;
        _logger = logger;
    }

    // ── Queries ─────────────────────────────────────────────

    /// <summary>Get a payment with tenant isolation. Customers see only their own; staff/business their business; admin any.</summary>
    public async Task<ApiResponse<PaymentResponse>> GetPaymentAsync(Guid paymentId, Guid userId, string role)
    {
        var payment = await _context.Payments
            .Include(p => p.Attempts.OrderBy(a => a.AttemptNumber))
            .FirstOrDefaultAsync(p => p.Id == paymentId);

        if (payment == null)
            return ApiResponse<PaymentResponse>.Fail("NOT_FOUND", "Payment not found.");

        if (!await CanAccessPaymentAsync(payment, userId, role))
            return ApiResponse<PaymentResponse>.Fail("NOT_FOUND", "Payment not found.");

        return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));
    }

    /// <summary>List payments with DB-first filters. Scope is enforced by role (tenant isolation).</summary>
    public async Task<ApiResponse<PaymentListResponse>> ListPaymentsAsync(PaymentListQuery query, Guid userId, string role)
    {
        var dbQuery = _context.Payments.AsNoTracking().AsQueryable();

        switch (role)
        {
            case "Customer":
                dbQuery = dbQuery.Where(p => p.CustomerId == userId);
                break;
            case "Staff":
            case "Business":
            {
                var businessId = await ResolveBusinessIdAsync(userId, role);
                if (businessId == null)
                    return ApiResponse<PaymentListResponse>.Fail("FORBIDDEN", "No business scope for user.");
                dbQuery = dbQuery.Where(p => p.BusinessId == businessId);
                break;
            }
            case "Admin":
                break;
            default:
                return ApiResponse<PaymentListResponse>.Fail("FORBIDDEN", "Role cannot list payments.");
        }

        if (query.AppointmentId.HasValue) dbQuery = dbQuery.Where(p => p.AppointmentId == query.AppointmentId);
        if (query.CustomerId.HasValue) dbQuery = dbQuery.Where(p => p.CustomerId == query.CustomerId);
        if (!string.IsNullOrWhiteSpace(query.Status) &&
            Enum.TryParse<PaymentStatus>(query.Status, true, out var status))
            dbQuery = dbQuery.Where(p => p.Status == status);
        if (!string.IsNullOrWhiteSpace(query.Method) &&
            Enum.TryParse<PaymentMethod>(query.Method, true, out var method))
            dbQuery = dbQuery.Where(p => p.Method == method);
        var hasTypeFilter = PaymentTypes.TryParse(query.Type, out var type);
        if (hasTypeFilter)
            dbQuery = dbQuery.Where(p => p.Type == type);
        if (query.From.HasValue) dbQuery = dbQuery.Where(p => p.CreatedAt >= query.From.Value);
        if (query.To.HasValue) dbQuery = dbQuery.Where(p => p.CreatedAt <= query.To.Value);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var total = await dbQuery.CountAsync();
        var items = await dbQuery
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Include(p => p.Attempts.OrderBy(a => a.AttemptNumber))
            .ToListAsync();

        return ApiResponse<PaymentListResponse>.Ok(new PaymentListResponse
        {
            Items = items.Select(ToResponse).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = total
        });
    }

    /// <summary>Server-computed payment summary for an appointment (service total, paid, balance, status).</summary>
    public async Task<ApiResponse<AppointmentPaymentSummary>> GetAppointmentPaymentSummaryAsync(
        Guid appointmentId, Guid userId, string role)
    {
        var appointment = await _context.Appointments
            .Include(a => a.Resources)
            .FirstOrDefaultAsync(a => a.Id == appointmentId);

        if (appointment == null)
            return ApiResponse<AppointmentPaymentSummary>.Fail("NOT_FOUND", "Appointment not found.");

        var access = await CanAccessAppointmentPaymentsAsync(appointment, userId, role);
        if (!access.allowed)
            return ApiResponse<AppointmentPaymentSummary>.Fail(access.notFound ? "NOT_FOUND" : "FORBIDDEN", access.message);

        return ApiResponse<AppointmentPaymentSummary>.Ok(await BuildSummaryAsync(appointment));
    }

    /// <summary>Dashboard aggregates for the caller's business (Business/Staff roles; Admin gets platform-wide).</summary>
    public async Task<ApiResponse<PaymentDashboardResponse>> GetDashboardAsync(Guid userId, string role)
    {
        if (role != "Business" && role != "Staff" && role != "Admin")
            return ApiResponse<PaymentDashboardResponse>.Fail("FORBIDDEN", "Only business users can view the payments dashboard.");

        Guid? businessId = role == "Admin" ? null : await ResolveBusinessIdAsync(userId, role);
        if (role != "Admin" && businessId == null)
            return ApiResponse<PaymentDashboardResponse>.Fail("FORBIDDEN", "No business scope for user.");

        var todayUtc = DateTime.UtcNow.Date;
        var monthStartUtc = new DateTime(todayUtc.Year, todayUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var scoped = _context.Payments.AsNoTracking().Where(p => businessId == null || p.BusinessId == businessId);

        var today = scoped.Where(p => p.Status == PaymentStatus.Success && p.CompletedAt >= todayUtc);
        var month = scoped.Where(p => p.Status == PaymentStatus.Success && p.CompletedAt >= monthStartUtc);

        var dashboard = new PaymentDashboardResponse
        {
            TodayCollected = await today.SumAsync(p => (decimal?)p.Amount) ?? 0m,
            TodayCount = await today.CountAsync(),
            MonthCollected = await month.SumAsync(p => (decimal?)p.Amount) ?? 0m,
            MonthCount = await month.CountAsync(),
            PendingCount = await scoped.CountAsync(p =>
                p.Status == PaymentStatus.Created || p.Status == PaymentStatus.AwaitingCustomer),
            PendingAmount = await scoped.Where(p =>
                    p.Status == PaymentStatus.Created || p.Status == PaymentStatus.AwaitingCustomer)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m,
            FailedCount = await scoped.CountAsync(p =>
                p.Status == PaymentStatus.Failed || p.Status == PaymentStatus.Expired),
            CashCollectedToday = await today.Where(p => p.Method == PaymentMethod.Cash).SumAsync(p => (decimal?)p.Amount) ?? 0m,
            MpesaCollectedToday = await today.Where(p => p.Method == PaymentMethod.Mpesa).SumAsync(p => (decimal?)p.Amount) ?? 0m,
            UnmatchedCallbackCount = await _context.PaymentCallbacks
                .Where(c => c.Kind == "c2b_confirmation" && c.PaymentId == null && c.Outcome == "unmatched")
                .CountAsync(c => businessId == null || c.BusinessId == businessId)
        };

        return ApiResponse<PaymentDashboardResponse>.Ok(dashboard);
    }

    // ── Lifecycle operations ────────────────────────────────

    /// <summary>
    /// Create a payment for an appointment. The amount is computed server-side from
    /// the immutable AppointmentResource price snapshots — the client never sends money.
    /// </summary>
    public async Task<ApiResponse<PaymentResponse>> CreatePaymentAsync(CreatePaymentRequest request, Guid userId, string role, string? idempotencyKey = null)
    {
        if (request.AppointmentId == Guid.Empty)
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "appointmentId is required.");

        if (!Enum.TryParse<PaymentMethod>(request.Method, true, out var method))
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "method must be 'cash' or 'mpesa'.");

        if (method == PaymentMethod.Mpesa && string.IsNullOrWhiteSpace(request.PhoneNumber))
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "phoneNumber is required for M-PESA payments.");

        if (method == PaymentMethod.Mpesa && !IsValidKenyanPhone(request.PhoneNumber!))
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "phoneNumber must be a valid Kenyan number (07XXXXXXXX/01XXXXXXXX/2547XXXXXXXX).");

        if (!PaymentTypes.TryParse(request.Type, out var type))
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", $"type must be {PaymentTypes.FullPayment}, {PaymentTypes.BookingFee} or {PaymentTypes.BalancePayment}.");

        var appointment = await _context.Appointments
            .Include(a => a.Resources)
            .FirstOrDefaultAsync(a => a.Id == request.AppointmentId);
        if (appointment == null)
            return ApiResponse<PaymentResponse>.Fail("NOT_FOUND", "Appointment not found.");

        switch (role)
        {
            case "Customer":
                if (appointment.CustomerId != userId)
                    return ApiResponse<PaymentResponse>.Fail("FORBIDDEN", "You can only pay for your own appointments.");
                break;
            case "Staff":
            case "Business":
            {
                var businessId = await ResolveBusinessIdAsync(userId, role);
                if (businessId == null || appointment.BusinessId != businessId)
                    return ApiResponse<PaymentResponse>.Fail("FORBIDDEN", "Appointment is outside your business.");
                break;
            }
            default:
                return ApiResponse<PaymentResponse>.Fail("FORBIDDEN", "Role cannot create payments.");
        }

        var config = await GetOrCreateConfigAsync(appointment.BusinessId);

        // Summed in memory: the shared test host runs SQLite, which cannot translate SUM over decimal columns.
        var paidAmounts = await _context.Payments
            .Where(p => p.AppointmentId == appointment.Id && p.Status == PaymentStatus.Success)
            .Select(p => p.Amount)
            .ToListAsync();
        var alreadyPaid = paidAmounts.Sum();

        var serviceTotal = appointment.Resources.Sum(r => r.Price);
        if (serviceTotal <= 0)
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "Appointment has no payable service total.");

        var amount = type switch
        {
            PaymentType.BookingFee => serviceTotal / 2m,
            PaymentType.BalancePayment => serviceTotal - alreadyPaid,
            _ => serviceTotal - alreadyPaid
        };

        if (amount <= 0)
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "Appointment is already fully paid.");

        if (method == PaymentMethod.Cash && !config.CashEnabled)
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "Cash payments are not enabled for this business.");
        if (method == PaymentMethod.Mpesa && !config.MpesaEnabled)
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "M-PESA payments are not enabled for this business.");

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            BusinessId = appointment.BusinessId,
            CustomerId = appointment.CustomerId,
            AppointmentId = appointment.Id,
            Type = type,
            Method = method,
            Provider = method == PaymentMethod.Cash ? PaymentProviderKind.Cash : PaymentProviderKind.DarajaStk,
            Status = PaymentStatus.Created,
            Amount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero),
            Currency = "KES",
            Reference = GenerateReference(),
            PhoneNumber = method == PaymentMethod.Mpesa ? NormalizePhone(request.PhoneNumber!) : null,
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow
        };

        await _context.Payments.AddAsync(payment);

        if (method == PaymentMethod.Mpesa)
        {
            var (ok, err) = await StartAttemptAsync(payment, config);
            if (!ok && err != null)
            {
                await _uow.SaveChangesAsync();
                return err;
            }
        }

        await _uow.SaveChangesAsync();

        _logger.LogInformation(
            "Payment {PaymentId} ({Reference}) created for business {BusinessId}, appointment {AppointmentId}, amount {Amount} {Currency}, method {Method}",
            payment.Id, payment.Reference, payment.BusinessId, payment.AppointmentId, payment.Amount, payment.Currency, method);

        await _context.Entry(payment).Collection(p => p.Attempts).LoadAsync();
        return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));
    }

    /// <summary>Authorised staff/business confirms physical cash was received. Audited, idempotent, never backdated.</summary>
    public async Task<ApiResponse<PaymentResponse>> ConfirmCashAsync(Guid paymentId, string? note, Guid userId, string role, string? idempotencyKey = null)
    {
        var payment = await _context.Payments
            .Include(p => p.Attempts.OrderBy(a => a.AttemptNumber))
            .FirstOrDefaultAsync(p => p.Id == paymentId);
        if (payment == null)
            return ApiResponse<PaymentResponse>.Fail("NOT_FOUND", "Payment not found.");

        var businessId = await ResolveBusinessIdAsync(userId, role);
        if (businessId == null || payment.BusinessId != businessId)
            return ApiResponse<PaymentResponse>.Fail("NOT_FOUND", "Payment not found.");

        if (role == "Staff" && !_permissions.HasPermission(role, "payments.confirm_cash"))
            return ApiResponse<PaymentResponse>.Fail("FORBIDDEN", "You are not allowed to confirm cash payments.");

        if (payment.Method != PaymentMethod.Cash)
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "Only cash payments can be confirmed as cash.");

        // Idempotency: confirming twice returns the same success — never creates a second payment.
        if (payment.Status == PaymentStatus.Success)
            return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));

        if (payment.Status is PaymentStatus.Cancelled or PaymentStatus.Expired or PaymentStatus.Reversed or PaymentStatus.Refunded)
            return ApiResponse<PaymentResponse>.Fail("INVALID_STATUS_TRANSITION", "Payment is " + payment.Status + " and cannot be confirmed.");

        // Guardrail: no silent backdating — cash confirmation happens in the present.
        if (payment.CreatedAt > DateTime.UtcNow.Add(CashConfirmGrace))
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "Payment was created in the future; cannot confirm yet.");

        PaymentStateMachine.Transition(payment, PaymentStatus.Success);
        payment.ConfirmedByUserId = userId;
        payment.ExternalReference = "CASH-" + payment.Reference;

        var attempt = new PaymentAttempt
        {
            Id = Guid.NewGuid(),
            PaymentId = payment.Id,
            AttemptNumber = payment.Attempts.Count + 1,
            Provider = PaymentProviderKind.Cash,
            Status = "success",
            ErrorMessage = string.IsNullOrWhiteSpace(note) ? null : Truncate(note, 500),
            CompletedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        AddAttempt(payment, attempt);

        await _uow.SaveChangesAsync();

        _logger.LogInformation(
            "Cash payment {PaymentId} confirmed by user {UserId} (business {BusinessId}), note present: {HasNote}",
            payment.Id, userId, payment.BusinessId, !string.IsNullOrWhiteSpace(note));

        return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));
    }

    /// <summary>Retry a failed/expired payment with a new attempt (manual). Cash payments cannot be retried.</summary>
    public async Task<ApiResponse<PaymentResponse>> RetryPaymentAsync(Guid paymentId, Guid userId, string role, string? idempotencyKey = null)
    {
        var payment = await _context.Payments
            .Include(p => p.Attempts.OrderBy(a => a.AttemptNumber))
            .FirstOrDefaultAsync(p => p.Id == paymentId);
        if (payment == null)
            return ApiResponse<PaymentResponse>.Fail("NOT_FOUND", "Payment not found.");

        if (!await CanAccessPaymentAsync(payment, userId, role))
            return ApiResponse<PaymentResponse>.Fail("NOT_FOUND", "Payment not found.");

        if (payment.Status is not (PaymentStatus.Failed or PaymentStatus.Expired or PaymentStatus.Created))
            return ApiResponse<PaymentResponse>.Fail("INVALID_STATUS_TRANSITION", "Payment in status " + payment.Status + " cannot be retried.");

        if (payment.Method == PaymentMethod.Cash)
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "Cash payments are confirmed by staff, not retried.");

        if (payment.Attempts.Count >= _options.Value.MaxAttempts)
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "Maximum attempts (" + _options.Value.MaxAttempts + ") reached for this payment.");

        var config = await GetOrCreateConfigAsync(payment.BusinessId);
        var (ok, err) = await StartAttemptAsync(payment, config);
        if (!ok && err != null)
        {
            await _uow.SaveChangesAsync();
            return err;
        }

        await _uow.SaveChangesAsync();
        await _context.Entry(payment).Collection(p => p.Attempts).LoadAsync();
        return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));
    }

    /// <summary>Cancel a payment that has not succeeded (customer, staff or business action).</summary>
    public async Task<ApiResponse<PaymentResponse>> CancelPaymentAsync(Guid paymentId, Guid userId, string role)
    {
        var payment = await _context.Payments
            .Include(p => p.Attempts.OrderBy(a => a.AttemptNumber))
            .FirstOrDefaultAsync(p => p.Id == paymentId);
        if (payment == null)
            return ApiResponse<PaymentResponse>.Fail("NOT_FOUND", "Payment not found.");

        if (!await CanAccessPaymentAsync(payment, userId, role))
            return ApiResponse<PaymentResponse>.Fail("NOT_FOUND", "Payment not found.");

        if (payment.Status is not (PaymentStatus.Created or PaymentStatus.AwaitingCustomer or PaymentStatus.Failed or PaymentStatus.Expired))
            return ApiResponse<PaymentResponse>.Fail("INVALID_STATUS_TRANSITION", "Payment in status " + payment.Status + " cannot be cancelled.");

        PaymentStateMachine.Transition(payment, PaymentStatus.Cancelled);
        await _uow.SaveChangesAsync();

        _logger.LogInformation("Payment {PaymentId} cancelled by user {UserId} ({Role})", payment.Id, userId, role);
        return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));
    }

    /// <summary>
    /// Request a reversal/refund of a successful payment. Business-owner only; admins
    /// inspect but never alter financial records. Automated Daraja reversal requires
    /// Safaricom-approved production credentials (initiator name + security credential);
    /// when the provider cannot execute it, the payment is recorded as a manual refund
    /// adjustment with the reason — an audited record, never a silent edit.
    /// </summary>
    public async Task<ApiResponse<PaymentResponse>> ReversePaymentAsync(Guid paymentId, string reason, Guid userId)
    {
        var payment = await _context.Payments
            .Include(p => p.Attempts.OrderBy(a => a.AttemptNumber))
            .FirstOrDefaultAsync(p => p.Id == paymentId);
        if (payment == null)
            return ApiResponse<PaymentResponse>.Fail("NOT_FOUND", "Payment not found.");

        var ownerId = await _context.Businesses.AsNoTracking()
            .Where(b => b.Id == payment.BusinessId)
            .Select(b => (Guid?)b.OwnerId)
            .FirstOrDefaultAsync();

        if (ownerId == null || ownerId != userId)
            return ApiResponse<PaymentResponse>.Fail("FORBIDDEN", "Only the business owner can reverse a payment.");

        if (string.IsNullOrWhiteSpace(reason))
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "A reversal reason is required.");

        if (payment.Status != PaymentStatus.Success)
            return ApiResponse<PaymentResponse>.Fail("INVALID_STATUS_TRANSITION", "Only successful payments can be reversed (current: " + payment.Status + ").");

        var config = await GetOrCreateConfigAsync(payment.BusinessId);
        var provider = _providers.FirstOrDefault(p => p.Kind == payment.Provider);

        var result = provider == null
            ? new ProviderReversalResult { Success = false, Supported = false }
            : await provider.ReverseAsync(payment, reason, config);

        if (result.Success)
        {
            PaymentStateMachine.Transition(payment, PaymentStatus.Reversed);
            payment.ReversalReason = Truncate(reason, 500);
            if (!string.IsNullOrEmpty(result.TransactionId))
                payment.ExternalReference = result.TransactionId;
        }
        else if (result.Supported)
        {
            return ApiResponse<PaymentResponse>.Fail("PROVIDER_ERROR",
                "Reversal failed at the provider: " + (result.ErrorMessage ?? result.ErrorCode ?? "unknown error"));
        }
        else
        {
            PaymentStateMachine.Transition(payment, PaymentStatus.Refunded);
            payment.ReversalReason = Truncate("MANUAL REFUND: " + reason, 500);
        }

        await _uow.SaveChangesAsync();

        _logger.LogWarning(
            "Payment {PaymentId} outcome {Outcome} by business owner {UserId}; reason: {Reason}",
            payment.Id, payment.Status == PaymentStatus.Reversed ? "REVERSED(provider)" : "REFUNDED(manual record)", userId, reason);

        return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));
    }

    /// <summary>Expire stale AwaitingCustomer payments (maintenance path).</summary>
    public async Task<int> ExpireStalePaymentsAsync()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-_options.Value.StkExpiryMinutes);
        var stale = await _context.Payments
            .Where(p => p.Status == PaymentStatus.AwaitingCustomer && p.CreatedAt < cutoff)
            .ToListAsync();

        foreach (var payment in stale)
        {
            PaymentStateMachine.Transition(payment, PaymentStatus.Expired);
            var lastAttempt = payment.Attempts.OrderByDescending(a => a.AttemptNumber).FirstOrDefault();
            if (lastAttempt != null && lastAttempt.Status == "pending")
            {
                lastAttempt.Status = "expired";
                lastAttempt.ErrorCode = "PUNCHED_TIMEOUT";
                lastAttempt.ErrorMessage = "No provider callback within the allowed window.";
                lastAttempt.CompletedAt = DateTime.UtcNow;
            }
        }

        if (stale.Count > 0)
            await _uow.SaveChangesAsync();

        return stale.Count;
    }

    // ── Internal helpers ────────────────────────────────────

    /// <summary>Start a new provider attempt (STK push). Errors are recorded on the attempt; the payment moves to Failed.</summary>
    private async Task<(bool success, ApiResponse<PaymentResponse>? response)> StartAttemptAsync(
        Payment payment, BusinessPaymentConfig config)
    {
        var provider = _providers.FirstOrDefault(p => p.Kind == payment.Provider);
        if (provider == null)
        {
            return (false, ApiResponse<PaymentResponse>.Fail("PROVIDER_ERROR", "No provider registered for " + payment.Provider + "."));
        }

        var attempt = new PaymentAttempt
        {
            Id = Guid.NewGuid(),
            PaymentId = payment.Id,
            AttemptNumber = payment.Attempts.Count + 1,
            Provider = payment.Provider,
            Status = "pending",
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            var result = await provider.InitiateAsync(payment, config);
            if (result.Success)
            {
                attempt.CheckoutRequestId = result.CheckoutRequestId;
                attempt.MerchantRequestId = result.MerchantRequestId;
                if (payment.Status != PaymentStatus.AwaitingCustomer)
                    PaymentStateMachine.Transition(payment, PaymentStatus.AwaitingCustomer);
                payment.FailedAt = null;
                _logger.LogInformation(
                    "STK push initiated for payment {PaymentId} attempt {AttemptNumber}: checkout={CheckoutRequestId}",
                    payment.Id, attempt.AttemptNumber, result.CheckoutRequestId);
            }
            else
            {
                attempt.Status = "failed";
                attempt.ErrorCode = result.ErrorCode;
                attempt.ErrorMessage = result.ErrorMessage;
                attempt.CompletedAt = DateTime.UtcNow;
                PaymentStateMachine.Transition(payment, PaymentStatus.Failed);
                _logger.LogWarning(
                    "STK push failed for payment {PaymentId} attempt {AttemptNumber}: {ErrorCode} {ErrorMessage} (retryable={Retryable})",
                    payment.Id, attempt.AttemptNumber, result.ErrorCode, result.ErrorMessage, result.Retryable);
            }
        }
        catch (Exception ex)
        {
            attempt.Status = "failed";
            attempt.ErrorCode = "PROVIDER_UNREACHABLE";
            attempt.ErrorMessage = Truncate(ex.Message, 500);
            attempt.CompletedAt = DateTime.UtcNow;
            if (PaymentStateMachine.CanTransition(payment.Status, PaymentStatus.Failed))
                PaymentStateMachine.Transition(payment, PaymentStatus.Failed);
            _logger.LogError(ex, "Provider initiation threw for payment {PaymentId}", payment.Id);
        }

        AddAttempt(payment, attempt);
        return (attempt.Status == "pending", null);
    }

    /// <summary>
    /// Adds an attempt through the DbSet so EF marks it as Added. An attempt created with an
    /// app-assigned GUID key and discovered only through the payment.Attempts navigation is
    /// attached as MODIFIED instead - issuing an UPDATE against a row that was never inserted,
    /// which fails with DbUpdateConcurrencyException (rows affected 0).
    /// </summary>
    private void AddAttempt(Payment payment, PaymentAttempt attempt)
    {
        _context.PaymentAttempts.Add(attempt);
        // Added to the navigation too so callers reading payment.Attempts stay consistent.
        payment.Attempts.Add(attempt);
    }

    private async Task<Guid?> ResolveBusinessIdAsync(Guid userId, string role)
    {
        if (role == "Business")
        {
            return await _context.Businesses.AsNoTracking()
                .Where(b => b.OwnerId == userId && !b.IsDeleted)
                .Select(b => (Guid?)b.Id)
                .FirstOrDefaultAsync();
        }

        if (role == "Staff")
        {
            return await _context.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.StaffBusinessId)
                .FirstOrDefaultAsync();
        }

        return null;
    }

    private async Task<bool> CanAccessPaymentAsync(Payment payment, Guid userId, string role)
    {
        switch (role)
        {
            case "Customer":
                return payment.CustomerId == userId;
            case "Staff":
            case "Business":
            {
                var businessId = await ResolveBusinessIdAsync(userId, role);
                return businessId != null && payment.BusinessId == businessId;
            }
            case "Admin":
                return true;
            default:
                return false;
        }
    }

    private async Task<(bool allowed, bool notFound, string message)> CanAccessAppointmentPaymentsAsync(
        Appointment appointment, Guid userId, string role)
    {
        switch (role)
        {
            case "Customer":
                if (appointment.CustomerId != userId)
                    return (false, true, "Appointment not found.");
                return (true, false, string.Empty);
            case "Staff":
            case "Business":
            {
                var businessId = await ResolveBusinessIdAsync(userId, role);
                if (businessId == null)
                    return (false, true, "Appointment not found.");
                if (appointment.BusinessId != businessId)
                    return (false, true, "Appointment not found.");
                return (true, false, string.Empty);
            }
            case "Admin":
                return (true, false, string.Empty);
            default:
                return (false, false, "Role cannot view appointment payments.");
        }
    }

    private async Task<AppointmentPaymentSummary> BuildSummaryAsync(Appointment appointment)
    {
        var payments = await _context.Payments
            .Include(p => p.Attempts.OrderBy(a => a.AttemptNumber))
            .Where(p => p.AppointmentId == appointment.Id)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        var serviceTotal = appointment.Resources.Sum(r => r.Price);
        var paid = payments.Where(p => p.Status == PaymentStatus.Success).Sum(p => p.Amount);

        return new AppointmentPaymentSummary
        {
            AppointmentId = appointment.Id,
            ServiceTotal = serviceTotal,
            AmountPaid = paid,
            Balance = Math.Max(0, serviceTotal - paid),
            PaymentStatus = paid <= 0 ? "unpaid" : paid >= serviceTotal ? "paid" : "partially_paid",
            Payments = payments.Select(ToResponse).ToList()
        };
    }

    /// <summary>Get (or lazily create) the payment configuration for a business.</summary>
    public async Task<BusinessPaymentConfig> GetOrCreateConfigAsync(Guid businessId)
    {
        var config = await _context.BusinessPaymentConfigs.FirstOrDefaultAsync(c => c.BusinessId == businessId);
        if (config != null) return config;

        config = new BusinessPaymentConfig
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CashEnabled = true,
            MpesaEnabled = false,
            CreatedAt = DateTime.UtcNow
        };
        await _context.BusinessPaymentConfigs.AddAsync(config);
        await _uow.SaveChangesAsync();
        return config;
    }

    /// <summary>Generate a unique Punched payment reference: PMT- + 12 hex chars. Used as C2B BillRefNumber / STK AccountReference.</summary>
    private static string GenerateReference()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return "PMT-" + Convert.ToHexString(bytes);
    }

    /// <summary>Valid Kenyan MSISDN: 07XXXXXXXX, 01XXXXXXXX, 2547/2541…</summary>
    private static bool IsValidKenyanPhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("2547") || digits.StartsWith("2541")) return digits.Length == 12;
        return (digits.StartsWith("07") || digits.StartsWith("01")) && digits.Length == 10;
    }

    /// <summary>Normalise to Safaricom MSISDN format 2547XXXXXXXX.</summary>
    private static string NormalizePhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("0") && digits.Length == 10) return "254" + digits[1..];
        return digits;
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];

    private static PaymentResponse ToResponse(Payment payment) => new()
    {
        Id = payment.Id,
        BusinessId = payment.BusinessId,
        CustomerId = payment.CustomerId,
        AppointmentId = payment.AppointmentId,
        Type = PaymentTypes.ToWire(payment.Type),
        Method = payment.Method.ToString().ToLowerInvariant(),
        Provider = payment.Provider.ToString().ToLowerInvariant(),
        Status = payment.Status.ToString().ToLowerInvariant(),
        Amount = payment.Amount,
        Currency = payment.Currency,
        Reference = payment.Reference,
        ExternalReference = payment.ExternalReference,
        PhoneNumber = payment.PhoneNumber,
        CreatedAt = payment.CreatedAt,
        CompletedAt = payment.CompletedAt,
        Attempts = payment.Attempts.OrderBy(a => a.AttemptNumber).Select(a => new PaymentAttemptResponse
        {
            AttemptNumber = a.AttemptNumber,
            Provider = a.Provider.ToString().ToLowerInvariant(),
            Status = a.Status,
            CheckoutRequestId = a.CheckoutRequestId,
            ProviderReference = a.ProviderReference,
            ErrorCode = a.ErrorCode,
            ErrorMessage = a.ErrorMessage,
            CreatedAt = a.CreatedAt,
            CompletedAt = a.CompletedAt
        }).ToList()
    };
}
