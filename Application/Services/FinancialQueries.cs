using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// The ledger filter/sort/paging rules, in one place.
///
/// <para>The transactions page, the expenses page and the cashbook all accept the same query
/// shape and MUST all apply it identically — otherwise the same filter would show different
/// rows on different screens, which is the fastest way to lose a business's trust in its own
/// books.</para>
/// </summary>
internal static class FinancialQueries
{
    /// <summary>Applies every non-empty filter, tenant scope included.</summary>
    public static IQueryable<FinancialTransaction> ApplyFilters(
        ApplicationDbContext context,
        IQueryable<FinancialTransaction> query,
        FinancialTransactionQuery filters,
        Guid businessId)
    {
        query = query.Where(t => t.BusinessId == businessId);

        // Voided rows never count towards a total — that is enforced in every sum. This decides
        // only whether they are LISTED, so the cashbook can hide rows that moved nothing while the
        // expense register keeps them visible for an audit.
        if (!filters.IncludeVoided)
            query = query.Where(t => !t.IsVoided);

        if (FinancialWire.TryParseType(filters.Type, out var type))
            query = query.Where(t => t.Type == type);

        // Direction is its own axis: money in is income and adjustments, money out is expenses and
        // refunds. Applied here (not in the UI) so filtering and the totals describe the same rows.
        if (FinancialWire.TryParseDirection(filters.Direction, out var direction))
        {
            query = direction == FinancialDirection.Outgoing
                ? query.Where(t => t.Type == FinancialTransactionType.Expense
                                   || t.Type == FinancialTransactionType.Refund)
                : query.Where(t => t.Type == FinancialTransactionType.Income
                                   || t.Type == FinancialTransactionType.Adjustment);
        }

        if (FinancialWire.TryParseSource(filters.Source, out var source))
            query = query.Where(t => t.Source == source);

        if (FinancialWire.TryParseMethod(filters.PaymentMethod, out var method))
            query = query.Where(t => t.PaymentMethod == method);

        if (FinancialWire.TryParseVerification(filters.VerificationStatus, out var verification))
            query = query.Where(t => t.VerificationStatus == verification);

        if (FinancialWire.TryParseReconciliation(filters.ReconciliationStatus, out var reconciliation))
            query = query.Where(t => t.ReconciliationStatus == reconciliation);

        // The category is normalised to a known key. A value that is not one of the predefined
        // categories matches nothing: silently ignoring it would answer a filtered question with
        // an unfiltered total, which reads as "the business spent less than it did".
        if (!string.IsNullOrWhiteSpace(filters.Category))
        {
            var category = ExpenseCategories.Normalise(filters.Category);
            query = category == null
                ? query.Where(t => false)
                : query.Where(t => t.Category == category);
        }

        if (filters.CustomerId is Guid customerId)
            query = query.Where(t => t.CustomerId == customerId);

        if (filters.RecordedByUserId is Guid recordedBy)
            query = query.Where(t => t.CreatedByUserId == recordedBy);

        // By name, as an EXISTS subquery: no join widened onto the register, and the predicate is
        // still evaluated in the database rather than in memory.
        if (!string.IsNullOrWhiteSpace(filters.RecordedByName))
        {
            var recorder = filters.RecordedByName.Trim();
            query = query.Where(t => t.CreatedByUserId != null
                                     && context.Users.Any(u => u.Id == t.CreatedByUserId && u.FullName.Contains(recorder)));
        }

        // Customer OR payee: a receipt names a customer, an expense names whoever was paid. Both
        // are stored on CustomerName, so one truthful filter covers both sides of the book.
        if (!string.IsNullOrWhiteSpace(filters.Payee))
        {
            var payee = filters.Payee.Trim();
            query = query.Where(t => t.CustomerName != null && t.CustomerName.Contains(payee));
        }

        if (filters.AppointmentId is Guid appointmentId)
            query = query.Where(t => t.AppointmentId == appointmentId);

        if (filters.MinAmount is decimal min)
            query = query.Where(t => t.Amount >= min);

        if (filters.MaxAmount is decimal max)
            query = query.Where(t => t.Amount <= max);

        if (filters.From is DateTime from)
            query = query.Where(t => t.TransactionDate >= from);

        if (filters.To is DateTime to)
            query = query.Where(t => t.TransactionDate <= to);

        // Search covers the fields a person actually types to find a row: a reference (Punched's
        // or M-Pesa's), a customer and the description.
        if (!string.IsNullOrWhiteSpace(filters.Search))
        {
            var term = filters.Search.Trim();
            query = query.Where(t =>
                t.Reference.Contains(term) ||
                (t.ExternalReference != null && t.ExternalReference.Contains(term)) ||
                (t.Description != null && t.Description.Contains(term)) ||
                (t.CustomerName != null && t.CustomerName.Contains(term)));
        }

        return query;
    }

    /// <summary>
    /// Stable ordering. Every sort finishes on <c>Id</c> so two rows with the same date cannot
    /// swap places between pages — which would drop one row and duplicate another during paging.
    /// </summary>
    public static IQueryable<FinancialTransaction> ApplySort(
        IQueryable<FinancialTransaction> query, string? sort, string? direction)
    {
        var descending = !string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase);

        return (sort ?? "date").Trim().ToLowerInvariant() switch
        {
            "amount" => descending
                ? query.OrderByDescending(t => t.Amount).ThenByDescending(t => t.Id)
                : query.OrderBy(t => t.Amount).ThenBy(t => t.Id),
            "reference" => descending
                ? query.OrderByDescending(t => t.Reference).ThenByDescending(t => t.Id)
                : query.OrderBy(t => t.Reference).ThenBy(t => t.Id),
            // A-to-Z by what the row is CALLED. Rows without a description fall back to their
            // reference so they sort where a reader would look for them instead of clustering at
            // the top as nulls.
            "description" or "name" => descending
                ? query.OrderByDescending(t => t.Description ?? t.Reference).ThenByDescending(t => t.Id)
                : query.OrderBy(t => t.Description ?? t.Reference).ThenBy(t => t.Id),
            _ => descending
                ? query.OrderByDescending(t => t.TransactionDate).ThenByDescending(t => t.Id)
                : query.OrderBy(t => t.TransactionDate).ThenBy(t => t.Id)
        };
    }

    /// <summary>Clamped paging: a page size can never be used to pull an entire ledger.</summary>
    public static (int page, int pageSize, int skip) Paging(int page, int pageSize)
    {
        var safePage = Math.Max(1, page);
        var safeSize = Math.Clamp(pageSize, 1, 100);
        return (safePage, safeSize, (safePage - 1) * safeSize);
    }
}
