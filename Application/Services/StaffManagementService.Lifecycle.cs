using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

/// <summary>
/// Leaving-the-team workflow: what a deactivation would leave behind, and moving
/// that work to a qualified colleague.
///
/// <para><b>Nothing is destroyed.</b> There is no delete path — a deactivation
/// suspends a membership, and every historical appointment, payment, review and
/// attendance event keeps pointing at the person who actually did the work. The
/// reassignment below only ever moves <b>future, non-terminal</b> appointments to
/// another member of the same business; completed history is never re-attributed,
/// because that would falsify the financial record.</para>
///
/// <para><b>Every rule is re-checked server-side.</b> The caller cannot name a
/// colleague from another tenant, an inactive one, one who is not qualified for
/// the service, or a slot that would double-book them. What cannot be moved comes
/// back with a reason — nothing is silently dropped.</para>
/// </summary>
public partial class StaffManagementService
{
    /// <summary>
    /// Appointments that still hold somebody's time. Terminal states
    /// (completed / cancelled / no-show) are history and are never moved.
    /// </summary>
    private static readonly string[] OpenAppointmentStatuses =
        [AppointmentStatuses.Booked, AppointmentStatuses.Confirmed, AppointmentStatuses.PendingPayment];

    /// <summary>Payment states that are waiting on a human decision.</summary>
    private static readonly PaymentStatus[] AwaitingDecisionStatuses =
        [PaymentStatus.Submitted, PaymentStatus.UnderReview, PaymentStatus.Flagged];

    private static readonly PaymentStatus[] SettledPaymentStatuses =
        [PaymentStatus.Success, PaymentStatus.Verified];

    /// <summary>
    /// Upper bound on the distinct service combinations probed when ranking
    /// reassignment candidates. A real diary has a handful; the cap keeps a
    /// pathological one from turning a single request into hundreds of queries.
    /// </summary>
    private const int MaxEligibilityProbes = 10;

