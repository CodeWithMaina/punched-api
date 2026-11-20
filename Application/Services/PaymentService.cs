using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
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
    private readonly INotificationService _notifications;
    private readonly ILogger<PaymentService> _logger;

    /// <summary>
    /// Optional commission ledger. Optional so every existing construction site
    /// (including the test suite) keeps compiling; when present, a successful
    /// payment releases the appointment's accrued commission and a reversal
    /// claws it back.
    /// </summary>
    private readonly ICommissionService? _commissions;

    /// <summary>
    /// Optional financial ledger. Optional so every existing construction site (including the test
    /// suite) keeps compiling; when present, a settled payment is recognised in the business's books
    /// exactly once, and a reversal records a linked refund.
    /// </summary>
    private readonly IFinancialLedgerService? _ledger;

    /// <summary>
    /// Optional durable outbox: the obligation that a settled payment becomes income. When present,
    /// settlement commits the obligation in the same transaction as the payment status, and the
    /// processor (or the worker) finishes the books.
    /// </summary>
    private readonly IPaymentLedgerOutbox? _ledgerOutbox;

    /// <summary>Optional immediate ledger attempt after settlement; the worker remains the guarantee.</summary>
    private readonly IPaymentLedgerProcessor? _ledgerProcessor;

    /// <summary>Optional serialisation of concurrent verify/reject decisions (PostgreSQL advisory lock).</summary>
    private readonly IPaymentDecisionGuard? _decisionGuard;

    public PaymentService(
        ApplicationDbContext context,
        IUnitOfWork uow,
        IEnumerable<IPaymentProvider> providers,
        IPermissionService permissions,
        PaymentCredentialProtector protector,
        IOptions<PaymentOptions> options,
        INotificationService notifications,
        ILogger<PaymentService> logger,
        ICommissionService? commissions = null,
        IFinancialLedgerService? ledger = null,
        IPaymentLedgerOutbox? ledgerOutbox = null,
        IPaymentLedgerProcessor? ledgerProcessor = null,
        IPaymentDecisionGuard? decisionGuard = null)
    {
        _context = context;
        _uow = uow;
        _providers = providers;
        _permissions = permissions;
        _protector = protector;
        _options = options;
        _notifications = notifications;
        _logger = logger;
        _commissions = commissions;
        _ledger = ledger;
        _ledgerOutbox = ledgerOutbox;
        _ledgerProcessor = ledgerProcessor;
        _decisionGuard = decisionGuard;
    }

    /// <summary>
    /// Releases commission for an appointment whose payment just settled.
    /// Idempotent (the ledger dedupes and only moves statuses forward) and
    /// non-fatal: a commission problem never fails a confirmed payment.
    /// </summary>
    private async Task SettleCommissionSafelyAsync(Guid businessId, Guid? appointmentId, Guid paymentId)
    {
        if (_commissions == null || appointmentId is not Guid id) return;

        try
        {
            await _commissions.SettleForAppointmentAsync(businessId, id, paymentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Commission settlement failed for appointment {AppointmentId} after payment {PaymentId}; the payment stands.",
                id, paymentId);
        }
    }

    /// <summary>
    /// Persists a settlement together with its durable ledger obligation.
    ///
    /// <para>The payment's settled status and the obligation to recognise it are committed in ONE
    /// transaction, so there is no window in which a settled payment exists with neither income nor a
    /// record that income is still owed. On PostgreSQL this is a real transaction; on SQLite/InMemory
    /// (tests) the two writes still happen back-to-back inside the same unit of work.</para>
    ///
    /// <para>After the commit the ledger write is attempted immediately — the common case is still
    /// one request, one income row. If that attempt fails, the obligation is already durable and the
    /// worker retries it; the payment is never rolled back for a books problem.</para>
    /// </summary>
    private async Task CommitSettlementAsync(Func<Task> save, Payment payment, CancellationToken cancellationToken = default)
    {
        if (_ledgerOutbox == null)
        {
            // Legacy construction (tests that pass only the ledger): keep the old best-effort path.
            await save();
            return;
        }

        if (_context.Database.IsRelational() && _context.Database.CurrentTransaction == null)
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            await save();
            await _ledgerOutbox.EnsureObligationAsync(payment, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            // Either not relational, or the caller already owns a decision transaction (the advisory
            // lock scope). In both cases the save and the obligation share that unit of work.
            await save();
            await _ledgerOutbox.EnsureObligationAsync(payment, cancellationToken);
        }
    }

    /// <summary>
    /// Turns a settled payment into income, or guarantees that the worker will.
    ///
    /// <para>When the outbox is present this never calls the ledger directly for the "write" step —
    /// the processor owns the attempt and its retry policy — so there is exactly one code path that
    /// can create income and exactly one place that decides what a failure means.</para>
    /// </summary>
    private async Task RecogniseInLedgerSafelyAsync(Payment payment, CancellationToken cancellationToken = default)
    {
        if (!PaymentStatuses.IsSettled(payment.Status)) return;

        if (_ledgerOutbox != null)
        {
            try
            {
                // Idempotent: the settlement transaction already inserted this; a legacy settlement
                // (or a replayed callback) gets the obligation here.
                await _ledgerOutbox.EnsureObligationAsync(payment, cancellationToken);

                if (_ledgerProcessor != null)
                {
                    var result = await _ledgerProcessor.ProcessAsync(payment.Id, cancellationToken);
                    if (result.Outcome is LedgerProcessOutcome.Created or LedgerProcessOutcome.AlreadyRecorded)
                    {
                        _logger.LogInformation(
                            "Payment {PaymentId} recorded in the ledger as {TransactionId} ({Outcome}).",
                            payment.Id, result.FinancialTransactionId, result.Outcome);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Payment {PaymentId} settled but its ledger row is not written yet ({Outcome}, {Code}); the outbox retains the obligation.",
                            payment.Id, result.Outcome, result.ErrorCode);
                    }
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The obligation is already committed; the worker will finish the job.
                _logger.LogError(ex,
                    "Immediate ledger processing for payment {PaymentId} failed; the obligation stays queued for retry.",
                    payment.Id);
                return;
            }
        }

        if (_ledger == null) return;

        try
        {
            await _ledger.EnsureIncomeForPaymentAsync(payment, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not recognise payment {PaymentId} in the financial ledger; the payment itself stands.",
                payment.Id);
        }
    }

    /// <summary>
    /// Records the refund that accompanies a reversal. Linked to the income row, never to the original
    /// payment record, so the historical receipt stays exactly as it was.
    /// </summary>
    private async Task RecordRefundInLedgerSafelyAsync(Payment payment, string reason)
    {
        if (_ledger == null) return;

        try
        {
            await _ledger.EnsureRefundForReversedPaymentAsync(payment, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not record the refund for reversed payment {PaymentId} in the financial ledger.",
                payment.Id);
        }
    }

    /// <summary>
    /// Reverses commission because a payment was refunded or reversed.
    /// Idempotent and non-fatal, for the same reasons as settlement.
    /// </summary>
    private async Task ReverseCommissionSafelyAsync(Guid businessId, Guid? appointmentId, Guid paymentId, string reason)
    {
        if (_commissions == null || appointmentId is not Guid id) return;

        try
        {
            await _commissions.ReverseForPaymentAsync(businessId, id, paymentId, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Commission reversal failed for appointment {AppointmentId} after payment {PaymentId} was refunded; the payment stands.",
                id, paymentId);
        }
    }

    // ── Queries ─────────────────────────────────────────────

    /// <summary>Get a payment with tenant isolation. Customers see only their own; staff/business their business; admin any.</summary>
    public async Task<ApiResponse<PaymentResponse>> GetPaymentAsync(Guid paymentId, Guid userId, string role)
    {
        var payment = await _context.Payments
            .Include(p => p.Attempts.OrderBy(a => a.AttemptNumber))
            // The verification trail is part of the payment contract, so the single-payment
            // read carries it too — a customer checking on their submission sees the same
            // history the owner does.
            .Include(p => p.VerificationEvents)
            .FirstOrDefaultAsync(p => p.Id == paymentId);

        if (payment == null)
            return ApiResponse<PaymentResponse>.Fail("NOT_FOUND", "Payment not found.");

        if (!await CanAccessPaymentAsync(payment, userId, role))
            return ApiResponse<PaymentResponse>.Fail("NOT_FOUND", "Payment not found.");

        // Carry the customer's display name on the single-payment read too (the list
        // already resolves it for its page); without this the detail page cannot name them.
        var response = ToResponse(payment);
        if (payment.CustomerId is Guid customerId)
        {
            var names = await FinancialAccess.ResolveUserNamesAsync(_context, new[] { (Guid?)customerId });
            response.CustomerName = names.GetValueOrDefault(customerId);
        }

        // The cashbook row this payment became, when it became one. The detail screen must be able
        // to say "this is the entry, recorded by this person" instead of guessing from the status —
        // and it must be blank when no row exists, which is exactly what a null link says.
        var ledger = await _context.FinancialTransactions.AsNoTracking()
            .Where(t => t.SourcePaymentId == payment.Id && !t.IsVoided)
            .Select(t => new { t.Id, t.Reference, t.CreatedByUserId })
            .FirstOrDefaultAsync();

        if (ledger != null)
        {
            response.FinancialTransactionId = ledger.Id;
            response.LedgerReference = ledger.Reference;
            if (ledger.CreatedByUserId is Guid recorder)
            {
                var recorderNames = await FinancialAccess.ResolveUserNamesAsync(_context, new[] { (Guid?)recorder });
                response.LedgerRecordedByName = recorderNames.GetValueOrDefault(recorder);
            }
        }

        // Settlement and bookkeeping are distinct states, and the screen must be able to say so:
        // a settled payment whose income has not been written yet is "processing", not "recorded".
        if (ledger != null)
        {
            response.LedgerProcessingStatus = "recorded";
        }
        else if (PaymentStatuses.IsSettled(payment.Status))
        {
            var obligationStatus = await _context.PaymentLedgerObligations
                .AsNoTracking()
                .Where(o => o.PaymentId == payment.Id)
                .Select(o => o.Status)
                .FirstOrDefaultAsync();

            response.LedgerProcessingStatus =
                obligationStatus == PaymentLedgerObligationStatus.Failed ? "attention" : "processing";
        }

        return ApiResponse<PaymentResponse>.Ok(response);
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

        // `claims` is a GROUP, not a status: the "Needs verification" tab must page and count the
        // claims the owner still has to decide (submitted, under review, flagged) as ONE list. A
        // single-status filter would silently leave flagged claims out of the queue they belong to.
        var statusFilter = query.Status?.Trim();
        if (IsClaimsFilter(statusFilter))
        {
            dbQuery = dbQuery.Where(p =>
                p.Status == PaymentStatus.Submitted
                || p.Status == PaymentStatus.UnderReview
                || p.Status == PaymentStatus.Flagged);
        }
        else if (!string.IsNullOrWhiteSpace(statusFilter) &&
            PaymentStatuses.TryParse(statusFilter, out var status))
        {
            dbQuery = dbQuery.Where(p => p.Status == status);
        }
        else if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            // An unreadable status must not quietly widen the register to every payment: a money
            // screen that says "filtered" while showing everything is the one failure mode this
            // register can never have. Fail closed with an empty page instead.
            dbQuery = dbQuery.Where(p => false);
        }
        if (!string.IsNullOrWhiteSpace(query.Method) &&
            Enum.TryParse<PaymentMethod>(query.Method, true, out var method))
            dbQuery = dbQuery.Where(p => p.Method == method);
        var hasTypeFilter = PaymentTypes.TryParse(query.Type, out var type);
        if (hasTypeFilter)
            dbQuery = dbQuery.Where(p => p.Type == type);
        if (query.From.HasValue) dbQuery = dbQuery.Where(p => p.CreatedAt >= query.From.Value);
        if (query.To.HasValue) dbQuery = dbQuery.Where(p => p.CreatedAt <= query.To.Value);
        // Server-side free-text search across the payment's own fields and its customer.
        // EF.Functions.Like keeps this a parameterised SQL predicate (never in-memory filtering),
        // and the customer condition is an EXISTS subquery, so a page never fans out into N+1.
        var search = query.Search?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = $"%{search}%";
            var isCash = string.Equals(search, "cash", StringComparison.OrdinalIgnoreCase);
            var isMpesa = string.Equals(search, "mpesa", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(search, "m-pesa", StringComparison.OrdinalIgnoreCase);
            dbQuery = dbQuery.Where(p =>
                EF.Functions.Like(p.Reference, like) ||
                (p.PhoneNumber != null && EF.Functions.Like(p.PhoneNumber, like)) ||
                (p.ParsedTransactionCode != null && EF.Functions.Like(p.ParsedTransactionCode, like)) ||
                (p.ExternalReference != null && EF.Functions.Like(p.ExternalReference, like)) ||
                (p.CustomerId != null && _context.Users.Any(u =>
                    u.Id == p.CustomerId &&
                    (EF.Functions.Like(u.FullName, like) || (u.Email != null && EF.Functions.Like(u.Email, like))))) ||
                (isCash && p.Method == PaymentMethod.Cash) ||
                (isMpesa && p.Method == PaymentMethod.Mpesa));
        }

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var total = await dbQuery.CountAsync();
        var items = await OrderPayments(dbQuery, query.Sort)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Include(p => p.Attempts.OrderBy(a => a.AttemptNumber))
            .ToListAsync();

        var responses = items.Select(ToResponse).ToList();

        // Customer names for the visible page only, in one query: the payments register is read
        // by a person looking for somebody's money, so a column of ids would be useless.
        var names = await FinancialAccess.ResolveUserNamesAsync(_context, items.Select(p => p.CustomerId));
        for (var index = 0; index < responses.Count; index++)
        {
            responses[index].CustomerName = items[index].CustomerId is Guid customerId
                ? names.GetValueOrDefault(customerId)
                : null;
        }

        return ApiResponse<PaymentListResponse>.Ok(new PaymentListResponse
        {
            Items = responses,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            TotalPages = pageSize <= 0 ? 1 : (int)Math.Ceiling(total / (double)pageSize)
        });
    }

    /// <summary>
    /// True when a status filter names the awaiting-verification GROUP rather than one status.
    /// Accepts the labels a client naturally sends: <c>claims</c>, <c>awaiting</c>,
    /// <c>needs_verification</c>, <c>awaiting_verification</c>.
    /// </summary>
    private static bool IsClaimsFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var folded = new string(value.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return folded is "claims" or "awaiting" or "awaitingverification" or "needsverification";
    }

    /// <summary>
    /// The register's ordering, server-side. Unknown/blank values fall back to newest-first, so a
    /// list request can never fail because of an ordering name.
    /// </summary>
    private IOrderedQueryable<Payment> OrderPayments(IQueryable<Payment> query, string? sort)
    {
        var key = string.IsNullOrWhiteSpace(sort) ? "date-d" : sort.Trim().ToLowerInvariant();

        return key switch
        {
            "date-a" or "oldest" or "oldest-first" =>
                query.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id),
            "amt-d" or "amount-desc" or "highest" =>
                query.OrderByDescending(p => p.Amount).ThenByDescending(p => p.CreatedAt),
            "amt-a" or "amount-asc" or "lowest" =>
                query.OrderBy(p => p.Amount).ThenByDescending(p => p.CreatedAt),
            "name-a" or "name" or "name-asc" =>
                query.OrderBy(p => _context.Users
                        .Where(u => u.Id == p.CustomerId)
                        .Select(u => u.FullName)
                        .FirstOrDefault() ?? string.Empty)
                    .ThenByDescending(p => p.CreatedAt),
            _ => query.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id)
        };
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

        var settled = new[] { PaymentStatus.Success, PaymentStatus.Verified };

        var today = scoped.Where(p => settled.Contains(p.Status) && p.CompletedAt >= todayUtc);
        var month = scoped.Where(p => settled.Contains(p.Status) && p.CompletedAt >= monthStartUtc);

        var awaitingVerification = scoped.Where(p =>
            p.Status == PaymentStatus.Submitted || p.Status == PaymentStatus.UnderReview);

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
            AwaitingVerificationCount = await awaitingVerification.CountAsync(),
            AwaitingVerificationAmount = await awaitingVerification.SumAsync(p => (decimal?)p.Amount) ?? 0m,
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

        if (!Enum.TryParse<PaymentMethod>(request.Method, true, out var method) ||
            method == PaymentMethod.Unspecified)
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "method must be 'cash' or 'mpesa'.");

        // The phone number is only required when the platform drives the handset (an
        // STK push). A manual M-PESA payment is made by the customer themselves, so
        // the requirement is decided later, once the business's configuration says
        // which channel applies.

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
        // "Already paid" is every SETTLED payment — provider-confirmed and owner-verified
        // alike. Counting only Success would let a verified manual M-PESA payment be
        // charged again, which is exactly the overpayment the balance maths must prevent.
        var settledAmounts = await _context.Payments
            .Where(p => p.AppointmentId == appointment.Id &&
                        (p.Status == PaymentStatus.Success || p.Status == PaymentStatus.Verified))
            .Select(p => p.Amount)
            .ToListAsync();
        var alreadyPaid = settledAmounts.Sum();

        var serviceTotal = appointment.Resources.Sum(r => r.Price);
        if (serviceTotal <= 0)
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "Appointment has no payable service total.");

        // The booking fee is the business's own percentage of the service total, not
        // a platform constant. A percentage of 0 means "nothing up front", which is a
        // valid configuration — it is reported as such rather than silently charged.
        if (type == PaymentType.BookingFee && config.BookingFeeFor(serviceTotal) <= 0)
            return ApiResponse<PaymentResponse>.Fail(
                "VALIDATION_ERROR", "This business does not collect a booking fee.");

        var amount = type switch
        {
            PaymentType.BookingFee => config.BookingFeeFor(serviceTotal),
            PaymentType.BalancePayment => serviceTotal - alreadyPaid,
            _ => serviceTotal - alreadyPaid
        };

        if (amount <= 0)
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "Appointment is already fully paid.");

        // Manual M-PESA: the business has published its own PayBill/Till and collects
        // by the customer paying it themselves. No Daraja call happens, so the payment
        // is created Pending and waits for a pasted confirmation + an owner decision.
        var manualMpesa = method == PaymentMethod.Mpesa && config.HasManualMpesaDetails;

        if (method == PaymentMethod.Cash && !config.CashEnabled)
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "Cash payments are not enabled for this business.");
        if (method == PaymentMethod.Mpesa && !manualMpesa && !config.MpesaEnabled)
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "M-PESA payments are not enabled for this business.");

        // A stated amount is a claim about what was handed over, so it is never trusted:
        // it is validated against the balance the server just computed and can never
        // exceed it. It is accepted for the two flows where a human states the figure —
        // a partial cash collection, and a manual M-PESA payment the operator is
        // recording (the customer's number and the amount they sent). Anywhere else it
        // is rejected outright rather than silently ignored, so a client cannot smuggle
        // money into a provider row whose amount is fixed by the request we send Safaricom.
        if (request.Amount.HasValue)
        {
            if (!(method == PaymentMethod.Cash && type == PaymentType.BalancePayment) && !manualMpesa)
                return ApiResponse<PaymentResponse>.Fail(
                    "VALIDATION_ERROR",
                    "An amount may only be supplied for a cash balance payment or a manual M-PESA payment.");

            var requested = decimal.Round(request.Amount.Value, 2, MidpointRounding.AwayFromZero);
            if (requested <= 0)
                return ApiResponse<PaymentResponse>.Fail(
                    "VALIDATION_ERROR", "The amount must be greater than zero.");
            if (requested > amount)
                return ApiResponse<PaymentResponse>.Fail(
                    "VALIDATION_ERROR", "The amount exceeds the outstanding balance.");

            amount = requested;
        }

        // Duplicate guard. An appointment carries at most ONE open payment of a given
        // type, and the row a "pay after service" booking created IS that row — so
        // collecting against it updates it instead of adding a second one, which is how
        // a booking fee or a balance gets collected twice.
        var openPayments = await _context.Payments
            .Include(p => p.Attempts)
            .Where(p => p.AppointmentId == appointment.Id &&
                        (p.Status == PaymentStatus.Created ||
                         p.Status == PaymentStatus.AwaitingCustomer ||
                         p.Status == PaymentStatus.Processing ||
                         p.Status == PaymentStatus.Submitted ||
                         p.Status == PaymentStatus.UnderReview))
            .ToListAsync();

        // Reusable: the outstanding row ("pay after service") that nothing has touched yet.
        // A request for the whole appointment or for the balance both settle the SAME
        // obligation, so both reuse that row; a booking fee is a separate up-front charge
        // and never hijacks it.
        var reusable = type is PaymentType.BalancePayment or PaymentType.FullPayment;
        var outstanding = reusable
            ? openPayments.FirstOrDefault(p =>
                p.Type == PaymentType.BalancePayment &&
                p.Status == PaymentStatus.Created &&
                p.Method == PaymentMethod.Unspecified &&
                p.Attempts.Count == 0)
            : null;

        // Anything else already open for the same purpose is an in-flight payment: it has to
        // be settled, retried or cancelled instead of being silently duplicated.
        var conflicting = openPayments.FirstOrDefault(p => p.Id != outstanding?.Id && p.Type == type);
        if (conflicting != null)
            return ApiResponse<PaymentResponse>.Fail(
                "DUPLICATE_PAYMENT",
                $"This appointment already has an open {PaymentTypes.ToWire(type)} payment. " +
                "Settle, retry or cancel it instead of recording a second one.");

        var payment = outstanding ?? new Payment
        {
            Id = Guid.NewGuid(),
            BusinessId = appointment.BusinessId,
            CustomerId = appointment.CustomerId,
            AppointmentId = appointment.Id,
            Type = type,
            Status = PaymentStatus.Created,
            Currency = "KES",
            Reference = GenerateReference(),
            CreatedAt = DateTime.UtcNow
        };

        if (outstanding != null)
        {
            // Collecting only part of what is owed: the amount that arrived is this row,
            // and the rest is left as its own outstanding row.
            var received = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
            var owed = outstanding.Amount;
            if (received < owed)
            {
                // Part of the balance is being taken. The amount that arrived is this row;
                // what is still owed becomes its own outstanding row, so the balance the
                // business sees is the same figure from every angle.
                await _context.Payments.AddAsync(PaymentOutstanding.Create(
                    outstanding.BusinessId,
                    outstanding.CustomerId ?? Guid.Empty,
                    outstanding.AppointmentId ?? Guid.Empty,
                    owed - received,
                    userId,
                    DateTime.UtcNow));
            }
        }

        // The channel and the figure just chosen are written onto the row. Its amount is
        // rewritten to the balance that is genuinely outstanding, so an outstanding row
        // created before an earlier partial collection cannot re-charge the part that has
        // already been paid.
        payment.Method = method;
        payment.Provider = method == PaymentMethod.Cash
            ? PaymentProviderKind.Cash
            : manualMpesa ? PaymentProviderKind.ManualMpesa : PaymentProviderKind.DarajaStk;
        payment.Amount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        payment.CreatedByUserId = userId;
        // A manual payment is made *from* the customer's own handset, so their number is
        // recorded but is not a required input for the flow.
        if (method == PaymentMethod.Mpesa && !string.IsNullOrWhiteSpace(request.PhoneNumber))
            payment.PhoneNumber = NormalizePhone(request.PhoneNumber!);

        if (outstanding == null)
            await _context.Payments.AddAsync(payment);

        if (method == PaymentMethod.Mpesa && !manualMpesa)
        {
            if (string.IsNullOrWhiteSpace(request.PhoneNumber) || !IsValidKenyanPhone(request.PhoneNumber!))
                return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "A valid Kenyan M-PESA phone number is required.");

            var (ok, err) = await StartAttemptAsync(payment, config);
            if (!ok && err != null)
            {
                await _uow.SaveChangesAsync();
                return err;
            }
        }

        await _uow.SaveChangesAsync();

        _logger.LogInformation(
            "Payment {PaymentId} ({Reference}) created for business {BusinessId}, appointment {AppointmentId}, amount {Amount} {Currency}, method {Method}, manual={Manual}",
            payment.Id, payment.Reference, payment.BusinessId, payment.AppointmentId, payment.Amount, payment.Currency, method, manualMpesa);

        await _context.Entry(payment).Collection(p => p.Attempts).LoadAsync();
        return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));
    }

    /// <summary>
    /// Authorised staff/business confirms physical cash was received. Audited, idempotent,
    /// never backdated.
    ///
    /// <para><paramref name="amount"/> is what the operator actually counted, and is only
    /// honoured when it is smaller than what the row is for: collecting part of an
    /// outstanding balance leaves the rest as its own outstanding row, so the settled
    /// total and the remaining balance still add up to the appointment's value. A figure
    /// larger than the row is refused — the platform never records more cash than it was
    /// owed.</para>
    /// </summary>
    public async Task<ApiResponse<PaymentResponse>> ConfirmCashAsync(
        Guid paymentId, string? note, Guid userId, string role, string? idempotencyKey = null, decimal? amount = null)
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

        // A row that has not named its channel yet is an outstanding amount ("pay after
        // service"), and cash is a valid way to settle it — the act of taking the money is
        // what names the channel.
        if (payment.Method is not (PaymentMethod.Cash or PaymentMethod.Unspecified))
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "Only cash payments can be confirmed as cash.");

        // Idempotency: confirming twice returns the same success — never creates a second payment.
        if (payment.Status == PaymentStatus.Success)
            return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));

        if (payment.Status is PaymentStatus.Cancelled or PaymentStatus.Expired or PaymentStatus.Reversed or PaymentStatus.Refunded)
            return ApiResponse<PaymentResponse>.Fail("INVALID_STATUS_TRANSITION", "Payment is " + payment.Status + " and cannot be confirmed.");

        // Guardrail: no silent backdating — cash confirmation happens in the present.
        if (payment.CreatedAt > DateTime.UtcNow.Add(CashConfirmGrace))
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "Payment was created in the future; cannot confirm yet.");

        if (amount.HasValue)
        {
            var counted = decimal.Round(amount.Value, 2, MidpointRounding.AwayFromZero);
            if (counted <= 0)
                return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "The amount received must be greater than zero.");
            if (counted > payment.Amount)
                return ApiResponse<PaymentResponse>.Fail(
                    "VALIDATION_ERROR",
                    $"The amount received cannot exceed the {payment.Amount:0.00} {payment.Currency} outstanding on this payment.");

            // Part of the balance was taken. The money that arrived is this row; the money
            // still owed becomes its own outstanding row, so the appointment's balance
            // stays correct and the remainder is collected (or paid by M-PESA) later.
            if (counted < payment.Amount)
            {
                var remainder = decimal.Round(payment.Amount - counted, 2, MidpointRounding.AwayFromZero);
                await _context.Payments.AddAsync(PaymentOutstanding.Create(
                    payment.BusinessId,
                    payment.CustomerId ?? Guid.Empty,
                    payment.AppointmentId ?? Guid.Empty,
                    remainder,
                    userId,
                    DateTime.UtcNow));
                payment.Amount = counted;
            }
        }

        // Collecting the cash is what names the channel on a previously unnamed row.
        if (payment.Method == PaymentMethod.Unspecified)
        {
            payment.Method = PaymentMethod.Cash;
            payment.Provider = PaymentProviderKind.Cash;
        }

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

        // Status and ledger obligation commit together; the income row follows immediately after.
        await CommitSettlementAsync(_uow.SaveChangesAsync, payment);

        // The money arrived, so the appointment's accrued commission can be
        // released. Idempotent, and never able to fail the payment itself.
        await SettleCommissionSafelyAsync(payment.BusinessId, payment.AppointmentId, payment.Id);

        // Cash in hand is recognised income. If the books cannot be written right now, the
        // obligation committed above is what guarantees the worker finishes it.
        await RecogniseInLedgerSafelyAsync(payment);

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

        if (payment.Status is not (PaymentStatus.Created or PaymentStatus.AwaitingCustomer or PaymentStatus.Failed
            or PaymentStatus.Expired or PaymentStatus.Submitted or PaymentStatus.UnderReview))
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

        var isOwner = await _context.BusinessMemberships.AsNoTracking()
            .AnyAsync(m => m.BusinessId == payment.BusinessId &&
                           m.UserId == userId &&
                           m.Role == BusinessMembershipRole.Owner &&
                           m.Status == BusinessMembershipStatus.Active);

        if (!isOwner)
            return ApiResponse<PaymentResponse>.Fail("FORBIDDEN", "Only the business owner can reverse a payment.");

        if (string.IsNullOrWhiteSpace(reason))
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "A reversal reason is required.");

        if (!PaymentStatuses.IsSettled(payment.Status))
            return ApiResponse<PaymentResponse>.Fail("INVALID_STATUS_TRANSITION", "Only settled payments can be reversed (current: " + payment.Status + ").");

        // A manual M-PESA payment was never executed by a provider, so there is
        // nothing to call: it is recorded as a manual refund adjustment with the
        // reason, exactly as an unsupported provider reversal is.
        if (payment.Provider == PaymentProviderKind.ManualMpesa)
        {
            PaymentStateMachine.Transition(payment, PaymentStatus.Refunded);
            payment.ReversalReason = Truncate("MANUAL REFUND: " + reason, 500);

            if (payment.CustomerId.HasValue)
            {
                await _notifications.QueueAsync(new NotificationRequest(
                    "payment.reversed",
                    payment.CustomerId.Value,
                    payment.BusinessId,
                    new Dictionary<string, object?>
                    {
                        ["businessName"] = await GetBusinessNameAsync(payment.BusinessId),
                        ["amount"] = payment.Amount,
                        ["reason"] = reason,
                        ["appointmentId"] = payment.AppointmentId
                    },
                    $"pay:{payment.Id}:reversed"));
            }

            await _uow.SaveChangesAsync();
            await ReverseCommissionSafelyAsync(payment.BusinessId, payment.AppointmentId, payment.Id, reason);

            // A manual M-PESA refund is recorded in the ledger as a refund against the income row, so
            // the original receipt keeps standing and the money out is separately provable.
            await RecordRefundInLedgerSafelyAsync(payment, reason);

            _logger.LogWarning(
                "Manual M-PESA payment {PaymentId} refunded (recorded) by owner {UserId}; reason: {Reason}",
                payment.Id, userId, reason);

            return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));
        }

        var config = await GetOrCreateConfigAsync(payment.BusinessId);
        var provider = _providers.FirstOrDefault(p => p.Kind == payment.Provider);

        var result = provider == null
            ? new ProviderReversalResult { Success = false, Supported = false }
            : await provider.ReverseAsync(payment, reason, config);

        var isReversed = false;
        if (result.Success)
        {
            PaymentStateMachine.Transition(payment, PaymentStatus.Reversed);
            payment.ReversalReason = Truncate(reason, 500);
            if (!string.IsNullOrEmpty(result.TransactionId))
                payment.ExternalReference = result.TransactionId;
            isReversed = true;
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

        // Durable payment.reversed intent (Phase 3 trigger) — committed in the same
        // unit of work as the status change so the notification cannot be lost.
        if (payment.CustomerId.HasValue && isReversed)
        {
            await _notifications.QueueAsync(new NotificationRequest(
                "payment.reversed",
                payment.CustomerId.Value,
                payment.BusinessId,
                new Dictionary<string, object?>
                {
                    ["businessName"] = await GetBusinessNameAsync(payment.BusinessId),
                    ["amount"] = payment.Amount,
                    ["reason"] = reason,
                    ["appointmentId"] = payment.AppointmentId
                },
                $"pay:{payment.Id}:reversed"));
        }

        await _uow.SaveChangesAsync();

        // A refund/reversal must claw the commission back. Idempotent and
        // non-fatal: the financial record of the reversal is what matters.
        await ReverseCommissionSafelyAsync(payment.BusinessId, payment.AppointmentId, payment.Id, reason);

        // The ledger records the refund against the income row it recognises, so "money in" and
        // "money back out" stay two provable records rather than one rewritten one.
        await RecordRefundInLedgerSafelyAsync(payment, reason);

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
            return await _context.BusinessMemberships.AsNoTracking()
                .Where(m => m.UserId == userId &&
                            m.Role == BusinessMembershipRole.Owner &&
                            m.Status == BusinessMembershipStatus.Active &&
                            !m.Business.IsDeleted)
                .Select(m => (Guid?)m.BusinessId)
                .FirstOrDefaultAsync();
        }

        if (role == "Staff")
        {
            return await _context.BusinessMemberships.AsNoTracking()
                .Where(m => m.UserId == userId &&
                            m.Role == BusinessMembershipRole.Staff &&
                            m.Status == BusinessMembershipStatus.Active)
                .Select(m => (Guid?)m.BusinessId)
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
            .Include(p => p.VerificationEvents)
            .Where(p => p.AppointmentId == appointment.Id)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        var config = await GetOrCreateConfigAsync(appointment.BusinessId);
        var serviceTotal = appointment.Resources.Sum(r => r.Price);
        // Settled means the business holds the money — either a provider-confirmed
        // payment or a manual M-PESA submission the owner verified.
        var paid = payments.Where(p => PaymentStatuses.IsSettled(p.Status)).Sum(p => p.Amount);

        return new AppointmentPaymentSummary
        {
            AppointmentId = appointment.Id,
            ServiceTotal = serviceTotal,
            BookingFee = config.BookingFeeFor(serviceTotal),
            BookingFeePercentage = config.BookingFeePercentage,
            AmountPaid = paid,
            Balance = Math.Max(0, serviceTotal - paid),
            PaymentStatus = paid <= 0 ? "unpaid" : paid >= serviceTotal ? "paid" : "partially_paid",
            VerificationStatus = ResolveVerificationStatus(payments),
            Payments = payments.Select(ToResponse).ToList()
        };
    }

    /// <summary>
    /// One word for "where does this appointment stand with payment verification".
    /// Derived from the payments, so it can never disagree with them.
    /// </summary>
    private static string ResolveVerificationStatus(IReadOnlyList<Payment> payments)
    {
        if (payments.Any(p => p.Status == PaymentStatus.Verified)) return "verified";
        if (payments.Any(p => PaymentStatuses.IsAwaitingVerification(p.Status))) return "awaiting_verification";
        if (payments.Any(p => p.Status == PaymentStatus.Rejected)) return "rejected";
        if (payments.Any(p => p.Status is PaymentStatus.Created or PaymentStatus.AwaitingCustomer)) return "pending";
        return "none";
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
    private static string GenerateReference() => PaymentReferences.New();

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

    /// <summary>The wire shape of a payment, shared so a booking can describe the row it just wrote.</summary>
    internal static PaymentResponse ToResponse(Payment payment) => new()
    {
        Id = payment.Id,
        BusinessId = payment.BusinessId,
        CustomerId = payment.CustomerId,
        AppointmentId = payment.AppointmentId,
        Type = PaymentTypes.ToWire(payment.Type),
        Method = payment.Method.ToString().ToLowerInvariant(),
        Provider = payment.Provider.ToString().ToLowerInvariant(),
        // The wire dialect has to be one dialect: `awaiting_customer` and `under_review` are
        // written with an underscore everywhere else (the queue, the client's status maps), so
        // a plain ToLower() would emit `underreview` here and nowhere else.
        Status = PaymentStatuses.ToWire(payment.Status),
        Amount = payment.Amount,
        Currency = payment.Currency,
        Reference = payment.Reference,
        ExternalReference = payment.ExternalReference,
        PhoneNumber = payment.PhoneNumber,
        CreatedAt = payment.CreatedAt,
        CompletedAt = payment.CompletedAt,
        SubmittedMessage = payment.SubmittedMessage,
        SubmittedAt = payment.SubmittedAt,
        ParsedTransactionCode = payment.ParsedTransactionCode,
        ParsedAmount = payment.ParsedAmount,
        ParsedTransactionAt = payment.ParsedTransactionAt,
        ParsedRecipient = payment.ParsedRecipient,
        VerifiedAt = payment.VerifiedAt,
        VerifiedByUserId = payment.VerifiedByUserId,
        RejectedAt = payment.RejectedAt,
        RejectedByUserId = payment.RejectedByUserId,
        RejectionReason = payment.RejectionReason,
        VerificationEvents = payment.VerificationEvents
            .OrderBy(e => e.CreatedAt)
            .Select(ToVerificationEventResponse)
            .ToList(),
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

    /// <summary>Resolves the business display name for payment notification payloads.</summary>
    private async Task<string> GetBusinessNameAsync(Guid businessId)
    {
        return await _context.Businesses
            .AsNoTracking()
            .Where(b => b.Id == businessId)
            .Select(b => b.Name)
            .FirstOrDefaultAsync() ?? string.Empty;
    }
}
