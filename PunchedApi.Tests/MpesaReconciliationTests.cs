using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>
/// M-Pesa statement reconciliation: matching, discrepancies, unmatched money, duplicate imports and
/// duplicate receipts, plus the rule that reconciliation never quietly verifies a payment.
/// </summary>
public class MpesaReconciliationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public MpesaReconciliationTests()
    {
        _connection = BookingTestBase.CreateConnection();
        _context = BookingTestBase.CreateContext(_connection);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private MpesaReconciliationService Reconciliation() => new(
        _context, new UnitOfWork(_context), TestHelpers.CreateLogger<MpesaReconciliationService>());

    private FinancialService Finances() => new(
        _context, new UnitOfWork(_context), TestHelpers.CreateLogger<FinancialService>());

    private async Task<(Business business, User owner)> SeedAsync()
    {
        var owner = BookingTestBase.CreateOwner("recon@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Recon Salon");
        await BookingTestBase.SeedAsync(_context, owner, business);
        return (business, owner);
    }

    /// <summary>
    /// A statement with a header row: a line that should match, one whose amount disagrees, one no
    /// Punched record knows about, and one that appears twice inside the file.
    /// </summary>
    private const string StatementCsv =
        "Receipt No,Completion Time,Amount,Name,MSISDN,Account,Details\n" +
        "QJK42NUM01,08/11/2026 09:15,1000.00,Jane Wanjiru,254712345678,PMT-AAAA,Buy Goods\n" +
        "QJK42NUM02,08/11/2026 10:05,700.00,Peter Otieno,254700000001,PMT-BBBB,Buy Goods\n" +
        "QJK42NUM03,08/11/2026 11:30,500.00,Unknown Payer,254700000002,PMT-CCCC,Buy Goods\n" +
        "QJK42NUM03,08/11/2026 11:31,500.00,Unknown Payer,254700000002,PMT-CCCC,Buy Goods\n";

    // ═══════════════════════════════════════════════════════════
    //  MATCHING
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task An_import_matches_by_receipt_flags_a_discrepancy_and_reports_unmatched_money()
    {
        var (_, owner) = await SeedAsync();

        // Punched recorded 1,000 for the first receipt, and only 650 for the second one.
        var exact = await Finances().RecordIncomeAsync(new RecordIncomeRequest
        {
            Amount = 1000m,
            Source = "walk_in",
            PaymentMethod = "mpesa_till",
            TransactionReference = "QJK42NUM01"
        }, owner.Id, "Business");

        var shortfall = await Finances().RecordIncomeAsync(new RecordIncomeRequest
        {
            Amount = 650m,
            Source = "walk_in",
            PaymentMethod = "mpesa_till",
            TransactionReference = "QJK42NUM02"
        }, owner.Id, "Business");

        Assert.True(exact.Success);
        Assert.True(shortfall.Success);

        var import = await Reconciliation().ImportStatementAsync(new ImportMpesaStatementRequest
        {
            FileName = "november-statement.csv",
            Content = StatementCsv
        }, owner.Id, "Business");

        Assert.True(import.Success);
        var workspace = import.Data!;

        Assert.NotNull(workspace.Import);
        Assert.Equal(4, workspace.Import!.LineCount);
        Assert.Equal(1, workspace.Import.MatchedCount);
        Assert.Equal(1, workspace.Import.DiscrepancyCount);
        Assert.Equal(1, workspace.Import.UnmatchedCount);
        Assert.Equal(1, workspace.Import.ExcludedCount);

        var matchedLine = workspace.Lines.Single(l => l.ReceiptNumber == "QJK42NUM01");
        Assert.Equal("matched", matchedLine.Status);
        Assert.Equal(1000m, matchedLine.PunchedAmount);
        Assert.Equal(0m, matchedLine.AmountVariance);
        Assert.Equal("reference", matchedLine.MatchMethod);

        var discrepancy = workspace.Lines.Single(l => l.ReceiptNumber == "QJK42NUM02");
        Assert.Equal("discrepancy", discrepancy.Status);
        // The statement says MORE than Punched recorded, so the variance is positive.
        Assert.Equal(50m, discrepancy.AmountVariance);

        var stranger = workspace.Lines.Single(l => l.ReceiptNumber == "QJK42NUM03" && !l.IsDuplicateInFile);
        Assert.Equal("unreconciled", stranger.Status);
        Assert.Null(stranger.MatchedTransactionId);

        var duplicate = workspace.Lines.Single(l => l.ReceiptNumber == "QJK42NUM03" && l.IsDuplicateInFile);
        Assert.True(duplicate.IsDuplicateInFile);
        Assert.Equal("excluded", duplicate.Status);

        // The ledger row records that it was CHECKED — and its verification is untouched.
        var reconciliationState = await _context.FinancialTransactions
            .SingleAsync(t => t.ExternalReference == "QJK42NUM01");

        Assert.Equal(ReconciliationStatus.Matched, reconciliationState.ReconciliationStatus);
        Assert.NotNull(reconciliationState.ReconciledAt);
        Assert.Equal(FinancialVerificationStatus.Pending, reconciliationState.VerificationStatus);
    }

    [Fact]
    public async Task A_verified_payment_is_not_reconciled_merely_by_being_verified()
    {
        var (business, _) = await SeedAsync();

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Type = PaymentType.FullPayment,
            Method = PaymentMethod.Mpesa,
            Provider = PaymentProviderKind.ManualMpesa,
            Status = PaymentStatus.Verified,
            Amount = 1000m,
            Currency = "KES",
            Reference = "PMT-VERIFIED1",
            ParsedTransactionCode = "QJK42NUM01",
            ParsedAmount = 1000m,
            VerifiedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        await BookingTestBase.SeedAsync(_context, payment);

        var ledger = new FinancialLedgerService(
            _context, new UnitOfWork(_context), TestHelpers.CreateLogger<FinancialLedgerService>());

        await ledger.EnsureIncomeForPaymentAsync(payment);

        var row = await _context.FinancialTransactions.SingleAsync(t => t.SourcePaymentId == payment.Id);
        Assert.Equal(FinancialVerificationStatus.Verified, row.VerificationStatus);
        Assert.Equal(ReconciliationStatus.Unreconciled, row.ReconciliationStatus);
        Assert.Null(row.ReconciledAt);
    }

    [Fact]
    public async Task Re_importing_the_same_statement_does_not_duplicate_its_lines()
    {
        var (_, owner) = await SeedAsync();

        var first = await Reconciliation().ImportStatementAsync(new ImportMpesaStatementRequest
        {
            FileName = "statement.csv",
            Content = StatementCsv
        }, owner.Id, "Business");

        var second = await Reconciliation().ImportStatementAsync(new ImportMpesaStatementRequest
        {
            FileName = "statement.csv",
            Content = StatementCsv
        }, owner.Id, "Business");

        Assert.True(first.Success);
        Assert.True(second.Success);

        Assert.Equal(1, await _context.MpesaStatementImports.CountAsync());
        Assert.Equal(4, await _context.MpesaStatementLines.CountAsync());

        // The same batch is handed back, so the screen never suggests a second import happened.
        Assert.Equal(first.Data!.Import!.Id, second.Data!.Import!.Id);
    }

    [Fact]
    public async Task A_line_with_no_punched_counterpart_can_be_excluded_manually()
    {
        var (_, owner) = await SeedAsync();

        var import = await Reconciliation().ImportStatementAsync(new ImportMpesaStatementRequest
        {
            Content = StatementCsv,
            FileName = "statement.csv"
        }, owner.Id, "Business");

        // The line that has no counterpart is the FIRST occurrence; the repeat is already excluded.
        var stranger = import.Data!.Lines.Single(l => l.ReceiptNumber == "QJK42NUM03" && !l.IsDuplicateInFile);

        var excluded = await Reconciliation().MatchLineAsync(stranger.Id, new MatchStatementLineRequest
        {
            Status = "excluded",
            Note = "Personal transfer, not business income"
        }, owner.Id, "Business");

        Assert.True(excluded.Success);
        Assert.Equal("excluded", excluded.Data!.Status);
        Assert.Equal("manual", excluded.Data.MatchMethod);

        // The batch counters follow the decision.
        var batch = await _context.MpesaStatementImports.SingleAsync();
        Assert.Equal(2, batch.ExcludedCount);
    }

    [Fact]
    public async Task A_file_with_no_readable_lines_is_refused_rather_than_silently_accepted()
    {
        var (_, owner) = await SeedAsync();

        var result = await Reconciliation().ImportStatementAsync(new ImportMpesaStatementRequest
        {
            FileName = "notes.csv",
            Content = "hello,world\nthis,is not,a statement"
        }, owner.Id, "Business");

        Assert.False(result.Success);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Equal(0, await _context.MpesaStatementImports.CountAsync());
    }
}