    /// <inheritdoc />
    public async Task<ApiResponse<StaffDeactivationImpactResponse>> GetDeactivationImpactAsync(
        Guid callerUserId, string role, Guid staffUserId, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveStaffAsync(callerUserId, role, staffUserId);
        if (resolved == null)
            return ApiResponse<StaffDeactivationImpactResponse>.Fail(
                "NOT_FOUND", "Staff member not found in your business.");

        var (businessId, membership, user) = resolved.Value;

        if (!await HasPermissionAsync(callerUserId, businessId, "staff.view"))
            return ApiResponse<StaffDeactivationImpactResponse>.Fail(
                "FORBIDDEN", "You are not allowed to view staff.");

        var now = DateTime.UtcNow;

        var futureAppointments = await _context.Appointments.AsNoTracking()
            .Where(a => a.BusinessId == businessId
                && a.StaffUserId == staffUserId
                && OpenAppointmentStatuses.Contains(a.Status)
                && a.ScheduledAt >= now)
            .OrderBy(a => a.ScheduledAt)
            .Select(a => new { a.Id, a.ScheduledAt, a.Status })
            .ToListAsync(cancellationToken);

        var nextAppointments = new List<StaffImpactAppointmentResponse>();
        if (futureAppointments.Count > 0)
        {
            var preview = futureAppointments.Take(5).ToList();
            var previewIds = preview.Select(a => a.Id).ToList();

            var previewRows = await _context.Appointments.AsNoTracking()
                .Where(a => previewIds.Contains(a.Id))
                .Select(a => new
                {
                    a.Id,
                    a.CustomerId,
                    // Summed as double because SQLite cannot aggregate decimal in SQL; the
                    // result is cast back and rounded to money before it is reported.
                    Value = (decimal)_context.AppointmentResources
                        .Where(r => r.AppointmentId == a.Id)
                        .Sum(r => (double)r.Price)
                })
                .ToListAsync(cancellationToken);

            var rowById = previewRows.ToDictionary(r => r.Id);
            var customerIds = previewRows.Select(r => r.CustomerId).Distinct().ToList();

            var customerNames = customerIds.Count == 0
                ? new Dictionary<Guid, string>()
                : await _context.Users.AsNoTracking()
                    .Where(u => customerIds.Contains(u.Id))
                    .Select(u => new { u.Id, u.FullName })
                    .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

            foreach (var appointment in preview)
            {
                rowById.TryGetValue(appointment.Id, out var row);
                nextAppointments.Add(new StaffImpactAppointmentResponse
                {
                    AppointmentId = appointment.Id,
                    ScheduledAt = appointment.ScheduledAt,
                    Status = appointment.Status,
                    CustomerName = row != null && customerNames.TryGetValue(row.CustomerId, out var name)
                        ? name
                        : null,
                    ServiceValue = Math.Round(row?.Value ?? 0m, 2)
                });
            }
        }

        var unresolvedPast = await _context.Appointments.AsNoTracking()
            .CountAsync(a => a.BusinessId == businessId
                && a.StaffUserId == staffUserId
                && OpenAppointmentStatuses.Contains(a.Status)
                && a.ScheduledAt < now, cancellationToken);

        // Money waiting on a decision, attributed through the appointment it settles —
        // the same attribution rule the financial report uses.
        var pendingPayments = await _context.Payments.AsNoTracking()
            .Where(p => p.BusinessId == businessId
                && p.AppointmentId != null
                && AwaitingDecisionStatuses.Contains(p.Status)
                && _context.Appointments.Any(a =>
                    a.Id == p.AppointmentId!.Value
                    && a.BusinessId == businessId
                    && a.StaffUserId == staffUserId))
            .GroupBy(p => p.BusinessId)
            .Select(g => new
            {
                Count = g.Count(),
                // Summed as double because SQLite cannot aggregate decimal in SQL; every
                // reported figure is rounded back to money.
                Amount = (decimal)g.Sum(p => (double)p.Amount)
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Outstanding balance on completed work: the booked value of the service lines
        // minus what has actually settled. Cancelled and no-show visits are never debt.
        // Both sums are cast through double so the same statement runs on SQLite and
        // PostgreSQL; the result is money again after rounding.
        var completedValue = (decimal)await _context.AppointmentResources.AsNoTracking()
            .Where(r => _context.Appointments.Any(a =>
                a.Id == r.AppointmentId
                && a.BusinessId == businessId
                && a.StaffUserId == staffUserId
                && a.Status == AppointmentStatuses.Completed))
            .SumAsync(r => (double)r.Price, cancellationToken);

        var collectedValue = (decimal)await _context.Payments.AsNoTracking()
            .Where(p => p.BusinessId == businessId
                && p.AppointmentId != null
                && SettledPaymentStatuses.Contains(p.Status)
                && _context.Appointments.Any(a =>
                    a.Id == p.AppointmentId!.Value
                    && a.BusinessId == businessId
                    && a.StaffUserId == staffUserId
                    && a.Status == AppointmentStatuses.Completed))
            .SumAsync(p => (double)p.Amount, cancellationToken);

        var isClockedIn = await _context.AttendanceSessions.AsNoTracking()
            .AnyAsync(s => s.BusinessId == businessId
                && s.StaffUserId == staffUserId
                && s.Status == AttendanceSessionStatus.Open, cancellationToken);

        var response = new StaffDeactivationImpactResponse
        {
            UserId = staffUserId,
            FullName = user.FullName,
            IsActive = membership.Status == BusinessMembershipStatus.Active,
            MembershipStatus = membership.Status.ToString(),
            CanDeactivate = membership.Role != BusinessMembershipRole.Owner,
            FutureAppointmentCount = futureAppointments.Count,
            NextAppointments = nextAppointments,
            UnresolvedPastAppointmentCount = unresolvedPast,
            PendingVerificationCount = pendingPayments?.Count ?? 0,
            PendingVerificationAmount = Math.Round(pendingPayments?.Amount ?? 0m, 2),
            OutstandingAmount = Math.Round(Math.Max(0m, completedValue - collectedValue), 2),
            IsClockedIn = isClockedIn,
            ReassignmentCandidates = await RankReassignmentCandidatesAsync(
                businessId, staffUserId, cancellationToken)
        };

        if (!response.CanDeactivate) response.Warnings.Add("owner_cannot_be_deactivated");
        if (response.FutureAppointmentCount > 0) response.Warnings.Add("future_appointments");
        if (response.UnresolvedPastAppointmentCount > 0) response.Warnings.Add("unresolved_past_appointments");
        if (response.PendingVerificationCount > 0) response.Warnings.Add("pending_payment_verification");
        if (response.OutstandingAmount > 0) response.Warnings.Add("outstanding_balance");
        if (response.IsClockedIn) response.Warnings.Add("still_clocked_in");
        if (response.FutureAppointmentCount > 0 && response.ReassignmentCandidates.Count == 0)
            response.Warnings.Add("no_reassignment_candidate");

        return ApiResponse<StaffDeactivationImpactResponse>.Ok(response);
    }
}

/// <summary>Reassignment: moving future work from one member to another.</summary>
public partial class StaffManagementService
{
    /// <inheritdoc />
    public async Task<ApiResponse<StaffReassignmentResponse>> ReassignAppointmentsAsync(
        Guid callerUserId, string role, Guid staffUserId,
        ReassignStaffAppointmentsRequest request, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveStaffAsync(callerUserId, role, staffUserId);
        if (resolved == null)
            return ApiResponse<StaffReassignmentResponse>.Fail(
                "NOT_FOUND", "Staff member not found in your business.");

        var (businessId, _, sourceUser) = resolved.Value;

        if (!await HasPermissionAsync(callerUserId, businessId, "staff.manage"))
            return ApiResponse<StaffReassignmentResponse>.Fail(
                "FORBIDDEN", "You are not allowed to manage staff.");

        var targetId = request.TargetStaffUserId;
        if (targetId == Guid.Empty || targetId == staffUserId)
            return ApiResponse<StaffReassignmentResponse>.Fail(
                "VALIDATION_ERROR", "Choose a different staff member to take over these appointments.");

        // The receiver must be an ACTIVE, BOOKABLE member of THIS business. A suspended
        // colleague is not offered for new work, so handing them a diary would create
        // exactly the state deactivation is meant to prevent.
        var target = await _context.BusinessMemberships.AsNoTracking()
            .Where(m => m.BusinessId == businessId
                && m.UserId == targetId
                && m.Role == BusinessMembershipRole.Staff
                && m.Status == BusinessMembershipStatus.Active
                && m.IsBookable)
            .Select(m => new { m.UserId })
            .FirstOrDefaultAsync(cancellationToken);

        if (target == null)
            return ApiResponse<StaffReassignmentResponse>.Fail(
                "VALIDATION_ERROR", "The selected colleague is not an active, bookable member of this business.");

        var targetUser = await _context.Users.AsNoTracking()
            .Where(u => u.Id == targetId && !u.IsDeleted)
            .Select(u => new { u.Id, u.FullName })
            .FirstOrDefaultAsync(cancellationToken);

        if (targetUser == null)
            return ApiResponse<StaffReassignmentResponse>.Fail(
                "NOT_FOUND", "The selected colleague could not be found.");

        var now = DateTime.UtcNow;

        var query = _context.Appointments
            .Where(a => a.BusinessId == businessId
                && a.StaffUserId == staffUserId
                && OpenAppointmentStatuses.Contains(a.Status)
                && a.ScheduledAt >= now);

        if (request.AppointmentIds is { Count: > 0 } requestedIds)
        {
            var wanted = requestedIds.Where(id => id != Guid.Empty).Distinct().ToList();
            query = query.Where(a => wanted.Contains(a.Id));
        }

        var candidates = await query
            .OrderBy(a => a.ScheduledAt)
            .Select(a => new { a.Id, a.ScheduledAt, a.EndAt })
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return ApiResponse<StaffReassignmentResponse>.Ok(new StaffReassignmentResponse
            {
                FromStaffUserId = staffUserId,
                ToStaffUserId = targetId,
                Message = "There are no future appointments to move."
            });
        }

        var servicesByAppointment = await LoadServiceIdsAsync(
            candidates.Select(a => a.Id).ToList(), cancellationToken);

        // Everything the receiver already holds in the affected span, so an overlap is
        // refused here with a precise reason instead of surfacing as a database
        // exclusion violation after half the batch has moved.
        var minStart = candidates.Min(a => a.ScheduledAt);
        var maxEnd = candidates.Max(a => a.EndAt);

        var occupied = await _context.Appointments.AsNoTracking()
            .Where(a => a.BusinessId == businessId
                && a.StaffUserId == targetId
                && OpenAppointmentStatuses.Contains(a.Status)
                && a.EndAt > minStart
                && a.ScheduledAt < maxEnd)
            .Select(a => new { a.ScheduledAt, a.EndAt })
            .ToListAsync(cancellationToken);

        var busy = occupied.Select(o => (o.ScheduledAt, o.EndAt)).ToList();

        var eligibility = _eligibility ?? new ProviderEligibilityService(_context);
        var eligibleCache = new Dictionary<string, bool>(StringComparer.Ordinal);

        var moved = new List<Guid>();
        var skipped = new List<StaffReassignmentSkipResponse>();

        foreach (var appointment in candidates)
        {
            var serviceIds = servicesByAppointment.GetValueOrDefault(appointment.Id) ?? [];

            // An appointment with no service lines cannot be judged eligible, so it is
            // never moved on the assumption that it probably fits.
            if (serviceIds.Length == 0)
            {
                skipped.Add(Skip(appointment.Id, appointment.ScheduledAt, "NOT_ELIGIBLE"));
                continue;
            }

            var key = ServiceSetKey(serviceIds);
            if (!eligibleCache.TryGetValue(key, out var isEligible))
            {
                isEligible = await eligibility.IsEligibleAsync(
                    businessId, targetId, serviceIds, cancellationToken);
                eligibleCache[key] = isEligible;
            }

            if (!isEligible)
            {
                skipped.Add(Skip(appointment.Id, appointment.ScheduledAt, "NOT_ELIGIBLE"));
                continue;
            }

            // Overlap against everything the receiver already has, PLUS everything already
            // accepted in this batch — otherwise two moved bookings could collide with
            // each other even though neither collides with the existing diary.
            if (busy.Any(b => appointment.ScheduledAt < b.EndAt && appointment.EndAt > b.ScheduledAt))
            {
                skipped.Add(Skip(appointment.Id, appointment.ScheduledAt, "TARGET_UNAVAILABLE"));
                continue;
            }

            busy.Add((appointment.ScheduledAt, appointment.EndAt));
            moved.Add(appointment.Id);
        }

        if (moved.Count > 0)
        {
            var rows = await _context.Appointments
                .Where(a => moved.Contains(a.Id) && a.BusinessId == businessId)
                .ToListAsync(cancellationToken);

            var originalVersions = rows.ToDictionary(r => r.Id, r => r.Version);

            foreach (var row in rows)
            {
                row.StaffUserId = targetId;
                // A reassignment IS a change to the appointment, so the optimistic-concurrency
                // token moves with it: a stale transition request cannot silently win against it.
                row.Version++;
            }

            _audit?.Record(
                businessId,
                staffUserId,
                callerUserId,
                StaffAuditEventTypes.AppointmentsReassigned,
                $"{moved.Count} appointment(s) moved from {sourceUser.FullName} to {targetUser.FullName}.",
                new
                {
                    fromStaffUserId = staffUserId,
                    toStaffUserId = targetId,
                    movedCount = moved.Count,
                    skippedCount = skipped.Count,
                    appointmentIds = moved
                });

            // One transaction: either the whole handed-over batch moves or none of it does.
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                // The database's no-staff-overlap exclusion is the last word. It is reported
                // as a conflict rather than a 500, and the batch is rolled back atomically —
                // a half-moved diary would be worse than none.
                await transaction.RollbackAsync(cancellationToken);
                DiscardFailedReassignment(rows, staffUserId, originalVersions);

                _logger.LogWarning(ex,
                    "Reassignment from {FromStaffUserId} to {ToStaffUserId} in business {BusinessId} conflicted at the database.",
                    staffUserId, targetId, businessId);

                return ApiResponse<StaffReassignmentResponse>.Fail(
                    "CONFLICT",
                    "One of those appointments now overlaps this colleague's diary. Nothing was moved; please retry.");
            }
        }

        _logger.LogInformation(
            "Staff reassignment: business {BusinessId} moved {MovedCount} appointment(s) from {FromStaffUserId} to {ToStaffUserId} by {ActorUserId} ({SkippedCount} skipped)",
            businessId, moved.Count, staffUserId, targetId, callerUserId, skipped.Count);

        return ApiResponse<StaffReassignmentResponse>.Ok(new StaffReassignmentResponse
        {
            FromStaffUserId = staffUserId,
            ToStaffUserId = targetId,
            MovedCount = moved.Count,
            SkippedCount = skipped.Count,
            Skipped = skipped,
            Message = moved.Count == 0
                ? "No appointments could be moved. Review the reasons and try another colleague."
                : $"{moved.Count} appointment(s) moved to {targetUser.FullName}."
        });
    }

