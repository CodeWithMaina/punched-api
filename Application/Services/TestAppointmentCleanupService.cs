using Microsoft.EntityFrameworkCore;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Data.Seeding;

namespace PunchedApi.Application.Services;

/// <summary>
/// Identifies and (when explicitly asked to apply) removes appointments that a demo
/// seeder or a verification script wrote for one business.
///
/// Three origins are recognised, and only these:
/// <list type="bullet">
/// <item><b>Demo book</b> — the id is one the Aurelia activity seeder can produce
/// (<c>DeterministicSeed.GuidFor(scope, "appointment:{index:D5}")</c>).</item>
/// <item><b>Verification booking</b> — the appointment's status history carries the
/// verification marker note written by <c>verify-aurelia-demo.ps1</c>.</item>
/// <item><b>Automation booking</b> — the appointment note starts with a known
/// automation prefix (for example <c>CLINE VERIFY</c>).</item>
/// </list>
///
/// Every other row is retained and reported as such, so a record whose origin cannot be
/// established is never destroyed. Deletion is tenant-scoped and id-scoped end to end:
/// no predicate keys off dates, customer names, statuses or "all rows".
/// </summary>
public interface ITestAppointmentCleanupService
{
    Task<TestAppointmentCleanupReport> ExecuteAsync(
        TestAppointmentCleanupRequest request, CancellationToken cancellationToken = default);
}

public sealed record TestAppointmentCleanupRequest(
    Guid BusinessId,
    bool Apply = false,
    string SeedScope = "aurelia-demo",
    int MaxSeedIndex = 5000,
    string VerificationMarker = "Seeded verification booking.",
    IReadOnlyList<string>? AutomationNotePrefixes = null,
    IReadOnlyList<string>? DemoCustomerEmailDomains = null);

public sealed record TestAppointmentCleanupReport(
    Guid BusinessId,
    string BusinessName,
    bool Applied,
    int TotalAppointments,
    int SeededAppointments,
    int VerificationAppointments,
    int AutomationAppointments,
    int SyntheticIdentityAppointments,
    int RetainedAppointments,
    IReadOnlyDictionary<string, int> RemovedRows,
    IReadOnlyList<string> SampleAppointmentIds,
    IReadOnlyDictionary<string, int> RemainingByStatus,
    IReadOnlyList<string> Warnings);

public sealed class TestAppointmentCleanupService : ITestAppointmentCleanupService
{
    private static readonly string[] DefaultAutomationPrefixes = ["CLINE VERIFY"];

    private readonly ApplicationDbContext _db;
    private readonly ILogger<TestAppointmentCleanupService> _logger;

