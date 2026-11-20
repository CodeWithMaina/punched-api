using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Manual M-PESA: the customer pays the business's own PayBill/Till and pastes the
/// confirmation SMS, and the business owner decides whether it counts.
///
/// <para><b>Why this is separate from the Daraja path.</b> The Daraja flow has a
/// trusted witness — Safaricom's callback. This flow has none: the only evidence is
/// text the customer typed. So the design rule throughout is that <i>nothing a
/// customer submits can settle a payment</i>. A submission moves a payment to
/// <see cref="PaymentStatus.Submitted"/> and stops there; only an explicit owner
/// decision reaches <see cref="PaymentStatus.Verified"/>.</para>
///
/// <para><b>What is defended.</b> The amount compared against is always the one
/// parsed out of the message, never a client value. Receipt codes are unique at the
/// database level, so a code cannot back two payments. Every submission and decision
/// is appended to <see cref="PaymentVerificationEvent"/>, so the trail survives a
/// later approval.</para>
/// </summary>
public partial class PaymentService
{
    /// <summary>How long a submission may wait for a decision before it lapses.</summary>
    private static readonly TimeSpan VerificationWindow = TimeSpan.FromDays(3);

    /// <summary>
    /// Tolerance for the amount comparison, in KES. M-PESA states whole shillings and
    /// cents, but rounding a percentage to 2dp can leave a fraction — and rejecting a
    /// genuine payment over a fraction of a shilling is worse than accepting it.
    /// </summary>
    private const decimal AmountTolerance = 1m;

    // ═══════════════════════════════════════════════════════════
    //  PUBLIC DETAILS (storefront, anonymous)
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// What a storefront visitor is told about paying this business. Anonymous and
    /// read-only: it exposes the numbers a customer must send to, and nothing else.
    /// </summary>
    public async Task<ApiResponse<PublicPaymentDetailsResponse>> GetPublicPaymentDetailsAsync(Guid businessId)
    {
        var business = await _context.Businesses.AsNoTracking()
            .Where(b => b.Id == businessId && !b.IsDeleted)
            .Select(b => new NamedRow(b.Id, b.Name))
            .FirstOrDefaultAsync();

        if (business == null)
            return ApiResponse<PublicPaymentDetailsResponse>.Fail("BUSINESS_NOT_FOUND", "Business not found.");

        var config = await _context.BusinessPaymentConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.BusinessId == businessId);

        // A business that has never opened this screen has no row yet. Report the
        // channel as off rather than creating configuration as a side effect of an
        // anonymous read — and report the storefront payment policy as its defaults
        // (Payment Included on, Pay After Service off), which is exactly what a business
        // with no row gets when it books.
        var details = new PublicPaymentDetailsResponse
        {
            BusinessId = business.Id,
            BusinessName = business.Name,
            Currency = "KES",
            Enabled = config?.HasManualMpesaDetails == true,
            CashEnabled = config?.CashEnabled ?? true,
            BookingFeePercentage = config?.BookingFeePercentage ?? 0m,
            PayUpfrontEnabled = config?.PayUpfrontEnabled ?? true,
            PayAfterServiceEnabled = config?.PayAfterServiceEnabled ?? false
        };

        details.BookingAvailable = details.PayUpfrontEnabled || details.PayAfterServiceEnabled;
        details.OffersPaymentChoice = details.PayUpfrontEnabled && details.PayAfterServiceEnabled;

        if (config?.HasManualMpesaDetails == true)
        {
            // Only the ONE active, complete method is ever exposed — never the inactive
            // method's saved values, never a half-configured instruction.
            var method = config.ActiveManualMethod();
            details.AccountType = method;

            switch (method)
            {
                case ManualMpesaMethods.Paybill:
                    details.PaybillNumber = config.MpesaPaybillNumber;
                    details.PaybillAccount = config.MpesaPaybillAccount;
                    break;
                case ManualMpesaMethods.Till:
                    details.TillNumber = config.MpesaTillNumber;
                    break;
                case ManualMpesaMethods.SendMoney:
                    // Normalised 254… form; the client groups it for display.
                    details.PhoneNumber = config.MpesaPhoneNumber;
                    break;
            }

            details.PayeeName = config.MpesaPayeeName;
            details.Instructions = config.MpesaInstructions;
        }