    private static StaffReassignmentSkipResponse Skip(Guid id, DateTime scheduledAt, string reason) =>
        new() { AppointmentId = id, ScheduledAt = scheduledAt, Reason = reason };

    /// <summary>
    /// Undoes a failed batch in the change tracker after the transaction rolled back.
    ///
    /// <para>Without this the appointment rows would stay <c>Modified</c> with the new
    /// owner, and the audit row would stay staged, so any later <c>SaveChanges</c> in
    /// the same request would retry — and possibly half-succeed a move that was
    /// reported to the caller as refused. The entities are detached rather than reset
    /// so no stale snapshot can be written either.</para>
    /// </summary>
    private void DiscardFailedReassignment(
        List<Appointment> rows, Guid sourceStaffUserId, IReadOnlyDictionary<Guid, int> originalVersions)
    {
        foreach (var row in rows)
        {
            row.StaffUserId = sourceStaffUserId;
            if (originalVersions.TryGetValue(row.Id, out var version)) row.Version = version;
            _context.Entry(row).State = EntityState.Detached;
        }

        foreach (var entry in _context.ChangeTracker.Entries<StaffAuditEvent>()
                     .Where(e => e.State == EntityState.Added
                         && e.Entity.EventType == StaffAuditEventTypes.AppointmentsReassigned)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Service-line ids per appointment, in one query for the whole batch.
    /// </summary>
    private async Task<Dictionary<Guid, Guid[]>> LoadServiceIdsAsync(
        List<Guid> appointmentIds, CancellationToken cancellationToken)
    {
        if (appointmentIds.Count == 0) return [];

        var rows = await _context.AppointmentResources.AsNoTracking()
            .Where(r => appointmentIds.Contains(r.AppointmentId))
            .Select(r => new { r.AppointmentId, r.ServiceCatalogItemId })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.AppointmentId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.ServiceCatalogItemId).Distinct().OrderBy(x => x).ToArray());
    }

