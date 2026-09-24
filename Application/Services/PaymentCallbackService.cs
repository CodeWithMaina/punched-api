using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Idempotent provider callback processing. Every callback is persisted BEFORE it
/// is applied; duplicates are detected via the unique EventKey constraint and the
/// first-wins rule, so the same callback arriving twice can never create two
/// successful payments. Unmatched C2B confirmations are retained as a
/// reconciliation queue instead of being dropped.
///
/// Callback processing is deliberately allowed to reach terminal states from
/// Expired (a delayed success callback after Punched's own expiry window is still
/// a real payment — money moved) via Expired → AwaitingCustomer → Success.
/// </summary>
public class PaymentCallbackService : IPaymentCallbackService
{
    private readonly ApplicationDbContext _context;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<PaymentCallbackService> _logger;

    public PaymentCallbackService(ApplicationDbContext context, IUnitOfWork uow, ILogger<PaymentCallbackService> logger)
    {
        _context = context;
        _uow = uow;
        _logger = logger;
    }

    // ── STK push result callback ────────────────────────────

    public async Task<(string outcome, Guid? paymentId)> ProcessStkCallbackAsync(string rawPayload)
    {
        using var doc = JsonDocument.Parse(rawPayload);
        var root = doc.RootElement;

        var body = root.TryGetProperty("Body", out var b) ? b : default;
        var stk = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("stkCallback", out var s) ? s : default;
        if (stk.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning("STK callback received without Body.stkCallback structure.");
            return ("rejected", null);
        }

        var checkoutRequestId = GetStr(stk, "CheckoutRequestID") ?? string.Empty;
        var resultCode = GetStr(stk, "ResultCode") ?? string.Empty;
        var resultDesc = GetStr(stk, "ResultDesc");
                var callbackId = GetStr(stk, "CheckoutRequestID") ?? Guid.NewGuid().ToString("N");

        // Idempotency key includes the result code so a duplicate of the SAME result
        // is ignored, while Safaricom legitimately sending both a timeout and a later
        // result can still be distinguished.
        var eventKey = "stk:" + checkoutRequestId + ":" + resultCode;

        if (await _context.PaymentCallbacks.AnyAsync(c => c.EventKey == eventKey))
            return ("duplicate", null);

        // Locate the payment by the attempt's CheckoutRequestID.
        var attempt = await _context.PaymentAttempts
            .OrderByDescending(a => a.AttemptNumber)
            .FirstOrDefaultAsync(a => a.CheckoutRequestId == checkoutRequestId);

        var record = new PaymentCallback
        {
            Id = Guid.NewGuid(),
            Kind = "stk",
            EventKey = eventKey,
            ResultCode = resultCode,
            ResultDescription = Truncate(resultDesc, 500),
            RawPayload = rawPayload,
            PaymentId = attempt?.PaymentId,
            CreatedAt = DateTime.UtcNow
        };

        var payment = attempt == null
            ? null
            : await _context.Payments.Include(p => p.Attempts).FirstOrDefaultAsync(p => p.Id == attempt.PaymentId);

        if (payment == null)
        {
            record.Outcome = "unmatched";
            await _context.PaymentCallbacks.AddAsync(record);
            await _uow.SaveChangesAsync();
            _logger.LogWarning("STK callback for unknown checkout id {CheckoutRequestId} stored as unmatched.", checkoutRequestId);
            return ("unmatched", null);
        }

        record.BusinessId = payment.BusinessId;

        if (resultCode == "0" && stk.TryGetProperty("CallbackMetadata", out var meta) && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("Item", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            decimal? amount = null;
            string? receipt = null;
            string? transactionDate = null;
            decimal? phone = null;
            foreach (var item in items.EnumerateArray())
            {
                var name = GetStr(item, "Name");
                switch (name)
                {
                    case "Amount": amount = GetNum(item, "Value"); break;
                    case "MpesaReceiptNumber": receipt = GetStr(item, "Value"); break;
                    case "TransactionDate": transactionDate = GetStr(item, "Value"); break;
                    case "PhoneNumber": phone = GetNum(item, "Value"); break;
                }
            }

            record.TransactionId = receipt;

            // Amount verification: a success whose amount does not match is NOT applied.
            if (amount.HasValue && amount.Value != payment.Amount)
            {
                record.Outcome = "rejected";
                record.ResultDescription = Truncate(
                    (record.ResultDescription ?? string.Empty) + " | Amount mismatch: expected " + payment.Amount + ", got " + amount.Value, 500);
                await _context.PaymentCallbacks.AddAsync(record);
                await _uow.SaveChangesAsync();
                _logger.LogError(
                    "STK callback amount mismatch for payment {PaymentId}: expected {Expected}, got {Actual} — payment NOT marked successful.",
                    payment.Id, payment.Amount, amount.Value);
                return ("rejected", payment.Id);
            }

            ApplySuccess(payment, attempt, receipt, "stk", record);
            record.Outcome = "applied";
            record.Processed = true;
            record.ProcessedAt = DateTime.UtcNow;
        }
        else if (resultCode != "0")
        {
            var (code, message, retryable) = MapStkFailure(resultCode, resultDesc);
            record.Outcome = "applied";
            record.Processed = true;
            record.ProcessedAt = DateTime.UtcNow;

            if (payment.Status == PaymentStatus.Success)
            {
                // Late failure after success — keep the success (money already moved).
                _logger.LogWarning("STK failure callback {Code} arrived after payment {PaymentId} was already successful; ignoring.", resultCode, payment.Id);
            }
            else
            {
                ApplyFailure(payment, attempt, code, message, retryable);
            }
        }
        else
        {
            record.Outcome = "rejected"; // success without metadata — cannot verify
        }

        await _context.PaymentCallbacks.AddAsync(record);
        await _uow.SaveChangesAsync();
        return (record.Outcome ?? "applied", payment.Id);
    }

    // ── C2B confirmation (PayBill / Till) ───────────────────

    public async Task<(string outcome, Guid? paymentId)> ProcessC2BConfirmationAsync(string rawPayload)
    {
        using var doc = JsonDocument.Parse(rawPayload);
        var root = doc.RootElement;

        var transId = GetStr(root, "TransID") ?? string.Empty;
        var resultCode = GetStr(root, "ResultCode") ?? GetStr(root, "ResultCode") ?? "0";
        var amount = GetNum(root, "TransAmount");
        var shortCode = GetStr(root, "BusinessShortCode");
        var billRef = GetStr(root, "BillRefNumber");
        var msisdn = GetStr(root, "MSISDN");
        var transactionType = GetStr(root, "TransactionType");

        if (string.IsNullOrEmpty(transId))
        {
            _logger.LogWarning("C2B confirmation without TransID rejected.");
            return ("rejected", null);
        }

        var eventKey = "c2b:" + transId;
        if (await _context.PaymentCallbacks.AnyAsync(c => c.EventKey == eventKey))
            return ("duplicate", null);

        var record = new PaymentCallback
        {
            Id = Guid.NewGuid(),
            Kind = "c2b_confirmation",
            EventKey = eventKey,
            ResultCode = resultCode,
            ResultDescription = Truncate(transactionType, 500),
            TransactionId = transId,
            RawPayload = rawPayload,
            CreatedAt = DateTime.UtcNow
        };

        // Tenant resolution: the shortcode identifies the business — Business A can
        // never be credited with Business B's payment.
        BusinessPaymentConfig? config = null;
        if (!string.IsNullOrEmpty(shortCode))
            config = await _context.BusinessPaymentConfigs
                .FirstOrDefaultAsync(c => c.MpesaShortCode == shortCode && c.MpesaEnabled);

        // Payment matching: the reference the customer entered (BillRefNumber) must
        // equal a Punched payment reference (PMT-…). Amount+phone alone is never
        // trusted as a match key.
        Payment? payment = null;
        if (config != null && !string.IsNullOrEmpty(billRef))
            payment = await _context.Payments
                .Include(p => p.Attempts)
                .FirstOrDefaultAsync(p => p.Reference == billRef && p.BusinessId == config.BusinessId);

        record.BusinessId = config?.BusinessId;
        record.PaymentId = payment?.Id;

        if (config == null || payment == null)
        {
            record.Outcome = "unmatched";
            await _context.PaymentCallbacks.AddAsync(record);
            await _uow.SaveChangesAsync();
            _logger.LogWarning(
                "C2B confirmation {TransID} (shortcode {ShortCode}, ref {BillRef}, amount {Amount}) stored as UNMATCHED for reconciliation.",
                transId, shortCode, billRef, amount);
            return ("unmatched", null);
        }

        if (resultCode == "0" || resultCode == "00" || string.IsNullOrEmpty(resultCode) || resultCode == "Success")
        {
            if (amount.HasValue && amount.Value != payment.Amount)
            {
                record.Outcome = "rejected";
                record.ResultDescription = "Amount mismatch: expected " + payment.Amount + ", got " + amount.Value;
                await _context.PaymentCallbacks.AddAsync(record);
                await _uow.SaveChangesAsync();
                _logger.LogError("C2B amount mismatch for payment {PaymentId}: expected {Expected}, got {Actual}.", payment.Id, payment.Amount, amount.Value);
                return ("rejected", payment.Id);
            }

            var attempt = payment.Attempts.OrderBy(a => a.AttemptNumber).LastOrDefault();
            ApplySuccess(payment, attempt, transId, "c2b", record);
            record.Outcome = "applied";
            record.Processed = true;
            record.ProcessedAt = DateTime.UtcNow;
            _logger.LogInformation("C2B confirmation {TransID} applied to payment {PaymentId} (business {BusinessId}).", transId, payment.Id, payment.BusinessId);
        }
        else
        {
            record.Outcome = "applied";
            record.Processed = true;
            record.ProcessedAt = DateTime.UtcNow;
            ApplyFailure(payment, payment.Attempts.OrderBy(a => a.AttemptNumber).LastOrDefault(), "C2B_" + resultCode, record.ResultDescription, false);
        }

        await _context.PaymentCallbacks.AddAsync(record);
        await _uow.SaveChangesAsync();
        return (record.Outcome ?? "applied", payment.Id);
    }

    /// <summary>C2B validation (only active when Safaricom enables validation for the shortcode): accept known references, reject unknown ones.</summary>
    public async Task<(bool accepted, string description)> ProcessC2BValidationAsync(string rawPayload)
    {
        using var doc = JsonDocument.Parse(rawPayload);
        var root = doc.RootElement;

        var billRef = GetStr(root, "BillRefNumber");
        var shortCode = GetStr(root, "BusinessShortCode");

        var eventKey = "c2b-validation:" + (GetStr(root, "TransID") ?? Guid.NewGuid().ToString("N"));
        if (await _context.PaymentCallbacks.AnyAsync(c => c.EventKey == eventKey))
            return (true, "Accepted");

        var known = false;
        if (!string.IsNullOrEmpty(billRef) && !string.IsNullOrEmpty(shortCode))
        {
            known = await _context.BusinessPaymentConfigs
                .Where(c => c.MpesaShortCode == shortCode)
                .Join(_context.Payments.Where(p => p.Reference == billRef),
                    c => c.BusinessId, p => p.BusinessId, (c, p) => p)
                .AnyAsync();
        }

        await _context.PaymentCallbacks.AddAsync(new PaymentCallback
        {
            Id = Guid.NewGuid(),
            Kind = "c2b_validation",
            EventKey = eventKey,
            ResultCode = known ? "0" : "C2B00012",
            ResultDescription = known ? "Accepted" : "Unknown payment reference",
            RawPayload = rawPayload,
            Outcome = "applied",
            Processed = true,
            ProcessedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        await _uow.SaveChangesAsync();

        // Accept-with-warning when validation is enabled but the reference is unknown:
        // rejecting loses the customer's money on an otherwise valid till/paybill.
        // The confirmation callback will store it as unmatched for reconciliation.
        _logger.LogInformation("C2B validation for ref {BillRef} (shortcode {ShortCode}): {Result}", billRef, shortCode, known ? "accepted" : "accepted-with-warning");
        return (true, "Accepted");
    }

    // ── Shared apply helpers ────────────────────────────────

    /// <summary>Mark a payment successful from a callback. Handles late callbacks after expiry.</summary>
    private void ApplySuccess(Payment payment, PaymentAttempt? attempt, string? receipt, string source, PaymentCallback record)
    {
        // Idempotency: never re-apply success to an already-successful payment.
        if (payment.Status == PaymentStatus.Success)
            return;

        // Late success after Punched-side expiry: walk Expired → AwaitingCustomer → Success.
        if (payment.Status == PaymentStatus.Expired)
            PaymentStateMachine.Transition(payment, PaymentStatus.AwaitingCustomer);

        PaymentStateMachine.Transition(payment, PaymentStatus.Success);
        payment.ExternalReference = receipt;

        if (attempt != null)
        {
            attempt.Status = "success";
            attempt.ProviderReference = receipt;
            attempt.CompletedAt = DateTime.UtcNow;
        }
        else
        {
            // C2B payments are usually created without an attempt (customer pays
            // offline); record the confirmation as the single attempt.
            var newAttempt = new PaymentAttempt
            {
                Id = Guid.NewGuid(),
                PaymentId = payment.Id,
                AttemptNumber = payment.Attempts.Count + 1,
                Provider = source == "c2b" ? PaymentProviderKind.DarajaC2B : payment.Provider,
                Status = "success",
                ProviderReference = receipt,
                CompletedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };
            // Added through the DbSet so EF marks it as Added (see PaymentService.AddAttempt).
            _context.PaymentAttempts.Add(newAttempt);
            payment.Attempts.Add(newAttempt);
        }

        _logger.LogInformation(
            "Payment {PaymentId} succeeded via {Source}; receipt {Receipt}; callback {EventKey}",
            payment.Id, source, receipt, record.EventKey);
    }

    /// <summary>Mark a payment failed from a callback. Never overwrites previous attempts.</summary>
    private void ApplyFailure(Payment payment, PaymentAttempt? attempt, string errorCode, string? message, bool retryable)
    {
        if (payment.Status is PaymentStatus.Success or PaymentStatus.Reversed or PaymentStatus.Refunded)
            return;

        PaymentStateMachine.Transition(payment, PaymentStatus.Failed);
        if (attempt != null && attempt.Status == "pending")
        {
            attempt.Status = "failed";
            attempt.ErrorCode = errorCode;
            attempt.ErrorMessage = message != null && message.Length > 500 ? message[..500] : message;
            attempt.CompletedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Map STK result codes to normalised errors + retryability classification.</summary>
    private static (string code, string message, bool retryable) MapStkFailure(string resultCode, string? desc) => resultCode switch
    {
        "1" => ("INSUFFICIENT_FUNDS", desc ?? "The initiator has insufficient funds.", false),
        "1032" => ("CUSTOMER_CANCELLED", desc ?? "Customer cancelled the M-PESA prompt.", false),
        "1037" => ("DS_TIMEOUT", desc ?? "Customer could not be reached in time.", true),
        "2001" => ("INVALID_MERCHANT_CREDENTIALS", desc ?? "Invalid merchant credentials (passkey/shortcode).", false),
        "1019" => ("INVALID_MSISDN", desc ?? "Invalid customer phone number.", false),
        _ => ("PROVIDER_ERROR_" + resultCode, desc ?? "Provider reported an error.", true)
    };

    /// <summary>
    /// Reads a property as text. Daraja is inconsistent about JSON types: real STK
    /// callbacks send <c>"ResultCode": 0</c> as a NUMBER (and TransactionDate as a
    /// number too), so accepting only <see cref="JsonValueKind.String"/> silently
    /// turned a successful callback into an empty result code — which the failure
    /// branch then applied as a FAILED payment. Numbers/booleans are therefore
    /// coerced to their raw text.
    /// </summary>
    private static string? GetStr(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static decimal? GetNum(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String => decimal.TryParse(v.GetString(), out var d) ? d : null,
            _ => null
        };
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];
}