        return ApiResponse<PublicPaymentDetailsResponse>.Ok(details);
    }

    // ═══════════════════════════════════════════════════════════
    //  SUBMISSION (customer)
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Record a pasted M-PESA confirmation against a payment.
    ///
    /// <para>Order of checks matters and is deliberate: identity and tenant scope
    /// first (never leak whether a payment exists), then whether this payment can
    /// still accept a submission, then the message itself. Only after all three does
    /// anything get written.</para>
    /// </summary>
    public async Task<ApiResponse<PaymentResponse>> SubmitMpesaMessageAsync(
        Guid paymentId, SubmitMpesaMessageRequest request, Guid userId, string role)
    {
        var payment = await _context.Payments
            .Include(p => p.Attempts.OrderBy(a => a.AttemptNumber))
            .Include(p => p.VerificationEvents)
            .FirstOrDefaultAsync(p => p.Id == paymentId);

        if (payment == null || !await CanAccessPaymentAsync(payment, userId, role))
            return ApiResponse<PaymentResponse>.Fail("NOT_FOUND", "Payment not found.");

        if (payment.Method is not (PaymentMethod.Mpesa or PaymentMethod.Unspecified))
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "This payment is not an M-PESA payment.");

        if (!PaymentStatuses.CanAcceptSubmission(payment.Status))
            return ApiResponse<PaymentResponse>.Fail(
                "INVALID_STATUS_TRANSITION",
                payment.Status == PaymentStatus.Verified
                    ? "This payment has already been verified."
                    : "A confirmation is already waiting to be reviewed for this payment.");

        var parsed = MpesaMessageParser.Parse(request.Message);
        if (!parsed.IsValid)
            return ApiResponse<PaymentResponse>.Fail(parsed.FailureCode!, parsed.FailureMessage!);

        // ── Duplicate transaction code ──────────────────────────
        // The same receipt may legitimately be re-submitted for the SAME payment
        // (for example after a rejection). It may never back a second payment.
        var codeOwner = await _context.Payments.AsNoTracking()
            .Where(p => p.ParsedTransactionCode == parsed.TransactionCode && p.Id != payment.Id)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync();

        if (codeOwner != null)
            return ApiResponse<PaymentResponse>.Fail(
                "MPESA_CODE_ALREADY_USED",
                "That M-PESA transaction code has already been submitted. Each payment must have its own code.");

        // ── Amount comparison ───────────────────────────────────
        // The expected amount is the payment's own server-computed figure; the
        // submitted one comes from the message. A mismatch is refused here rather than
        // queued, so the customer is told immediately instead of waiting for a
        // rejection — but the message itself is never the reason a payment settles.
        var submitted = parsed.Amount!.Value;
        if (Math.Abs(submitted - payment.Amount) > AmountTolerance)
            return ApiResponse<PaymentResponse>.Fail(
                "MPESA_AMOUNT_MISMATCH",
                $"That message shows KES {submitted:N2}, but KES {payment.Amount:N2} is due for this booking. " +
                "Check you paid the right amount, or paste the confirmation for the correct payment.");

        payment.SubmittedMessage = Truncate(request.Message.Trim(), 1000);
        payment.SubmittedAt = DateTime.UtcNow;
        // A pasted confirmation IS the manual M-PESA channel, so a row that had not named
        // its channel yet (an outstanding "pay after service" amount) is named by this
        // submission — the money arrived by M-PESA, and the record now says so.
        if (payment.Method == PaymentMethod.Unspecified)
        {
            payment.Method = PaymentMethod.Mpesa;
            payment.Provider = PaymentProviderKind.ManualMpesa;
        }
        payment.ParsedTransactionCode = parsed.TransactionCode;
        payment.ParsedAmount = submitted;
        payment.ParsedTransactionAt = parsed.TransactionAtUtc;
        payment.ParsedRecipient = parsed.Recipient;
        // A fresh submission clears the previous decision so the queue shows it as
        // undecided rather than carrying a stale rejection forward.
        payment.RejectionReason = null;
        payment.RejectedAt = null;
        payment.RejectedByUserId = null;

        PaymentStateMachine.Transition(payment, PaymentStatus.Submitted);

        await AddVerificationEventAsync(
            payment,
            kind: "submitted",
            actorUserId: userId,
            amount: submitted,
            transactionCode: parsed.TransactionCode,
            message: payment.SubmittedMessage,
            reason: parsed.Payer is null ? null : Truncate($"Payer: {parsed.Payer}", 500));

        await _uow.SaveChangesAsync();

        await NotifyOwnerOfSubmissionAsync(payment, parsed);

        _logger.LogInformation(
            "Manual M-PESA submission accepted for payment {PaymentId} (business {BusinessId}): code {Code}, amount {Amount}",
            payment.Id, payment.BusinessId, parsed.TransactionCode, submitted);

        return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));
    }

    // ═══════════════════════════════════════════════════════════
    //  OWNER QUEUE & HISTORY
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// The owner's payment-verification queue and history, tenant-scoped.
    ///
    /// <para><paramref name="query"/>.Status selects the lens: <c>awaiting</c> (the
    /// default — Submitted, UnderReview and Flagged), a single status, <c>attention</c>
    /// (flagged + under review) or <c>all</c>. The counters are always computed over the
    /// whole manual-M-PESA population, not the filtered page, so the header never changes
    /// meaning when a filter is applied.</para>
    /// </summary>
    public async Task<ApiResponse<PaymentVerificationQueueResponse>> GetVerificationQueueAsync(
        PaymentVerificationQuery query, Guid userId, string role)
    {
        var scope = await ResolveQueueScopeAsync(userId, role);
        if (scope.ErrorCode != null)
            return ApiResponse<PaymentVerificationQueueResponse>.Fail(scope.ErrorCode, scope.ErrorMessage!);

        var businessId = scope.BusinessId;

        // Only manual M-PESA payments have a verification lifecycle at all.
        var manual = _context.Payments.AsNoTracking()
            .Where(p => p.Provider == PaymentProviderKind.ManualMpesa)
            .Where(p => businessId == null || p.BusinessId == businessId);

        var status = query.Status?.Trim().ToLowerInvariant();
        var awaiting = manual.Where(p =>
            p.Status == PaymentStatus.Submitted ||
            p.Status == PaymentStatus.UnderReview ||
            p.Status == PaymentStatus.Flagged);

        var scoped = status switch
        {
            null or "" or "awaiting" or "pending_verification" => awaiting,
            "all" => manual,
            "submitted" => manual.Where(p => p.Status == PaymentStatus.Submitted),
            "under_review" => manual.Where(p => p.Status == PaymentStatus.UnderReview),
            "flagged" => manual.Where(p => p.Status == PaymentStatus.Flagged),
            "attention" or "requires_attention" => manual.Where(p =>
                p.Status == PaymentStatus.Flagged || p.Status == PaymentStatus.UnderReview),
            "verified" => manual.Where(p => p.Status == PaymentStatus.Verified),
            "rejected" => manual.Where(p => p.Status == PaymentStatus.Rejected),
            _ => awaiting
        };

        // Submission window — when the customer pasted the message. Falls back to
        // creation so a row that somehow never stamped SubmittedAt is still filterable.
        if (query.From.HasValue) scoped = scoped.Where(p => (p.SubmittedAt ?? p.CreatedAt) >= query.From.Value);
        if (query.To.HasValue) scoped = scoped.Where(p => (p.SubmittedAt ?? p.CreatedAt) <= query.To.Value);

        // Transaction window — the date the message itself stated.
        if (query.TransactionFrom.HasValue)
            scoped = scoped.Where(p => p.ParsedTransactionAt != null && p.ParsedTransactionAt >= query.TransactionFrom.Value);
        if (query.TransactionTo.HasValue)
            scoped = scoped.Where(p => p.ParsedTransactionAt != null && p.ParsedTransactionAt <= query.TransactionTo.Value);

        // Amount window — what the message claims was paid (falling back to what is owed).
        if (query.MinAmount.HasValue) scoped = scoped.Where(p => (p.ParsedAmount ?? p.Amount) >= query.MinAmount.Value);
        if (query.MaxAmount.HasValue) scoped = scoped.Where(p => (p.ParsedAmount ?? p.Amount) <= query.MaxAmount.Value);

        var search = query.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            // Resolve matching customer names/phones to ids first: the payment row carries
            // no name, and filtering after the fact would require loading every row.
            var matchingCustomers = await _context.Users.AsNoTracking()
                .Where(u => u.FullName.Contains(search) ||
                            (u.PhoneNumber != null && u.PhoneNumber.Contains(search)))
                .Select(u => u.Id)
                .Take(200)
                .ToListAsync();

            scoped = scoped.Where(p =>
                p.Reference.Contains(search) ||
                (p.ParsedTransactionCode != null && p.ParsedTransactionCode.Contains(search)) ||
                (p.PhoneNumber != null && p.PhoneNumber.Contains(search)) ||
                (p.CustomerId != null && matchingCustomers.Contains(p.CustomerId.Value)));
        }


        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var total = await scoped.CountAsync();
        var rows = await scoped
            .OrderByDescending(p => p.SubmittedAt ?? p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(p => p.VerificationEvents)
            .Include(p => p.Attempts)
            .ToListAsync();

        // The awaiting total is summed in memory from a single-column projection rather
        // than in SQL: `decimal` has no native aggregate on every provider the platform
        // runs on (SQLite in tests), and a per-row SUM that only works in production is
        // a bug that hides until it matters. The projection keeps it to one narrow
        // query, and the awaiting set is the queue itself — dozens, not millions.
        var awaitingAmounts = await awaiting.Select(p => p.Amount).ToListAsync();

        return ApiResponse<PaymentVerificationQueueResponse>.Ok(new PaymentVerificationQueueResponse
        {
            Items = await BuildVerificationItemsAsync(rows),
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            TotalPages = pageSize <= 0 ? 1 : (int)Math.Ceiling(total / (double)pageSize),
            AwaitingCount = awaitingAmounts.Count,
            AwaitingAmount = awaitingAmounts.Sum(),
            SubmittedCount = await manual.CountAsync(p => p.Status == PaymentStatus.Submitted),
            FlaggedCount = await manual.CountAsync(p => p.Status == PaymentStatus.Flagged),
            UnderReviewCount = await manual.CountAsync(p => p.Status == PaymentStatus.UnderReview),
            VerifiedCount = await manual.CountAsync(p => p.Status == PaymentStatus.Verified),
            RejectedCount = await manual.CountAsync(p => p.Status == PaymentStatus.Rejected)
        });
    }

    /// <summary>
    /// One claim in full, tenant-scoped, for the reconciliation workspace's detail panel.
    /// Shaped by the exact same builder as the queue, so the detail can never disagree
    /// with the row it was opened from.
    /// </summary>
    public async Task<ApiResponse<PaymentVerificationItemResponse>> GetVerificationItemAsync(
        Guid paymentId, Guid userId, string role)
    {
        var scope = await ResolveQueueScopeAsync(userId, role);
        if (scope.ErrorCode != null)
            return ApiResponse<PaymentVerificationItemResponse>.Fail(scope.ErrorCode, scope.ErrorMessage!);

        var payment = await _context.Payments.AsNoTracking()
            .Where(p => p.Provider == PaymentProviderKind.ManualMpesa)
            .Where(p => scope.BusinessId == null || p.BusinessId == scope.BusinessId)
            .Include(p => p.VerificationEvents)
            .Include(p => p.Attempts)
            .FirstOrDefaultAsync(p => p.Id == paymentId);

        if (payment == null)
            return ApiResponse<PaymentVerificationItemResponse>.Fail("NOT_FOUND", "Claim not found.");

        var items = await BuildVerificationItemsAsync(new List<Payment> { payment });
        return ApiResponse<PaymentVerificationItemResponse>.Ok(items[0]);
    }

    /// <summary>
    /// Resolve the business a queue/claim read is scoped to, enforcing the roles that may
    /// see payment evidence at all. Admin reads across tenants; everyone else is pinned to
    /// the business on their active membership — another tenant's claims simply are not
    /// there to find.
    /// </summary>
    private async Task<(Guid? BusinessId, string? ErrorCode, string? ErrorMessage)> ResolveQueueScopeAsync(
        Guid userId, string role)
    {
        if (role != "Business" && role != "Staff" && role != "Admin")
            return (null, "FORBIDDEN", "Only business users can review payments.");

        if (role == "Admin") return (null, null, null);

        var businessId = await ResolveBusinessIdAsync(userId, role);
        if (businessId == null)
            return (null, "FORBIDDEN", "No business scope for user.");

        return (businessId, null, null);
    }

    // ═══════════════════════════════════════════════════════════
    //  DECISIONS (owner)
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// The owner has opened a submission. A non-terminal marker so the queue can show
    /// "being looked at" without a second table; repeating it is a no-op.
    /// </summary>
    public async Task<ApiResponse<PaymentResponse>> MarkVerificationUnderReviewAsync(
        Guid paymentId, Guid userId, string role)
    {
        var loaded = await LoadForDecisionAsync(paymentId, userId, role);
        if (loaded.Error != null) return loaded.Error;

        var payment = loaded.Payment!;
        if (payment.Status == PaymentStatus.UnderReview)
            return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));

        // Submitted starts a review; Flagged is cleared back to one (the owner decided the
        // doubt is resolved enough to look at it normally).
        if (payment.Status != PaymentStatus.Submitted && payment.Status != PaymentStatus.Flagged)
            return ApiResponse<PaymentResponse>.Fail(
                "INVALID_STATUS_TRANSITION", "Only a submitted or flagged payment can be marked under review.");

        PaymentStateMachine.Transition(payment, PaymentStatus.UnderReview);
        await AddVerificationEventAsync(payment, "under_review", userId);
        await _uow.SaveChangesAsync();

        return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));
    }

    /// <summary>
    /// Set a submission aside for a second look. Flagging is deliberately NOT a decision
    /// about the money: the claim stays unverified and stays in the awaiting queue (so it
    /// cannot be quietly dropped), a decision can still be taken straight from the flagged
    /// state, and re-flagging updates the note instead of stacking a second verdict. The
    /// note is internal — it is never shown to the customer.
    /// </summary>
    public async Task<ApiResponse<PaymentResponse>> FlagPaymentAsync(
        Guid paymentId, FlagPaymentRequest request, Guid userId, string role)
    {
        var loaded = await LoadForDecisionAsync(paymentId, userId, role);
        if (loaded.Error != null) return loaded.Error;

        var payment = loaded.Payment!;

        if (payment.Status == PaymentStatus.Verified || payment.Status == PaymentStatus.Rejected)
            return ApiResponse<PaymentResponse>.Fail(
                "INVALID_STATUS_TRANSITION",
                "A claim that has already been decided cannot be flagged for review.");

        if (!PaymentStatuses.IsAwaitingVerification(payment.Status))
            return ApiResponse<PaymentResponse>.Fail(
                "INVALID_STATUS_TRANSITION",
                "Only a submission awaiting a decision can be flagged (current: " + payment.Status + ").");

        var note = string.IsNullOrWhiteSpace(request.Note)
            ? "Flagged for further investigation."
            : Truncate(request.Note!.Trim(), 500);

        // Only transition on the first flag; re-flagging keeps the status and appends the
        // new note, so the trail shows every reason it was looked at.
        if (payment.Status != PaymentStatus.Flagged)
            PaymentStateMachine.Transition(payment, PaymentStatus.Flagged);

        await AddVerificationEventAsync(payment, "flagged", userId, reason: note);
        await _uow.SaveChangesAsync();

        _logger.LogInformation(
            "Manual M-PESA payment {PaymentId} flagged for review by user {UserId} (business {BusinessId}).",
            payment.Id, userId, payment.BusinessId);

        return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));
    }

    /// <summary>
    /// Refuse a submitted payment. The reason is mandatory and is shown to the
    /// customer, so it has to say what to do next rather than just "no".
    /// </summary>
    public async Task<ApiResponse<PaymentResponse>> RejectPaymentAsync(
        Guid paymentId, RejectPaymentRequest request, Guid userId, string role)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return ApiResponse<PaymentResponse>.Fail("VALIDATION_ERROR", "A rejection reason is required.");

        // A rejection and an approval racing on the same claim are serialised the same way: whoever
        // gets the lock first decides, and the other sees the decided status.
        await using var decision = _decisionGuard == null
            ? null
            : await _decisionGuard.BeginAsync(paymentId);

        var loaded = await LoadForDecisionAsync(paymentId, userId, role);
        if (loaded.Error != null) return loaded.Error;

        var payment = loaded.Payment!;
        var reason = Truncate(request.Reason.Trim(), 500)!;

        if (payment.Status == PaymentStatus.Rejected && payment.RejectionReason == reason)
            return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));

        if (!PaymentStatuses.IsAwaitingVerification(payment.Status))
            return ApiResponse<PaymentResponse>.Fail(
                "INVALID_STATUS_TRANSITION",
                "Only a submitted payment can be rejected (current: " + payment.Status + ").");

        PaymentStateMachine.Transition(payment, PaymentStatus.Rejected);
        payment.RejectedByUserId = userId;
        payment.RejectionReason = reason;

        await AddVerificationEventAsync(
            payment, "rejected", userId, amount: payment.ParsedAmount,
            transactionCode: payment.ParsedTransactionCode, reason: reason);

        // The appointment stays in pending_payment: the slot is still held so the
        // customer can pay again and re-submit without losing their time. The
        // payment-expiry sweep cancels it if nobody acts.
        await NotifyCustomerOfDecisionAsync(payment, verified: false, reason: reason);

        await _uow.SaveChangesAsync();

        // The rejection is durable; release the decision lock.
        if (decision != null) await decision.CommitAsync();

        _logger.LogInformation(
            "Manual M-PESA payment {PaymentId} REJECTED by user {UserId} (business {BusinessId}); reason: {Reason}",
            payment.Id, userId, payment.BusinessId, reason);

        return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));
    }

    /// <summary>
    /// Confirm the money arrived. This is the only path that turns a pasted message
    /// into a settled payment.
    ///
    /// <para>Idempotent: verifying an already-verified payment returns the same
    /// success without a second history row, a second appointment transition or a
    /// second commission release. Two concurrent approvals cannot both win — the
    /// status check makes the loser see a refusal instead of double-settling.</para>
    /// </summary>
    public async Task<ApiResponse<PaymentResponse>> VerifyPaymentAsync(
        Guid paymentId, VerifyPaymentRequest request, Guid userId, string role)
    {
        // Serialise concurrent decisions on this payment. The lock is released by CommitAsync once the
        // settlement and its obligation are committed; every early return rolls it back.
        await using var decision = _decisionGuard == null
            ? null
            : await _decisionGuard.BeginAsync(paymentId);

        var loaded = await LoadForDecisionAsync(paymentId, userId, role);
        if (loaded.Error != null) return loaded.Error;

        var payment = loaded.Payment!;

        // Already decided: same answer, no side effects repeated.
        if (payment.Status == PaymentStatus.Verified)
            return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));

        if (payment.Status == PaymentStatus.Rejected)
            return ApiResponse<PaymentResponse>.Fail(
                "INVALID_STATUS_TRANSITION",
                "This submission was rejected. The customer must submit a new confirmation before it can be verified.");

        if (!PaymentStatuses.IsAwaitingVerification(payment.Status))
            return ApiResponse<PaymentResponse>.Fail(
                "INVALID_STATUS_TRANSITION",
                "Only a submitted payment can be verified (current: " + payment.Status + ").");

        // What the owner stated when confirming. A supplied received amount may not exceed
        // what this payment is for — over-collection here would be a typo, not a bigger
        // payment, and the ledger must never be told a figure the claim cannot support.
        if (request.ReceivedAmount is decimal receivedAmount)
        {
            if (receivedAmount <= 0)
                return ApiResponse<PaymentResponse>.Fail(
                    "VALIDATION_ERROR", "The received amount must be greater than zero.");

            if (receivedAmount > payment.Amount + AmountTolerance)
                return ApiResponse<PaymentResponse>.Fail(
                    "VALIDATION_ERROR",
                    $"The received amount cannot exceed the {payment.Amount:0.00} {payment.Currency} this payment is for.");
        }

        // Defence in depth: the same receipt must not settle two payments even if it
        // somehow reached two rows (the unique index should make this impossible).
        if (!string.IsNullOrEmpty(payment.ParsedTransactionCode))
        {
            var twinVerified = await _context.Payments.AsNoTracking().AnyAsync(p =>
                p.Id != payment.Id &&
                p.ParsedTransactionCode == payment.ParsedTransactionCode &&
                p.Status == PaymentStatus.Verified);

            if (twinVerified)
                return ApiResponse<PaymentResponse>.Fail(
                    "MPESA_CODE_ALREADY_USED",
                    "That M-PESA transaction code has already been verified against another payment.");
        }

        PaymentStateMachine.Transition(payment, PaymentStatus.Verified);
        payment.VerifiedByUserId = userId;

        await AddVerificationEventAsync(
            payment, "verified", userId, amount: payment.ParsedAmount,
            transactionCode: string.IsNullOrWhiteSpace(request.TransactionReference)
                ? payment.ParsedTransactionCode
                : Truncate(request.TransactionReference!.Trim(), 20),
            reason: BuildVerificationNote(request));

        // The appointment moves with the payment, inside the same unit of work: a
        // verified payment that left its appointment stuck in pending_payment would
        // be a worse failure than not verifying at all.
        var appointmentStatus = await AdvanceAppointmentAfterVerificationAsync(payment, userId);

        // The verification, its history event, the appointment transition and the ledger obligation
        // all commit together. A crash immediately after this cannot lose the income: the obligation
        // is in the same committed row set.
        await CommitSettlementAsync(_uow.SaveChangesAsync, payment);

        // Settlement and its ledger obligation are now durable: release the decision lock before the
        // (idempotent, non-transactional) commission release and ledger attempt.
        if (decision != null) await decision.CommitAsync();

        // Settled money releases commission exactly as a provider-confirmed payment
        // does. Idempotent and non-fatal.
        await SettleCommissionSafelyAsync(payment.BusinessId, payment.AppointmentId, payment.Id);

        // An owner's verification is a person confirming the money arrived, so the ledger recognises
        // it as verified income — one row, however many times the decision is replayed.
        await RecogniseInLedgerSafelyAsync(payment);

        await NotifyCustomerOfDecisionAsync(payment, verified: true, reason: null);

        _logger.LogInformation(
            "Manual M-PESA payment {PaymentId} VERIFIED by user {UserId} (business {BusinessId}); appointment {AppointmentId} → {Status}",
            payment.Id, userId, payment.BusinessId, payment.AppointmentId, appointmentStatus ?? "(unchanged)");

        return ApiResponse<PaymentResponse>.Ok(ToResponse(payment));
    }

    // ═══════════════════════════════════════════════════════════
    //  INTERNAL HELPERS
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Load a payment for an owner/staff decision, enforcing tenant scope and the
    /// permission that governs the money.
    ///
    /// <para>The tenant check is deliberately the same one every other payment read
    /// uses (<see cref="CanAccessPaymentAsync"/>), so a business can only ever decide
    /// on its own payments — the queue filters by business id and this re-checks the
    /// individual row, which is what stops a hand-crafted id from crossing tenants.</para>
    /// </summary>
    private async Task<(Payment? Payment, ApiResponse<PaymentResponse>? Error)> LoadForDecisionAsync(
        Guid paymentId, Guid userId, string role)
    {
        var payment = await _context.Payments
            .Include(p => p.Attempts.OrderBy(a => a.AttemptNumber))
            .Include(p => p.VerificationEvents)
            .FirstOrDefaultAsync(p => p.Id == paymentId);

        if (payment == null || !await CanAccessPaymentAsync(payment, userId, role))
            return (null, ApiResponse<PaymentResponse>.Fail("NOT_FOUND", "Payment not found."));

        if (role is not ("Business" or "Staff" or "Admin"))
            return (null, ApiResponse<PaymentResponse>.Fail(
                "FORBIDDEN", "Only the business can decide on a submitted payment."));

        if (role == "Staff" && !_permissions.HasPermission(role, "payments.confirm_cash"))
            return (null, ApiResponse<PaymentResponse>.Fail(
                "FORBIDDEN", "You are not allowed to verify payments."));

        return (payment, null);
    }

    /// <summary>
    /// Append one row to the payment's verification trail. Never updates an existing
    /// row, so the trail is a true log of what happened in the order it happened.
    /// </summary>
    private async Task AddVerificationEventAsync(
        Payment payment,
        string kind,
        Guid? actorUserId,
        decimal? amount = null,
        string? transactionCode = null,
        string? message = null,
        string? reason = null)
    {
        var actorName = actorUserId.HasValue
            ? await _context.Users.AsNoTracking()
                .Where(u => u.Id == actorUserId.Value)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync()
            : null;

        var entry = new PaymentVerificationEvent
        {
            Id = Guid.NewGuid(),
            BusinessId = payment.BusinessId,
            PaymentId = payment.Id,
            Kind = kind,
            ActorUserId = actorUserId,
            ActorName = Truncate(actorName, 100),
            Amount = amount,
            TransactionCode = Truncate(transactionCode, 20),
            Message = Truncate(message, 1000),
            Reason = Truncate(reason, 500),
            CreatedAt = DateTime.UtcNow
        };

        // Added through the DbSet so EF marks it Added (an entity reached only through
        // a navigation with an app-assigned key would be tracked as Modified and issue
        // an UPDATE against a row that was never inserted).
        await _context.PaymentVerificationEvents.AddAsync(entry);
        payment.VerificationEvents.Add(entry);
    }

    /// <summary>Shape one verification-event row for the API.</summary>
    private static PaymentVerificationEventResponse ToVerificationEventResponse(PaymentVerificationEvent entry) => new()
    {
        Id = entry.Id,
        Kind = entry.Kind,
        ActorUserId = entry.ActorUserId,
        ActorName = entry.ActorName,
        Amount = entry.Amount,
        TransactionCode = entry.TransactionCode,
        Message = entry.Message,
        Reason = entry.Reason,
        CreatedAt = entry.CreatedAt
    };

    /// <summary>
    /// Fold the owner's confirmations and any corrected figures into the single audit line
    /// the trail stores, so exactly what was asserted at the moment of deciding is
    /// preserved — even though only one string fits on the event row.
    /// </summary>
    private static string? BuildVerificationNote(VerifyPaymentRequest request)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.Note)) parts.Add(request.Note!.Trim());

        var confirmations = new List<string>(4);
        if (request.TransactionExists == true) confirmations.Add("transaction seen on statement");
        if (request.AmountMatches == true) confirmations.Add("amount matches");
        if (request.CodeMatches == true) confirmations.Add("code matches");
        if (request.BelongsToBusiness == true) confirmations.Add("paid into this business");
        if (confirmations.Count > 0) parts.Add("Confirmed: " + string.Join("; ", confirmations) + ".");

        if (request.ReceivedAmount is decimal received) parts.Add($"Received {received:0.00}.");
        if (!string.IsNullOrWhiteSpace(request.TransactionReference))
            parts.Add($"Reference {request.TransactionReference!.Trim()}.");

        return parts.Count == 0 ? null : Truncate(string.Join(" ", parts), 500);
    }

    /// <summary>
    /// Move the appointment that a just-verified payment belongs to.
    ///
    /// <para>Returns the new status, or null when nothing needed to change. A
    /// <c>pending_payment</c> appointment becomes <c>confirmed</c>: the money that was
    /// holding it back has arrived. A <c>booked</c> appointment is confirmed too,
    /// because a verified booking fee is a stronger signal than the owner tapping
    /// confirm. Anything already past that point (confirmed, completed, cancelled) is
    /// left exactly as it is — verification must never resurrect a cancelled booking
    /// or re-open a finished one.</para>
    /// </summary>
    private async Task<string?> AdvanceAppointmentAfterVerificationAsync(Payment payment, Guid actorUserId)
    {
        if (payment.AppointmentId is not Guid appointmentId) return null;

        var appointment = await _context.Appointments
            .FirstOrDefaultAsync(a => a.Id == appointmentId);

        if (appointment == null) return null;

        if (appointment.Status is not (AppointmentStatuses.PendingPayment or AppointmentStatuses.Booked))
            return null;

        var from = appointment.Status;
        appointment.Status = AppointmentStatuses.Confirmed;
        appointment.Version++;

        await _context.AppointmentStatusHistory.AddAsync(new AppointmentStatusHistory
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            Status = AppointmentStatuses.Confirmed,
            ChangedAt = DateTime.UtcNow,
            ChangedByUserId = actorUserId,
            Note = $"Booking fee verified ({payment.Reference}).",
            CreatedAt = DateTime.UtcNow
        });

        _logger.LogInformation(
            "Appointment {AppointmentId} moved {From} → confirmed because payment {PaymentId} was verified.",
            appointment.Id, from, payment.Id);

        return AppointmentStatuses.Confirmed;
    }

    /// <summary>
    /// Tell the owner a confirmation is waiting. Queued inside the caller's unit of
    /// work where possible so the alert cannot exist without the submission; a
    /// notification fault is logged and never fails the submission itself.
    /// </summary>
    private async Task NotifyOwnerOfSubmissionAsync(Payment payment, MpesaMessageParseResult parsed)
    {
        var ownerId = await _context.BusinessMemberships.AsNoTracking()
            .Where(m => m.BusinessId == payment.BusinessId &&
                        m.Role == BusinessMembershipRole.Owner &&
                        m.Status == BusinessMembershipStatus.Active)
            .Select(m => (Guid?)m.UserId)
            .FirstOrDefaultAsync();

        if (ownerId == null) return;

        var customerName = payment.CustomerId.HasValue
            ? await _context.Users.AsNoTracking()
                .Where(u => u.Id == payment.CustomerId.Value)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync()
            : null;

        try
        {
            await _notifications.QueueAsync(new NotificationRequest(
                "payment.submitted",
                ownerId.Value,
                payment.BusinessId,
                new Dictionary<string, object?>
                {
                    ["businessName"] = await GetBusinessNameAsync(payment.BusinessId),
                    ["amount"] = parsed.Amount,
                    ["customerName"] = customerName ?? parsed.Payer,
                    ["reference"] = payment.Reference,
                    ["transactionCode"] = parsed.TransactionCode
                },
                $"pay:{payment.Id}:submitted:{parsed.TransactionCode}"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "payment.submitted notification could not be queued for payment {PaymentId}; the submission stands.",
                payment.Id);
        }
    }

    /// <summary>Tell the customer what the owner decided, including why on a rejection.</summary>
    private async Task NotifyCustomerOfDecisionAsync(Payment payment, bool verified, string? reason)
    {
        if (!payment.CustomerId.HasValue) return;

        var type = verified ? "payment.verified" : "payment.rejected";

        try
        {
            await _notifications.QueueAsync(new NotificationRequest(
                type,
                payment.CustomerId.Value,
                payment.BusinessId,
                new Dictionary<string, object?>
                {
                    ["businessName"] = await GetBusinessNameAsync(payment.BusinessId),
                    ["amount"] = payment.ParsedAmount ?? payment.Amount,
                    ["reason"] = reason,
                    ["appointmentId"] = payment.AppointmentId
                },
                $"pay:{payment.Id}:{type}"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Type} notification could not be queued for payment {PaymentId}; the decision stands.",
                type, payment.Id);
        }
    }

    /// <summary>
    /// Hydrate queue rows with the appointment, customer and service context the owner
    /// needs to make a decision — in a bounded number of queries, not one per row.
    /// </summary>
    private async Task<List<PaymentVerificationItemResponse>> BuildVerificationItemsAsync(List<Payment> rows)
    {
        if (rows.Count == 0) return new List<PaymentVerificationItemResponse>();

        var appointmentIds = rows.Where(p => p.AppointmentId.HasValue)
            .Select(p => p.AppointmentId!.Value).Distinct().ToList();
        var customerIds = rows.Where(p => p.CustomerId.HasValue)
            .Select(p => p.CustomerId!.Value).Distinct().ToList();
        var codes = rows.Where(p => !string.IsNullOrEmpty(p.ParsedTransactionCode))
            .Select(p => p.ParsedTransactionCode!).Distinct().ToList();

        var appointments = await _context.Appointments.AsNoTracking()
            .Where(a => appointmentIds.Contains(a.Id))
            .Select(a => new AppointmentContextRow(a.Id, a.Status, a.ScheduledAt, a.StaffUserId))
            .ToDictionaryAsync(a => a.Id);

        var customers = await _context.Users.AsNoTracking()
            .Where(u => customerIds.Contains(u.Id))
            .Select(u => new UserContactRow(u.Id, u.FullName, u.PhoneNumber, u.Email))
            .ToDictionaryAsync(u => u.Id);

        var services = await _context.AppointmentResources.AsNoTracking()
            .Where(r => appointmentIds.Contains(r.AppointmentId))
            .OrderBy(r => r.SortOrder)
            .Select(r => new AppointmentServiceLineRow(r.AppointmentId, r.Name, r.Price))
            .ToListAsync();

        // What the business has actually collected per appointment, so a booking-fee claim can
        // show the balance that will still stand against the booking once it settles. Grouped in
        // memory on purpose: `decimal` has no native SUM on every provider the platform runs on
        // (SQLite in the test suite), and an aggregate that only works in production is a bug
        // that hides until it matters. The projection keeps it to one narrow query.
        var collectedRows = await _context.Payments.AsNoTracking()
            .Where(p => p.AppointmentId != null && appointmentIds.Contains(p.AppointmentId.Value))
            .Where(p => p.Status == PaymentStatus.Success || p.Status == PaymentStatus.Verified)
            .Select(p => new AppointmentAmountRow(p.AppointmentId!.Value, p.Amount))
            .ToListAsync();

        var collectedByAppointment = collectedRows
            .GroupBy(row => row.AppointmentId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Amount));

        // The professional the booking is assigned to, when it is.
        var staffIds = appointments.Values.Where(a => a.StaffUserId.HasValue)
            .Select(a => a.StaffUserId!.Value).Distinct().ToList();
        var staffNames = await _context.Users.AsNoTracking()
            .Where(u => staffIds.Contains(u.Id))
            .Select(u => new UserIdNameRow(u.Id, u.FullName))
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        // Any OTHER payment holding the same receipt is a reuse. The unique index
        // prevents the code being stored twice, but a row that was since rejected still
        // tells the owner this code has been seen before.
        var codeOwners = await _context.Payments.AsNoTracking()
            .Where(p => p.ParsedTransactionCode != null && codes.Contains(p.ParsedTransactionCode))
            .Select(p => new PaymentReceiptOwnerRow(p.Id, p.ParsedTransactionCode))
            .ToListAsync();

        // The ledger row each of these claims became, when it has been verified. One query for the
        // whole page (never one per claim), so the workspace can link straight to the receipt the
        // claim turned into instead of sending the owner hunting through the ledger.
        var paymentIds = rows.Select(p => p.Id).ToList();

        var linkedRows = await _context.FinancialTransactions.AsNoTracking()
            .Where(t => t.SourcePaymentId != null
                        && paymentIds.Contains(t.SourcePaymentId!.Value)
                        && !t.IsVoided)
            .Select(t => new LedgerRowRef(t.SourcePaymentId!.Value, t.Id))
            .ToListAsync();

        var transactionByPayment = linkedRows
            .GroupBy(t => t.SourcePaymentId)
            .ToDictionary(g => g.Key, g => g.First().Id);

        var items = new List<PaymentVerificationItemResponse>(rows.Count);

        foreach (var payment in rows)
        {
            var appointment = payment.AppointmentId.HasValue &&
                              appointments.TryGetValue(payment.AppointmentId.Value, out var found)
                ? found
                : null;

            var customer = payment.CustomerId.HasValue &&
                           customers.TryGetValue(payment.CustomerId.Value, out var person)
                ? person
                : null;

            // The booking's value and the balance that will remain once this claim
            // settles. Null when the claim is not attached to an appointment.
            decimal? bookingAmount = null;
            decimal? outstandingAmount = null;
            string? assignedStaffName = null;

            if (payment.AppointmentId.HasValue && appointment != null)
            {
                var bookingTotal = services
                    .Where(s => s.AppointmentId == payment.AppointmentId.Value)
                    .Sum(s => s.Price);

                var collected = collectedByAppointment.TryGetValue(payment.AppointmentId.Value, out var paid)
                    ? paid
                    : 0m;

                bookingAmount = bookingTotal;
                outstandingAmount = Math.Max(0m, bookingTotal - collected);

                if (appointment.StaffUserId.HasValue &&
                    staffNames.TryGetValue(appointment.StaffUserId.Value, out var staffName))
                    assignedStaffName = staffName;
            }

            // The most recent flag note, when the claim is sitting in the flagged lane.
            var lastFlag = payment.VerificationEvents
                .Where(e => e.Kind == "flagged")
                .OrderByDescending(e => e.CreatedAt)
                .FirstOrDefault();

            items.Add(new PaymentVerificationItemResponse
            {
                PaymentId = payment.Id,
                AppointmentId = payment.AppointmentId,
                AppointmentStatus = appointment?.Status,
                ScheduledAt = appointment?.ScheduledAt,
                CustomerId = payment.CustomerId,
                CustomerName = customer?.FullName,
                CustomerPhone = customer?.PhoneNumber,
                CustomerEmail = customer?.Email,
                // A guest checkout has no customer account; a registered customer does.
                IsRegisteredCustomer = payment.CustomerId.HasValue,
                AssignedStaffName = assignedStaffName,
                ServiceNames = payment.AppointmentId.HasValue
                    ? services.Where(s => s.AppointmentId == payment.AppointmentId.Value)
                        .Select(s => s.Name).ToList()
                    : new List<string>(),
                ExpectedAmount = payment.Amount,
                BookingAmount = bookingAmount,
                OutstandingAmount = outstandingAmount,
                SubmittedAmount = payment.ParsedAmount,
                Currency = payment.Currency,
                Status = PaymentStatuses.ToWire(payment.Status),
                Reference = payment.Reference,
                TransactionCode = payment.ParsedTransactionCode,
                TransactionAt = payment.ParsedTransactionAt,
                Recipient = payment.ParsedRecipient,
                SubmittedMessage = payment.SubmittedMessage,
                SubmittedAt = payment.SubmittedAt,
                // The payer is not a column: it is captured on the submission event, which
                // is where the parser's reading of "from …" was recorded.
                Payer = payment.VerificationEvents
                    .Where(e => e.Kind == "submitted" && e.Reason != null && e.Reason.StartsWith("Payer: "))
                    .OrderByDescending(e => e.CreatedAt)
                    .Select(e => e.Reason!["Payer: ".Length..])
                    .FirstOrDefault(),
                AmountMismatch = payment.ParsedAmount.HasValue &&
                                 Math.Abs(payment.ParsedAmount.Value - payment.Amount) > AmountTolerance,
                DuplicateOfPaymentId = payment.ParsedTransactionCode == null
                    ? null
                    : codeOwners
                        .Where(o => o.Id != payment.Id && o.ParsedTransactionCode == payment.ParsedTransactionCode)
                        .Select(o => (Guid?)o.Id)
                        .FirstOrDefault(),
                FlagNote = payment.Status == PaymentStatus.Flagged ? lastFlag?.Reason : null,
                FlaggedAt = payment.Status == PaymentStatus.Flagged ? lastFlag?.CreatedAt : null,
                VerifiedAt = payment.VerifiedAt,
                RejectedAt = payment.RejectedAt,
                RejectionReason = payment.RejectionReason,
                VerificationEvents = payment.VerificationEvents
                    .OrderBy(e => e.CreatedAt)
                    .Select(ToVerificationEventResponse)
                    .ToList(),
                // The receipt this claim became, once verified.
                FinancialTransactionId = transactionByPayment.TryGetValue(payment.Id, out var ledgerId)
                    ? ledgerId
                    : null
            });
        }

        return items;
    }

    /// <summary>
    /// Lapse submissions nobody decided in time, and cancel the appointment that was
    /// holding a slot for them.
    ///
    /// <para>This is the only place an expired booking fee releases its time. Doing it
    /// anywhere else would either free a slot while a real payment was in flight, or
    /// leave a slot blocked forever by an abandoned booking.</para>
    /// </summary>
    public async Task<int> ExpireStaleVerificationsAsync()
    {
        var cutoff = DateTime.UtcNow - VerificationWindow;

        var stale = await _context.Payments
            .Where(p => (p.Status == PaymentStatus.Submitted || p.Status == PaymentStatus.UnderReview)
                        && (p.SubmittedAt ?? p.CreatedAt) < cutoff)
            .ToListAsync();

        foreach (var payment in stale)
        {
            PaymentStateMachine.Transition(payment, PaymentStatus.Expired);

            await AddVerificationEventAsync(
                payment, "expired", null,
                reason: $"No decision within {VerificationWindow.TotalDays:0} days.");

            // The appointment is deliberately left exactly as it is.
            //
            // This used to cancel a booking whose verification window had lapsed. That made the
            // customer's appointment depend on a decision only the business can make, and it could
            // cancel a perfectly valid booking for no reason the customer caused. An expired
            // submission now means only that the money is unverified: the appointment keeps its
            // status, stays in the calendar, and the fee simply shows as outstanding — which is what
            // the owner needs to see in order to chase or waive it.
            _logger.LogInformation(
                "A booking-fee submission expired without a decision. PaymentId={PaymentId} " +
                "AppointmentId={AppointmentId} — the appointment is unaffected.",
                payment.Id, payment.AppointmentId);
        }

        if (stale.Count > 0)
        {
            await _uow.SaveChangesAsync();
            _logger.LogInformation(
                "Expired {Count} manual M-PESA submission(s) past the {Days}-day verification window.",
                stale.Count, VerificationWindow.TotalDays);
        }

        return stale.Count;
    }
}