    /// <summary>
    /// Stable key for a service combination, so eligibility is asked once per distinct
    /// combination instead of once per appointment.
    /// </summary>
    private static string ServiceSetKey(IEnumerable<Guid> serviceIds) =>
        string.Join(',', serviceIds.OrderBy(id => id));
}

/// <summary>Who could take over a leaving member's work.</summary>
public partial class StaffManagementService
{
    /// <summary>
    /// Colleagues who are active, bookable and qualified for at least one of the
    /// member's future appointments, most capable first.
    ///
    /// <para>Eligibility is never re-implemented here: it is asked of
    /// <see cref="IProviderEligibilityService"/>, the single authority the booking
    /// engine itself uses, so a colleague offered for reassignment is exactly a
    /// colleague the public booking flow would have offered.</para>
    /// </summary>
    private async Task<List<StaffReassignmentCandidateResponse>> RankReassignmentCandidatesAsync(
        Guid businessId, Guid staffUserId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var futureIds = await _context.Appointments.AsNoTracking()
            .Where(a => a.BusinessId == businessId
                && a.StaffUserId == staffUserId
                && OpenAppointmentStatuses.Contains(a.Status)
                && a.ScheduledAt >= now)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        if (futureIds.Count == 0) return [];

        var servicesByAppointment = await LoadServiceIdsAsync(futureIds, cancellationToken);
        var withServices = servicesByAppointment.Where(kv => kv.Value.Length > 0).ToList();
        if (withServices.Count == 0) return [];

        // Distinct combinations only, capped: a diary with fifty identical haircuts asks
        // the eligibility service once, not fifty times.
        var combinations = withServices
            .Select(kv => kv.Value)
            .Select(ids => (Key: ServiceSetKey(ids), ServiceIds: ids))
            .DistinctBy(x => x.Key, StringComparer.Ordinal)
            .Take(MaxEligibilityProbes)
            .ToList();

        var candidates = await _context.BusinessMemberships.AsNoTracking()
            .Where(m => m.BusinessId == businessId
                && m.Role == BusinessMembershipRole.Staff
                && m.Status == BusinessMembershipStatus.Active
                && m.IsBookable
                && m.UserId != staffUserId)
            .Select(m => new { m.UserId, m.PositionId })
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0) return [];

