using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;
using PunchedApi.Infrastructure.Services.Payments;

namespace PunchedApi.Tests;

/// <summary>
/// The guarantee this file exists to prove: a settled payment cannot permanently disappear from the
/// cashbook, and no amount of retrying, replaying or reconciling can create its income twice.
///
/// <para>These tests use SQLite with the real EF model (so the unique indexes and transaction
/// behaviour are exercised), never mocks of the persistence layer. Where a scenario needs a broken
/// database write, a scripted ledger service fails the call instead — the durable obligation is then
/// asserted in the database, not in memory.</para>
/// </summary>
public class PaymentLedgerOutboxTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public PaymentLedgerOutboxTests()
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

    // ── Harness ────────────────────────────────────────────────

    private static PaymentLedgerOptions OptionsFor(int maxAttempts = 5) => new()
    {
        MaxAttempts = maxAttempts,
        BaseBackoffSeconds = 60,
        MaxBackoffSeconds = 300,
        BatchSize = 25,
        ReconciliationBatchSize = 100
    };

    private PaymentLedgerOutbox Outbox() =>
        new(_context, TestHelpers.CreateLogger<PaymentLedgerOutbox>());

    private FinancialLedgerService Ledger() =>
        new(_context, new UnitOfWork(_context), TestHelpers.CreateLogger<FinancialLedgerService>());

    private PaymentLedgerProcessor Processor(
        IFinancialLedgerService? ledger = null,
        PaymentLedgerOptions? options = null) =>
        new(
            _context,
            ledger ?? Ledger(),
            Outbox(),
            Microsoft.Extensions.Options.Options.Create(options ?? OptionsFor()),
            TestHelpers.CreateLogger<PaymentLedgerProcessor>());

    private PaymentLedgerReconciliationService Reconciliation() =>
        new(_context, Outbox(), TestHelpers.CreateLogger<PaymentLedgerReconciliationService>());

    private async Task<(Business Business, User Owner, User Customer, Appointment Appointment)> SeedAsync()
    {
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var customer = BookingTestBase.CreateCustomer();

        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            StaffUserId = owner.Id,
            ScheduledAt = DateTime.UtcNow.AddHours(2),
            EndAt = DateTime.UtcNow.AddHours(3),
            Status = "booked",
            CreatedAt = DateTime.UtcNow
        };

        appointment.Resources.Add(new AppointmentResource
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            ServiceCatalogItemId = Guid.NewGuid(),
            Name = "Haircut",
            DurationMinutes = 30,
            Price = 1000m,
            CreatedAt = DateTime.UtcNow
        });

        await BookingTestBase.SeedAsync(_context, owner, business, customer, appointment);
        return (business, owner, customer, appointment);
    }

    private async Task<Payment> SeedSettledPaymentAsync(
        Business business, User customer, Appointment appointment,
        decimal amount = 1000m, PaymentStatus status = PaymentStatus.Success)
    {
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            AppointmentId = appointment.Id,
            Type = PaymentType.FullPayment,
            Method = PaymentMethod.Cash,
            Provider = PaymentProviderKind.Cash,
            Status = status,
            Amount = amount,
            Currency = "KES",
            Reference = "PMT-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
            CompletedAt = status == PaymentStatus.Success ? DateTime.UtcNow : null,
            VerifiedAt = status == PaymentStatus.Verified ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow
        };

        await BookingTestBase.SeedAsync(_context, payment);
        return payment;
    }

    private Task<int> IncomeCountAsync(Guid paymentId) =>
        _context.FinancialTransactions.CountAsync(t => t.SourcePaymentId == paymentId);

    private Task<PaymentLedgerObligation> ObligationAsync(Guid paymentId) =>
        _context.PaymentLedgerObligations.AsNoTracking().SingleAsync(o => o.PaymentId == paymentId);

    /// <summary>Makes a backed-off obligation due now, without waiting for the real clock.</summary>
    private Task MakeDueAsync(Guid paymentId) =>
        _context.PaymentLedgerObligations
            .Where(o => o.PaymentId == paymentId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow.AddMinutes(-1)));

    // ── Settlement → ledger shape ──────────────────────────────

    [Fact]
    public async Task Processing_a_settled_payment_writes_one_verified_income_and_completes_the_obligation()
    {
        var (business, _, customer, appointment) = await SeedAsync();
        var payment = await SeedSettledPaymentAsync(business, customer, appointment, 1000m);

        Assert.True(await Outbox().EnsureObligationAsync(payment));

        var result = await Processor().ProcessAsync(payment.Id);

        Assert.Equal(LedgerProcessOutcome.Created, result.Outcome);
        Assert.NotNull(result.FinancialTransactionId);

        var income = await _context.FinancialTransactions.SingleAsync(t => t.SourcePaymentId == payment.Id);
        Assert.Equal(FinancialTransactionType.Income, income.Type);
        Assert.Equal(FinancialVerificationStatus.Verified, income.VerificationStatus);
        Assert.Equal(1000m, income.Amount);
        Assert.Equal("KES", income.Currency);
        Assert.Equal(appointment.Id, income.AppointmentId);

        var obligation = await ObligationAsync(payment.Id);
        Assert.Equal(PaymentLedgerObligationStatus.Completed, obligation.Status);
        Assert.Equal(income.Id, obligation.FinancialTransactionId);
        Assert.NotNull(obligation.CompletedAt);
        Assert.Null(obligation.LastError);
    }

    [Fact]
    public async Task Processing_the_same_payment_again_is_idempotent_and_never_duplicates_income()
    {
        var (business, _, customer, appointment) = await SeedAsync();
        var payment = await SeedSettledPaymentAsync(business, customer, appointment);

        await Outbox().EnsureObligationAsync(payment);

        var first = await Processor().ProcessAsync(payment.Id);
        var second = await Processor().ProcessAsync(payment.Id);

        Assert.Equal(LedgerProcessOutcome.Created, first.Outcome);
        Assert.Equal(LedgerProcessOutcome.AlreadyRecorded, second.Outcome);
        Assert.Equal(first.FinancialTransactionId, second.FinancialTransactionId);
        Assert.Equal(1, await IncomeCountAsync(payment.Id));
    }

    [Fact]
    public async Task Two_payments_for_one_appointment_each_get_their_own_income_row()
    {
        var (business, _, customer, appointment) = await SeedAsync();

        // A booking fee and a balance payment are two legitimate settlements of one appointment.
        var bookingFee = await SeedSettledPaymentAsync(business, customer, appointment, 500m, PaymentStatus.Verified);
        var balance = await SeedSettledPaymentAsync(business, customer, appointment, 500m, PaymentStatus.Success);

        foreach (var payment in new[] { bookingFee, balance })
            await Outbox().EnsureObligationAsync(payment);

        await Processor().ProcessAsync(bookingFee.Id);
        await Processor().ProcessAsync(balance.Id);

        Assert.Equal(1, await IncomeCountAsync(bookingFee.Id));
        Assert.Equal(1, await IncomeCountAsync(balance.Id));

        var amounts = await _context.FinancialTransactions
            .Where(t => t.AppointmentId == appointment.Id)
            .Select(t => t.Amount)
            .ToListAsync();

        Assert.Equal(2, amounts.Count);
        Assert.Equal(1000m, amounts.Sum());
    }

    [Fact]
    public async Task A_payment_that_is_not_settled_never_becomes_income()
    {
        var (business, _, customer, appointment) = await SeedAsync();
        var submitted = await SeedSettledPaymentAsync(business, customer, appointment, 500m, PaymentStatus.Submitted);

        Assert.False(await Outbox().EnsureObligationAsync(submitted));

        // Even a manually introduced obligation cannot turn a claim into revenue.
        _context.PaymentLedgerObligations.Add(new PaymentLedgerObligation
        {
            Id = Guid.NewGuid(),
            PaymentId = submitted.Id,
            BusinessId = business.Id,
            Status = PaymentLedgerObligationStatus.Pending,
            NextAttemptAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var result = await Processor().ProcessAsync(submitted.Id);

        Assert.Equal(LedgerProcessOutcome.FailedPermanently, result.Outcome);
        Assert.Equal("PAYMENT_NOT_SETTLED", result.ErrorCode);
        Assert.Equal(0, await IncomeCountAsync(submitted.Id));

        var obligation = await ObligationAsync(submitted.Id);
        Assert.Equal(PaymentLedgerObligationStatus.Failed, obligation.Status);
        Assert.Equal(PaymentLedgerFailureKind.Permanent, obligation.FailureKind);
        Assert.Equal("PAYMENT_NOT_SETTLED", obligation.LastErrorCode);
    }

    [Fact]
    public async Task Ensuring_the_obligation_repeatedly_still_leaves_exactly_one_row()
    {
        var (business, _, customer, appointment) = await SeedAsync();
        var payment = await SeedSettledPaymentAsync(business, customer, appointment);

        // Three settlements of the same payment (a double-click plus a replayed callback): the
        // `ON CONFLICT (payment_id) DO NOTHING` insert is what makes only the first one a creation.
        var created = new[]
        {
            await Outbox().EnsureObligationAsync(payment),
            await Outbox().EnsureObligationAsync(payment),
            await Outbox().EnsureObligationAsync(payment)
        };

        Assert.Equal(1, created.Count(createdOne => createdOne));
        Assert.Equal(1, await _context.PaymentLedgerObligations.CountAsync(o => o.PaymentId == payment.Id));
    }


    // ── Failure, retry and recovery ────────────────────────────

    /// <summary>
    /// A ledger whose first N attempts fail transiently, then delegates to the real service. This is
    /// how a database outage looks to the processor: the payment is already settled, so the failure
    /// must leave work behind rather than vanish.
    /// </summary>
    private sealed class ScriptedLedger : IFinancialLedgerService
    {
        private readonly IFinancialLedgerService _inner;
        public int FailuresRemaining { get; set; }
        public int Attempts { get; private set; }

        public ScriptedLedger(IFinancialLedgerService inner, int failures) =>
            (_inner, FailuresRemaining) = (inner, failures);

        public Task<FinancialTransaction?> EnsureIncomeForPaymentAsync(
            Payment payment, CancellationToken cancellationToken = default) =>
            _inner.EnsureIncomeForPaymentAsync(payment, cancellationToken);

        public Task<LedgerIncomeResult> TryEnsureIncomeForPaymentAsync(
            Payment payment, CancellationToken cancellationToken = default)
        {
            Attempts += 1;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining -= 1;
                return Task.FromResult(LedgerIncomeResult.TransientFailure(
                    "SIMULATED_OUTAGE", "The ledger database was unavailable."));
            }

            return _inner.TryEnsureIncomeForPaymentAsync(payment, cancellationToken);
        }

        public Task<FinancialTransaction?> EnsureRefundForReversedPaymentAsync(
            Payment payment, string reason, CancellationToken cancellationToken = default) =>
            _inner.EnsureRefundForReversedPaymentAsync(payment, reason, cancellationToken);
    }

    [Fact]
    public async Task A_transient_ledger_failure_leaves_a_durable_obligation_that_the_worker_finishes()
    {
        var (business, _, customer, appointment) = await SeedAsync();
        var payment = await SeedSettledPaymentAsync(business, customer, appointment);

        await Outbox().EnsureObligationAsync(payment);

        var flaky = new ScriptedLedger(Ledger(), failures: 1);
        var firstAttempt = await Processor(flaky).ProcessAsync(payment.Id);

        Assert.Equal(LedgerProcessOutcome.RetryScheduled, firstAttempt.Outcome);
        Assert.Equal(0, await IncomeCountAsync(payment.Id));

        var afterFailure = await ObligationAsync(payment.Id);
        Assert.Equal(PaymentLedgerObligationStatus.Pending, afterFailure.Status);
        Assert.Equal(1, afterFailure.Attempts);
        Assert.Equal("SIMULATED_OUTAGE", afterFailure.LastErrorCode);
        Assert.Equal(PaymentLedgerFailureKind.Transient, afterFailure.FailureKind);
        Assert.NotNull(afterFailure.LastAttemptAt);
        Assert.True(afterFailure.NextAttemptAt > DateTime.UtcNow, "the retry must be backed off, not immediate");

        // Nothing has to survive in memory: a fresh processor (a restarted process) picks the row up.
        _context.ChangeTracker.Clear();
        await MakeDueAsync(payment.Id);

        var recovered = await Processor().ProcessAsync(payment.Id);

        Assert.Equal(LedgerProcessOutcome.Created, recovered.Outcome);
        Assert.Equal(1, await IncomeCountAsync(payment.Id));

        var completed = await ObligationAsync(payment.Id);
        Assert.Equal(PaymentLedgerObligationStatus.Completed, completed.Status);
        Assert.Equal(2, completed.Attempts);
        Assert.Null(completed.LastError);
    }


    [Fact]
    public async Task A_permanent_failure_is_parked_with_its_reason_and_never_retried_automatically()
    {
        var (business, _, customer, appointment) = await SeedAsync();
        var payment = await SeedSettledPaymentAsync(business, customer, appointment, 0m);

        await Outbox().EnsureObligationAsync(payment);
        var result = await Processor().ProcessAsync(payment.Id);

        Assert.Equal(LedgerProcessOutcome.FailedPermanently, result.Outcome);
        Assert.Equal("INVALID_AMOUNT", result.ErrorCode);
        Assert.Equal(0, await IncomeCountAsync(payment.Id));

        var obligation = await ObligationAsync(payment.Id);
        Assert.Equal(PaymentLedgerObligationStatus.Failed, obligation.Status);
        Assert.Equal(PaymentLedgerFailureKind.Permanent, obligation.FailureKind);
        Assert.NotNull(obligation.LastErrorCode);

        // It stays visible: the operator view finds it, and a retry does not invent income.
        var failed = await Outbox().ListAsync(new PaymentLedgerFilter(business.Id, OnlyProblems: true));
        Assert.Single(failed);
        Assert.Equal(payment.Id, failed[0].PaymentId);

        Assert.True(await Outbox().RequeueAsync(obligation.Id, business.Id));
        var retried = await Processor().ProcessAsync(payment.Id);
        Assert.Equal(LedgerProcessOutcome.FailedPermanently, retried.Outcome);
        Assert.Equal(0, await IncomeCountAsync(payment.Id));

        // Reconciliation must never silently resurrect a permanent failure.
        var reconciled = await Reconciliation().ReconcileAsync(100);
        Assert.Equal(0, reconciled.ObligationsRequeued);
        Assert.Equal(PaymentLedgerObligationStatus.Failed, (await ObligationAsync(payment.Id)).Status);
    }

    [Fact]
    public async Task Exhausting_the_retry_budget_parks_the_obligation_for_investigation()
    {
        var (business, _, customer, appointment) = await SeedAsync();
        var payment = await SeedSettledPaymentAsync(business, customer, appointment);

        await Outbox().EnsureObligationAsync(payment);

        var options = OptionsFor(maxAttempts: 2);
        var alwaysFailing = new ScriptedLedger(Ledger(), failures: 10);

        await Processor(alwaysFailing, options).ProcessAsync(payment.Id);
        await MakeDueAsync(payment.Id);
        var final = await Processor(alwaysFailing, options).ProcessAsync(payment.Id);

        Assert.Equal(LedgerProcessOutcome.FailedPermanently, final.Outcome);

        var obligation = await ObligationAsync(payment.Id);
        Assert.Equal(PaymentLedgerObligationStatus.Failed, obligation.Status);
        Assert.Equal(2, obligation.Attempts);
        Assert.Equal(PaymentLedgerFailureKind.Transient, obligation.FailureKind);
        Assert.Equal(0, await IncomeCountAsync(payment.Id));

        // A transient-exhausted obligation is recoverable: the reconciliation sweep gives it a fresh
        // budget, and the normal processor then writes the income. No operator action required.
        var reconciled = await Reconciliation().ReconcileAsync(100);
        Assert.Equal(1, reconciled.ObligationsRequeued);

        var recovered = await Processor().ProcessAsync(payment.Id);
        Assert.Equal(LedgerProcessOutcome.Created, recovered.Outcome);
        Assert.Equal(1, await IncomeCountAsync(payment.Id));
        Assert.Equal(PaymentLedgerObligationStatus.Completed, (await ObligationAsync(payment.Id)).Status);
    }


    // ── Reconciliation ─────────────────────────────────────────

    [Fact]
    public async Task Reconciliation_finds_a_settled_payment_with_no_income_and_restores_its_obligation()
    {
        var (business, _, customer, appointment) = await SeedAsync();
        var payment = await SeedSettledPaymentAsync(business, customer, appointment, 1000m);

        // The dangerous historical state: money settled, no obligation, no income.
        Assert.Empty(await _context.PaymentLedgerObligations.ToListAsync());

        var report = await Reconciliation().GetConsistencyReportAsync(null);
        Assert.Equal(1, report.SettledPaymentsWithoutIncome);
        Assert.Contains(report.Samples, s => s.Kind == "settled_without_income" && s.PaymentId == payment.Id);

        var reconciled = await Reconciliation().ReconcileAsync(100);

        Assert.Equal(1, reconciled.ObligationsCreated);
        var obligation = await ObligationAsync(payment.Id);
        Assert.Equal(PaymentLedgerObligationStatus.Pending, obligation.Status);
        Assert.Equal(0, await IncomeCountAsync(payment.Id));

        // The normal processing path creates the income — reconciliation itself writes no ledger row.
        var processed = await Processor().ProcessAsync(payment.Id);
        Assert.Equal(LedgerProcessOutcome.Created, processed.Outcome);
        Assert.Equal(1, await IncomeCountAsync(payment.Id));
        Assert.Equal(PaymentLedgerObligationStatus.Completed, (await ObligationAsync(payment.Id)).Status);
    }

    [Fact]
    public async Task Reconciliation_is_safe_to_repeat_and_never_duplicates_completed_work()
    {
        var (business, _, customer, appointment) = await SeedAsync();
        var payment = await SeedSettledPaymentAsync(business, customer, appointment);

        await Outbox().EnsureObligationAsync(payment);
        await Processor().ProcessAsync(payment.Id);

        var first = await Reconciliation().ReconcileAsync(100);
        var second = await Reconciliation().ReconcileAsync(100);

        Assert.Equal(0, first.ObligationsCreated);
        Assert.Equal(0, second.ObligationsCreated);
        Assert.Equal(1, await IncomeCountAsync(payment.Id));
        Assert.Equal(PaymentLedgerObligationStatus.Completed, (await ObligationAsync(payment.Id)).Status);
    }

    [Fact]
    public async Task Reconciliation_completes_an_obligation_whose_income_was_already_written()
    {
        var (business, _, customer, appointment) = await SeedAsync();
        var payment = await SeedSettledPaymentAsync(business, customer, appointment);

        await Outbox().EnsureObligationAsync(payment);

        // Simulate a crash after the income commit but before completion: write the income directly.
        await Ledger().EnsureIncomeForPaymentAsync(payment);
        Assert.Equal(PaymentLedgerObligationStatus.Pending, (await ObligationAsync(payment.Id)).Status);

        var reconciled = await Reconciliation().ReconcileAsync(100);

        Assert.Equal(1, reconciled.ObligationsCompleted);
        var obligation = await ObligationAsync(payment.Id);
        Assert.Equal(PaymentLedgerObligationStatus.Completed, obligation.Status);
        Assert.NotNull(obligation.FinancialTransactionId);
        Assert.Equal(1, await IncomeCountAsync(payment.Id));
    }


    [Fact]
    public async Task An_orphaned_income_row_is_reported_and_left_untouched()
    {
        var (business, _, _, _) = await SeedAsync();

        var orphan = new FinancialTransaction
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Type = FinancialTransactionType.Income,
            Source = TransactionSource.PunchedBooking,
            PaymentMethod = FinancialPaymentMethod.Cash,
            VerificationStatus = FinancialVerificationStatus.Verified,
            ReconciliationStatus = ReconciliationStatus.Unreconciled,
            Amount = 250m,
            Currency = "KES",
            Reference = "FIN-ORPHAN000001",
            SourcePaymentId = Guid.NewGuid(),
            TransactionDate = DateTime.UtcNow,
            RecordedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        _context.FinancialTransactions.Add(orphan);
        await _context.SaveChangesAsync();

        var report = await Reconciliation().GetConsistencyReportAsync(null);

        Assert.Equal(1, report.OrphanedIncomeRows);
        Assert.Contains(report.Samples, s => s.Kind == "orphaned_income" && s.Reference == orphan.Reference);

        // Reported, never deleted or rewritten.
        Assert.True(await _context.FinancialTransactions.AnyAsync(t => t.Id == orphan.Id));
    }

    // ── Claiming and leases ────────────────────────────────────

    [Fact]
    public async Task Claiming_a_batch_leases_it_so_a_second_worker_gets_nothing()
    {
        var (business, _, customer, appointment) = await SeedAsync();
        var first = await SeedSettledPaymentAsync(business, customer, appointment);
        var second = await SeedSettledPaymentAsync(business, customer, appointment, 400m);

        await Outbox().EnsureObligationAsync(first);
        await Outbox().EnsureObligationAsync(second);

        var batch = await Outbox().ClaimDueAsync(10);
        Assert.Equal(2, batch.Count);

        foreach (var claim in batch)
        {
            var leased = await _context.PaymentLedgerObligations.AsNoTracking().SingleAsync(o => o.Id == claim.ObligationId);
            Assert.Equal(PaymentLedgerObligationStatus.Processing, leased.Status);
        }

        // The second worker finds no due work — this is the guard that makes two instances safe.
        Assert.Empty(await Outbox().ClaimDueAsync(10));
    }

    [Fact]
    public async Task A_crashed_workers_lease_is_reclaimed_after_the_stale_window()
    {
        var (business, _, customer, appointment) = await SeedAsync();
        var payment = await SeedSettledPaymentAsync(business, customer, appointment);
        await Outbox().EnsureObligationAsync(payment);

        Assert.Single(await Outbox().ClaimDueAsync(10));

        await _context.PaymentLedgerObligations
            .Where(o => o.PaymentId == payment.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.LockedAt, DateTime.UtcNow.AddMinutes(-30)));

        Assert.Equal(1, await Outbox().ReclaimStaleAsync(TimeSpan.FromMinutes(3)));
        Assert.Single(await Outbox().ClaimDueAsync(10));
    }


    // ── Worker, tenancy and business-facing status ─────────────

    private ServiceProvider BuildWorkerProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IFinancialLedgerService, FinancialLedgerService>();
        services.AddScoped<IPaymentLedgerOutbox, PaymentLedgerOutbox>();
        services.AddScoped<IPaymentLedgerProcessor, PaymentLedgerProcessor>();
        services.AddScoped<IPaymentLedgerReconciliationService, PaymentLedgerReconciliationService>();
        services.Configure<PaymentLedgerOptions>(options =>
        {
            options.BatchSize = 10;
            options.ReconciliationBatchSize = 50;
        });

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_worker_processes_due_obligations_and_does_not_repeat_completed_ones()
    {
        var (business, _, customer, appointment) = await SeedAsync();
        var first = await SeedSettledPaymentAsync(business, customer, appointment);
        var second = await SeedSettledPaymentAsync(business, customer, appointment, 300m);

        await Outbox().EnsureObligationAsync(first);
        await Outbox().EnsureObligationAsync(second);

        using var provider = BuildWorkerProvider();
        var worker = new PaymentLedgerWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new PaymentLedgerOptions { BatchSize = 10 }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentLedgerWorker>.Instance);

        var batch = await worker.ProcessBatchAsync();

        Assert.Equal(2, batch.Claimed);
        Assert.Equal(2, batch.Created);
        Assert.Equal(1, await IncomeCountAsync(first.Id));
        Assert.Equal(1, await IncomeCountAsync(second.Id));

        var repeat = await worker.ProcessBatchAsync();
        Assert.Equal(0, repeat.Claimed);
        Assert.Equal(2, await _context.FinancialTransactions.CountAsync(t => t.SourcePaymentId != null));
    }

    [Fact]
    public async Task Operational_views_are_scoped_to_one_business()
    {
        var (business, owner, customer, appointment) = await SeedAsync();
        var payment = await SeedSettledPaymentAsync(business, customer, appointment);
        await Outbox().EnsureObligationAsync(payment);

        var otherOwner = BookingTestBase.CreateOwner("owner2@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id);
        var otherCustomer = BookingTestBase.CreateCustomer("customer2@test.com");
        var otherAppointment = new Appointment
        {
            Id = Guid.NewGuid(),
            BusinessId = otherBusiness.Id,
            CustomerId = otherCustomer.Id,
            StaffUserId = otherOwner.Id,
            ScheduledAt = DateTime.UtcNow.AddHours(2),
            EndAt = DateTime.UtcNow.AddHours(3),
            Status = "booked",
            CreatedAt = DateTime.UtcNow
        };
        await BookingTestBase.SeedAsync(_context, otherOwner, otherBusiness, otherCustomer, otherAppointment);
        var otherPayment = await SeedSettledPaymentAsync(otherBusiness, otherCustomer, otherAppointment, 700m);
        await Outbox().EnsureObligationAsync(otherPayment);

        var scoped = await Outbox().ListAsync(new PaymentLedgerFilter(business.Id));

        Assert.Single(scoped);
        Assert.All(scoped, view => Assert.Equal(business.Id, view.BusinessId));
        Assert.Equal(1, await Outbox().CountAsync(new PaymentLedgerFilter(business.Id)));
        Assert.Equal(2, await Outbox().CountAsync(new PaymentLedgerFilter(null)));
    }

    [Fact]
    public async Task The_business_facing_payment_status_distinguishes_settlement_from_bookkeeping()
    {
        var (business, owner, customer, appointment) = await SeedAsync();
        var payment = await SeedSettledPaymentAsync(business, customer, appointment);
        var payments = PaymentsService();

        // Settled, obligation still due → processing, never "recorded".
        await Outbox().EnsureObligationAsync(payment);
        var pending = await payments.GetPaymentAsync(payment.Id, owner.Id, "Business");
        Assert.True(pending.Success, pending.Error?.Message);
        Assert.Equal("processing", pending.Data!.LedgerProcessingStatus);
        Assert.Null(pending.Data.LedgerReference);

        // Recorded.
        await Processor().ProcessAsync(payment.Id);
        var recorded = await payments.GetPaymentAsync(payment.Id, owner.Id, "Business");
        Assert.Equal("recorded", recorded.Data!.LedgerProcessingStatus);
        Assert.NotNull(recorded.Data.LedgerReference);

        // A settled payment with no obligation at all is still "processing": the reconciler will
        // restore the obligation, so the money is not lost.
        var unqueued = await SeedSettledPaymentAsync(business, customer, appointment, 250m);
        var missing = await payments.GetPaymentAsync(unqueued.Id, owner.Id, "Business");
        Assert.Equal("processing", missing.Data!.LedgerProcessingStatus);
    }


    // ── Refunds, voids and the manual-approval path ────────────

    private FinancialService Finances() =>
        new(_context, new UnitOfWork(_context), TestHelpers.CreateLogger<FinancialService>());

    private PunchedApi.Application.Services.FinancialAnalyticsService Analytics() =>
        new(_context, TestHelpers.CreateLogger<PunchedApi.Application.Services.FinancialAnalyticsService>());

    private PaymentService PaymentsService(ICommissionService? commissions = null) =>
        new(
            _context,
            new UnitOfWork(_context),
            new IPaymentProvider[] { new CashPaymentProvider(TestHelpers.CreateLogger<CashPaymentProvider>()) },
            new PermissionService(),
            new PaymentCredentialProtector(
                Microsoft.Extensions.Options.Options.Create(new PaymentOptions()),
                DevelopmentEnvironment()),
            Microsoft.Extensions.Options.Options.Create(new PaymentOptions()),
            BookingTestBase.CreateNotificationService(_context),
            TestHelpers.CreateLogger<PaymentService>(),
            commissions: commissions,
            ledger: Ledger(),
            ledgerOutbox: Outbox(),
            ledgerProcessor: Processor());

    private static Microsoft.Extensions.Hosting.IHostEnvironment DevelopmentEnvironment()
    {
        var env = new Mock<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Development");
        return env.Object;
    }

    [Fact]
    public async Task Income_that_came_from_a_payment_cannot_be_voided_by_hand()
    {
        var (business, owner, customer, appointment) = await SeedAsync();
        var payment = await SeedSettledPaymentAsync(business, customer, appointment);
        await Outbox().EnsureObligationAsync(payment);
        var processed = await Processor().ProcessAsync(payment.Id);
        var incomeId = processed.FinancialTransactionId!.Value;

        var attempt = await Finances().VoidTransactionAsync(
            incomeId, new VoidFinancialRecordRequest { Reason = "typo" }, owner.Id, "Business");

        Assert.False(attempt.Success);
        Assert.Equal("INVALID_STATUS_TRANSITION", attempt.Error?.Code);

        var income = await _context.FinancialTransactions.AsNoTracking().SingleAsync(t => t.Id == incomeId);
        Assert.False(income.IsVoided);
        Assert.Null(income.VoidReason);

        // The reversal path is the only truthful way to take payment income back out.
        var cashbook = await Analytics().GetCashbookAsync(
            new FinancialTransactionQuery { PageSize = 20 }, owner.Id, "Business");
        Assert.Equal(1000m, cashbook.Data!.TotalInflows);
    }

    [Fact]
    public async Task Manually_recorded_income_can_be_voided_and_leaves_the_cashbook_immediately()
    {
        var (_, owner, _, _) = await SeedAsync();

        var recorded = await Finances().RecordIncomeAsync(new RecordIncomeRequest
        {
            Amount = 500m,
            Source = "walk_in",
            PaymentMethod = "cash",
            Description = "Walk-in haircut"
        }, owner.Id, "Business");

        Assert.True(recorded.Success, recorded.Error?.Message);

        var before = await Analytics().GetCashbookAsync(
            new FinancialTransactionQuery { PageSize = 20 }, owner.Id, "Business");
        Assert.Equal(500m, before.Data!.TotalInflows);

        var voided = await Finances().VoidTransactionAsync(
            recorded.Data!.Id, new VoidFinancialRecordRequest { Reason = "Recorded against the wrong day" },
            owner.Id, "Business");

        Assert.True(voided.Success, voided.Error?.Message);
        Assert.True(voided.Data!.IsVoided);
        Assert.Equal("Recorded against the wrong day", voided.Data.VoidReason);

        // Repeating the void is idempotent and keeps the FIRST reason.
        var repeated = await Finances().VoidTransactionAsync(
            recorded.Data.Id, new VoidFinancialRecordRequest { Reason = "a different reason" },
            owner.Id, "Business");
        Assert.True(repeated.Success);
        Assert.Equal("Recorded against the wrong day", repeated.Data!.VoidReason);

        var after = await Analytics().GetCashbookAsync(
            new FinancialTransactionQuery { PageSize = 20 }, owner.Id, "Business");
        Assert.Equal(0m, after.Data!.TotalInflows);
        Assert.Equal(0m, after.Data.WindowNetCashMovement);

        // The row stays visible (a voided movement is history), but it no longer moves the book.
        var entry = Assert.Single(after.Data.Entries);
        Assert.True(entry.IsVoided);

        // The row is still on file, with the audit trail that says who voided it and why.
        var stored = await _context.FinancialTransactions.AsNoTracking().SingleAsync(t => t.Id == recorded.Data.Id);
        Assert.True(stored.IsVoided);
        Assert.Equal(owner.Id, stored.VoidedByUserId);
        Assert.Contains(await _context.FinancialAuditLogs.Where(a => a.EntityId == stored.Id).ToListAsync(),
            a => a.Action == "voided");
    }


    [Fact]
    public async Task A_reversal_records_one_refund_and_never_touches_the_original_income()
    {
        var (business, _, customer, appointment) = await SeedAsync();
        var payment = await SeedSettledPaymentAsync(business, customer, appointment, 800m);
        await Outbox().EnsureObligationAsync(payment);
        var processed = await Processor().ProcessAsync(payment.Id);

        payment.Status = PaymentStatus.Reversed;
        payment.ReversedAt = DateTime.UtcNow;
        payment.ReversalReason = "Customer cancelled";
        await _context.SaveChangesAsync();

        var first = await Ledger().EnsureRefundForReversedPaymentAsync(payment, "Customer cancelled");
        var second = await Ledger().EnsureRefundForReversedPaymentAsync(payment, "Customer cancelled");

        Assert.NotNull(first);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(1, await _context.FinancialTransactions.CountAsync(t => t.Type == FinancialTransactionType.Refund));

        var income = await _context.FinancialTransactions.AsNoTracking().SingleAsync(t => t.SourcePaymentId == payment.Id);
        Assert.Equal(FinancialTransactionType.Income, income.Type);
        Assert.Equal(processed.FinancialTransactionId, income.Id);
        Assert.False(income.IsVoided);
        Assert.Equal(800m, income.Amount);

        // The obligation is historical fact: a refund does not reopen the income work.
        var obligation = await ObligationAsync(payment.Id);
        Assert.Equal(PaymentLedgerObligationStatus.Completed, obligation.Status);

        // And a reversed payment does not produce another income row when reconciliation runs.
        await Reconciliation().ReconcileAsync(100);
        Assert.Equal(1, await IncomeCountAsync(payment.Id));
    }


    /// <summary>Seeds a manual M-Pesa booking, creates its payment and submits a claim message.</summary>
    private async Task<(User Owner, User Customer, Appointment Appointment, Guid PaymentId)> SeedSubmittedClaimAsync(
        PaymentService service)
    {
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var customer = BookingTestBase.CreateCustomer();

        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            StaffUserId = owner.Id,
            ScheduledAt = DateTime.UtcNow.AddHours(2),
            EndAt = DateTime.UtcNow.AddHours(3),
            Status = AppointmentStatuses.PendingPayment,
            CreatedAt = DateTime.UtcNow
        };
        appointment.Resources.Add(new AppointmentResource
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            ServiceCatalogItemId = Guid.NewGuid(),
            Name = "Haircut",
            DurationMinutes = 30,
            Price = 1000m,
            CreatedAt = DateTime.UtcNow
        });

        var config = new BusinessPaymentConfig
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CashEnabled = true,
            MpesaEnabled = false,
            ManualMpesaEnabled = true,
            MpesaPaybillNumber = "247247",
            MpesaPaybillAccount = "12345",
            MpesaPayeeName = "NURU STUDIO",
            BookingFeePercentage = 50m,
            CreatedAt = DateTime.UtcNow
        };

        var enrollment = new CustomerBusinessEnrollment
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            BusinessId = business.Id,
            Status = CustomerBusinessEnrollmentStatus.Active,
            Source = "discovery",
            EnrolledAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await BookingTestBase.SeedAsync(_context, owner, business, customer, appointment, config, enrollment);

        var created = await service.CreatePaymentAsync(
            new CreatePaymentRequest { AppointmentId = appointment.Id, Method = "mpesa", Type = "booking_fee" },
            customer.Id, "Customer");
        Assert.True(created.Success, created.Error?.Message);

        var submitted = await service.SubmitMpesaMessageAsync(
            created.Data!.Id,
            new SubmitMpesaMessageRequest
            {
                Message = "QJK42NUM77 Confirmed. Ksh500.00 sent to NURU STUDIO for account 12345 " +
                          "on 12/5/26 at 3:04 PM. New M-PESA balance is Ksh500.00."
            },
            customer.Id, "Customer");

        Assert.True(submitted.Success, submitted.Error?.Message);

        _context.ChangeTracker.Clear();
        return (owner, customer, appointment, created.Data.Id);
    }

    [Fact]
    public async Task Approving_a_claim_settles_it_and_writes_exactly_one_income_entry()
    {
        var commissions = new Mock<ICommissionService>();
        commissions
            .Setup(c => c.SettleForAppointmentAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var service = PaymentsService(commissions.Object);
        var (owner, _, appointment, paymentId) = await SeedSubmittedClaimAsync(service);

        var approved = await service.VerifyPaymentAsync(
            paymentId, new VerifyPaymentRequest { Note = "Seen on the till statement." }, owner.Id, "Business");

        Assert.True(approved.Success, approved.Error?.Message);
        Assert.Equal("verified", approved.Data!.Status);

        // The books: one income row, the settlement amount, and a completed obligation.
        var income = await _context.FinancialTransactions.SingleAsync(t => t.SourcePaymentId == paymentId);
        Assert.Equal(FinancialVerificationStatus.Verified, income.VerificationStatus);
        Assert.Equal(approved.Data.Amount, income.Amount);
        Assert.Equal("KES", income.Currency);
        Assert.Equal(appointment.Id, income.AppointmentId);

        var obligation = await ObligationAsync(paymentId);
        Assert.Equal(PaymentLedgerObligationStatus.Completed, obligation.Status);
        Assert.Equal(income.Id, obligation.FinancialTransactionId);

        // Replaying the approval is idempotent in every dimension that matters.
        var again = await service.VerifyPaymentAsync(paymentId, new VerifyPaymentRequest(), owner.Id, "Business");
        Assert.True(again.Success);

        Assert.Equal(1, await IncomeCountAsync(paymentId));
        Assert.Equal(1, await _context.PaymentLedgerObligations.CountAsync(o => o.PaymentId == paymentId));

        commissions.Verify(
            c => c.SettleForAppointmentAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_rejected_claim_creates_no_obligation_and_no_income()
    {
        var service = PaymentsService();
        var (owner, _, _, paymentId) = await SeedSubmittedClaimAsync(service);

        var rejected = await service.RejectPaymentAsync(
            paymentId, new RejectPaymentRequest { Reason = "Not on the statement" }, owner.Id, "Business");

        Assert.True(rejected.Success, rejected.Error?.Message);
        Assert.Equal(0, await _context.PaymentLedgerObligations.CountAsync(o => o.PaymentId == paymentId));
        Assert.Equal(0, await IncomeCountAsync(paymentId));
    }
}