    public TestAppointmentCleanupService(ApplicationDbContext db, ILogger<TestAppointmentCleanupService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<TestAppointmentCleanupReport> ExecuteAsync(
        TestAppointmentCleanupRequest request, CancellationToken cancellationToken = default)
    {
        var business = await _db.Businesses
            .Where(b => b.Id == request.BusinessId)
            .Select(b => new { b.Id, b.Name })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Business {request.BusinessId} was not found.");

        _db.ChangeTracker.Clear();

        // ── 1. Classify the tenant's whole book (id + note + terminal markers only) ──
        var seedIds = SeedAppointmentIds(request.SeedScope, request.MaxSeedIndex);
        var prefixes = (request.AutomationNotePrefixes ?? DefaultAutomationPrefixes)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();

        var appointments = await _db.Appointments
            .Where(a => a.BusinessId == request.BusinessId)
            .Select(a => new { a.Id, a.CustomerId, a.Status, a.Note })
            .ToListAsync(cancellationToken);

        var verificationIds = await _db.AppointmentStatusHistory
            .Where(h => h.Note == request.VerificationMarker
                        && _db.Appointments.Any(a => a.Id == h.AppointmentId && a.BusinessId == request.BusinessId))
            .Select(h => h.AppointmentId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Synthetic identities: the demo seeder creates its customers on a reserved
        // email domain. A booking on such an address cannot be a real customer, which is
        // what makes "booked by test automation through the customer app" provable
        // rather than a guess.
        var reservedDomains = (request.DemoCustomerEmailDomains ?? ["demo.punched.app"])
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim().TrimStart('@'))
            .ToArray();

        var customerIds = appointments.Select(a => a.CustomerId).Distinct().ToList();
        var syntheticCustomerIds = new HashSet<Guid>();
        if (reservedDomains.Length > 0 && customerIds.Count > 0)
        {
            var candidates = await _db.Users
                .Where(u => customerIds.Contains(u.Id) && u.Email != null)
                .Select(u => new { u.Id, u.Email })
                .ToListAsync(cancellationToken);

            foreach (var candidate in candidates)
            {
                if (reservedDomains.Any(d =>
                        candidate.Email!.EndsWith("@" + d, StringComparison.OrdinalIgnoreCase)))
                {
                    syntheticCustomerIds.Add(candidate.Id);
                }
            }
        }

        var verificationSet = verificationIds.ToHashSet();
        var seeded = new List<Guid>();
        var verification = new List<Guid>();
        var automation = new List<Guid>();
        var synthetic = new List<Guid>();

        foreach (var appointment in appointments)
        {
            if (seedIds.Contains(appointment.Id))
            {
                seeded.Add(appointment.Id);
            }
            else if (verificationSet.Contains(appointment.Id))
            {
                verification.Add(appointment.Id);
            }
            else if (appointment.Note is { } note
                     && prefixes.Any(p => note.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                automation.Add(appointment.Id);
            }
            else if (syntheticCustomerIds.Contains(appointment.CustomerId))
            {
                synthetic.Add(appointment.Id);
            }
        }

        var targetIds = seeded.Concat(verification).Concat(automation).Concat(synthetic).Distinct().ToList();
        var retained = appointments.Count - targetIds.Count;

        var removed = new Dictionary<string, int>(StringComparer.Ordinal);
        var warnings = new List<string>();

        if (targetIds.Count > 0 && request.Apply)
        {
            removed = await DeleteAsync(request.BusinessId, targetIds, cancellationToken);
            _logger.LogInformation(
                "Purged {Appointments} test appointments for business {BusinessId}: {@Removed}",
                targetIds.Count, request.BusinessId, removed);
        }

        // ── 2. Report what the tenant still holds, so the operator can confirm ──
        var remaining = await _db.Appointments
            .Where(a => a.BusinessId == request.BusinessId)
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        if (!request.Apply && targetIds.Count > 0)
        {
            warnings.Add("Dry run: nothing was deleted. Re-run with --apply to remove the identified rows.");
        }

        return new TestAppointmentCleanupReport(
            business.Id,
            business.Name,
            request.Apply,
            appointments.Count,
            seeded.Count,
            verification.Count,
            automation.Count,
            synthetic.Count,
            retained,
            removed,
            targetIds.Take(10).Select(id => id.ToString("D")).ToList(),
            remaining.ToDictionary(r => r.Status, r => r.Count, StringComparer.Ordinal),
            warnings);
    }

    /// <summary>
    /// Deletes the identified appointments and their dependent rows in FK order.
    /// Every statement is scoped to the business <i>and</i> to the identified ids.
    /// </summary>
    private async Task<Dictionary<string, int>> DeleteAsync(
        Guid businessId, IReadOnlyList<Guid> appointmentIds, CancellationToken cancellationToken)
    {
        var removed = new Dictionary<string, int>(StringComparer.Ordinal);

        void Record(string table, int count)
        {
            if (count > 0)
            {
                removed[table] = count;
            }
        }

        var paymentIds = await _db.Payments
            .Where(p => p.BusinessId == businessId && p.AppointmentId != null
                        && appointmentIds.Contains(p.AppointmentId.Value))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        var ledgerIds = await _db.FinancialTransactions
            .Where(t => t.BusinessId == businessId
                        && ((t.AppointmentId != null && appointmentIds.Contains(t.AppointmentId.Value))
                            || (t.SourcePaymentId != null && paymentIds.Contains(t.SourcePaymentId.Value))))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // Restrict first: a review would block the appointment delete, and a job or
            // notification would outlive the booking it points at.
            Record("reviews", await _db.Reviews
                .Where(r => r.BusinessId == businessId && appointmentIds.Contains(r.AppointmentId))
                .ExecuteDeleteAsync(cancellationToken));

            Record("scheduled_jobs", await _db.ScheduledJobs
                .Where(j => j.BusinessId == businessId && j.AggregateId != null
                            && appointmentIds.Contains(j.AggregateId.Value))
                .ExecuteDeleteAsync(cancellationToken));

            Record("notifications", await _db.Notifications
                .Where(n => n.BusinessId == businessId && n.AppointmentId != null
                            && appointmentIds.Contains(n.AppointmentId.Value))
                .ExecuteDeleteAsync(cancellationToken));

            Record("financial_transactions_refunds", await _db.FinancialTransactions
                .Where(t => t.RefundOfTransactionId != null && ledgerIds.Contains(t.RefundOfTransactionId.Value))
                .ExecuteDeleteAsync(cancellationToken));

            Record("financial_transactions", await _db.FinancialTransactions
                .Where(t => t.BusinessId == businessId && ledgerIds.Contains(t.Id))
                .ExecuteDeleteAsync(cancellationToken));

            Record("payment_callbacks", await _db.PaymentCallbacks
                .Where(c => c.PaymentId != null && paymentIds.Contains(c.PaymentId.Value))
                .ExecuteDeleteAsync(cancellationToken));

            Record("payments", await _db.Payments
                .Where(p => p.BusinessId == businessId && paymentIds.Contains(p.Id))
                .ExecuteDeleteAsync(cancellationToken));

            Record("commission_entries", await _db.CommissionEntries
                .Where(c => c.BusinessId == businessId && c.AppointmentId != null
                            && appointmentIds.Contains(c.AppointmentId.Value))
                .ExecuteDeleteAsync(cancellationToken));

            Record("reschedule_requests", await _db.RescheduleRequests
                .Where(r => appointmentIds.Contains(r.AppointmentId))
                .ExecuteDeleteAsync(cancellationToken));

            Record("appointment_status_history", await _db.AppointmentStatusHistory
                .Where(h => appointmentIds.Contains(h.AppointmentId))
                .ExecuteDeleteAsync(cancellationToken));

            Record("appointment_resources", await _db.AppointmentResources
                .Where(r => appointmentIds.Contains(r.AppointmentId))
                .ExecuteDeleteAsync(cancellationToken));

            Record("appointments", await _db.Appointments
                .Where(a => a.BusinessId == businessId && appointmentIds.Contains(a.Id))
                .ExecuteDeleteAsync(cancellationToken));

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return removed;
    }

    /// <summary>
    /// The ids the demo seeder can produce for appointments: the same deterministic
    /// factory (<see cref="DeterministicSeed"/>) the seeder uses, so the marker is the
    /// id itself rather than a guess.
    /// </summary>
    private static HashSet<Guid> SeedAppointmentIds(string scope, int maxIndex)
    {
        var ids = new HashSet<Guid>();
        for (var index = 1; index <= Math.Max(1, maxIndex); index++)
        {
            ids.Add(DeterministicSeed.GuidFor(scope, $"appointment:{index:D5}"));
        }

        return ids;
    }
}