        var candidateIds = candidates.Select(c => c.UserId).ToList();

        var names = await _context.Users.AsNoTracking()
            .Where(u => candidateIds.Contains(u.Id) && !u.IsDeleted)
            .Select(u => new { u.Id, u.FullName })
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        var positionIds = candidates
            .Where(c => c.PositionId.HasValue)
            .Select(c => c.PositionId!.Value)
            .Distinct()
            .ToList();

        var positions = positionIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.StaffPositions.AsNoTracking()
                .Where(p => p.BusinessId == businessId && positionIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name })
                .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);

        var eligibility = _eligibility ?? new ProviderEligibilityService(_context);
        var result = new List<StaffReassignmentCandidateResponse>();

        foreach (var candidate in candidates)
        {
            if (!names.TryGetValue(candidate.UserId, out var fullName)) continue;

            var covered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var combination in combinations)
            {
                if (await eligibility.IsEligibleAsync(
                        businessId, candidate.UserId, combination.ServiceIds, cancellationToken))
                    covered.Add(combination.Key);
            }

            if (covered.Count == 0) continue;

            var eligibleCount = withServices.Count(kv =>
                covered.Contains(ServiceSetKey(kv.Value)));

            if (eligibleCount == 0) continue;

            result.Add(new StaffReassignmentCandidateResponse
            {
                UserId = candidate.UserId,
                FullName = fullName,
                PositionName = candidate.PositionId.HasValue
                    && positions.TryGetValue(candidate.PositionId.Value, out var positionName)
                        ? positionName
                        : null,
                EligibleAppointmentCount = eligibleCount
            });
        }

        return result
            .OrderByDescending(c => c.EligibleAppointmentCount)
            .ThenBy(c => c.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

