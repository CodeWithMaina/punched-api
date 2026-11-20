using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Named projection shapes for the EF Core queries in the financial and payment services.
///
/// <para><b>Why these exist instead of anonymous types.</b> EF Core compiles a LINQ query into a
/// delegate and caches it against the query's shape. An anonymous type is compiler-generated and
/// its name is <i>positional</i> — <c>&lt;&gt;f__AnonymousType63`2</c> — so it is renumbered
/// whenever the set of anonymous types in the assembly changes. A cached plan that outlives a
/// rebuild (which is exactly what happens when the API's binaries are replaced while the process
/// is still running, or under <c>dotnet watch</c>) is then left pointing at a type name that no
/// longer exists, and the request fails with:</para>
///
/// <para><c>Could not load type '&lt;&gt;f__AnonymousType63`2' from assembly 'PunchedApi,
/// Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'.</c></para>
///
/// <para>Named records remove that whole class of failure: the projection shape is a declared
/// type with a stable name, not a compiler artefact that shifts under the query plan's feet.</para>
///
/// <para>Every one of these is <c>internal</c> and is used only inside the service that declares
/// it, so none of them can cross an API boundary — the HTTP contract is served by the DTOs in
/// <c>Application/DTOs</c>.</para>
/// </summary>
internal sealed record UserIdNameRow(Guid Id, string FullName);

internal sealed record UserContactRow(Guid Id, string FullName, string? PhoneNumber, string? Email);

internal sealed record AppointmentContextRow(Guid Id, string Status, DateTime ScheduledAt, Guid? StaffUserId);

internal sealed record AppointmentServiceLineRow(Guid AppointmentId, string Name, decimal Price);

internal sealed record AppointmentAmountRow(Guid AppointmentId, decimal Amount);

internal sealed record AppointmentPriceRow(Guid AppointmentId, decimal Price);

internal sealed record PaymentReceiptOwnerRow(Guid Id, string? ParsedTransactionCode);

internal sealed record LedgerRowRef(Guid SourcePaymentId, Guid Id);

internal sealed record ServiceLineDetailRow(Guid AppointmentId, Guid ServiceCatalogItemId, string Name, decimal Price);

internal sealed record ServiceCategoryRefRow(Guid Id, Guid CategoryId);

internal sealed record NamedRow(Guid Id, string Name);

internal sealed record AppointmentStaffRefRow(Guid Id, Guid StaffUserId);

internal sealed record RefundOfRow(Guid RefundOf, decimal Amount);

internal sealed record LedgerReferenceRow(Guid Id, decimal Amount, string Reference);

internal sealed record PaymentReceiptRow(Guid Id, string Receipt, decimal Amount);

internal sealed record IncomeBreakdownRow(
    decimal Amount,
    FinancialPaymentMethod PaymentMethod,
    FinancialVerificationStatus VerificationStatus,
    DateTime TransactionDate);

internal sealed record TrendRow(decimal Amount, DateTime TransactionDate, FinancialVerificationStatus VerificationStatus);

internal sealed record ExpenseTrendRow(decimal Amount, string? Category, DateTime TransactionDate);

/// <summary>One ledger movement, as the cashbook's running balance and totals both read it.</summary>
internal sealed record LedgerMovementRow(
    Guid Id,
    DateTime TransactionDate,
    decimal Amount,
    FinancialTransactionType Type,
    FinancialPaymentMethod PaymentMethod,
    FinancialVerificationStatus VerificationStatus,
    bool IsVoided,
    Guid? CreatedByUserId);
