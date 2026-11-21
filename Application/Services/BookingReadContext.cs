using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Runs the independent reads a booking decision needs <b>concurrently</b>, each on its
/// own short-lived <see cref="ApplicationDbContext"/>.
///
/// <para><b>Why this exists.</b> A booking decision (and an availability grid) needs a
/// dozen unrelated reads: the business, its catalogue, the roster, the roster's service
/// assignments, the business schedule, each member's working week, the dated rosters and
/// the appointments already holding time. Sequentially that is a dozen network round
/// trips. On a pooled database far from the API — a Neon instance measures roughly
/// 215-265 ms per trip — that alone is several seconds before anything is written, which
/// is exactly how a booking came to take ten seconds or more.</para>
///
/// <para>None of those reads depend on each other, so they run in parallel across pooled
/// connections and the wall-clock cost becomes about one round trip instead of ten. The
/// entities and the rules are unchanged: this only changes <i>when</i> the queries run,
/// never <i>what</i> they ask. The single authoritative write (and its transaction) still
/// happens on the caller's context.</para>
///
/// <para>Each context is created from the registered
/// <see cref="DbContextOptions{ApplicationDbContext}"/> and disposed with the fan-out.
/// Nothing is ever written through them.</para>
/// </summary>
public sealed class BookingReadFanout : IDisposable
{
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly List<ApplicationDbContext> _contexts = new();

    public BookingReadFanout(DbContextOptions<ApplicationDbContext> options) => _options = options;

    /// <summary>How many reads run at once. Bounded so one booking cannot exhaust the pool.</summary>
    public const int MaxDegreeOfParallelism = 8;

    /// <summary>
    /// Runs every read concurrently and returns their results in the same order. A read
    /// that throws faults the whole batch — a booking decision is never made on a
    /// partially-read schedule.
    /// </summary>
    public async Task<T[]> RunAsync<T>(IReadOnlyList<Func<ApplicationDbContext, Task<T>>> reads)
    {
        if (reads.Count == 0) return Array.Empty<T>();

        // Contexts are created up front, on this thread, so the list is not mutated by the
        // workers. new ApplicationDbContext is safe here: the type only needs its options,
        // and these instances never save.
        var contexts = new ApplicationDbContext[reads.Count];
        for (var i = 0; i < reads.Count; i++)
        {
            contexts[i] = new ApplicationDbContext(_options);
            _contexts.Add(contexts[i]);
        }

        var results = new T[reads.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, reads.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism },
            async (index, _) => { results[index] = await reads[index](contexts[index]); });

        return results;
    }

    public void Dispose()
    {
        foreach (var context in _contexts) context.Dispose();
        _contexts.Clear();
    }
}

/// <summary>One reserved interval of a staff member's diary.</summary>
public sealed record StaffBusyInterval(
    Guid StaffUserId, DateTime StartUtc, DateTime EndUtc, Guid AppointmentId);
/// <summary>
/// The customer record a storefront booking belongs to, matched inside the same read wave
/// as the rest of the booking context.
///
/// <para>Matching follows the schema grain: an EMAIL is a person account, while a PHONE is
/// only trusted when this business already knows it (it must be accompanied by an
/// enrollment). The enrollment carries the Guest/Registered tag.</para>
/// </summary>
public sealed record BookingCustomerMatch(
    Guid UserId,
    string FullName,
    string Email,
    string? PhoneNumber,
    string? AvatarUrl,
    CustomerType? EnrollmentType,
    CustomerBusinessEnrollmentStatus? EnrollmentStatus)
{
    /// <summary>
    /// True only when this business has a real, active relationship with the customer.
    /// Nullable on purpose: an absent enrollment reads as no relationship, never as
    /// Active (the enum zero value), which would silently skip the enrollment a new
    /// business relationship needs.
    /// </summary>
    public bool HasActiveEnrollment => EnrollmentStatus == CustomerBusinessEnrollmentStatus.Active;
}

/// <summary>
/// Everything a booking decision or an availability grid needs, read once.
///
/// <para>Windows are minutes from business-local midnight, exactly as the schedule tables
/// store them, so the existing grid code consumes this without translation.</para>
/// </summary>
public sealed class BookingReadContext
{
    public required Business Business { get; init; }
    public BusinessPaymentConfig? PaymentConfig { get; init; }
    public IReadOnlyList<ServiceCatalogItem> Services { get; init; } = Array.Empty<ServiceCatalogItem>();

    /// <summary>Members eligible to take <b>every</b> requested service, alphabetically.</summary>
    public IReadOnlyList<User> Providers { get; init; } = Array.Empty<User>();

    /// <summary>Solo / owner_only / category_based — see <see cref="EffectiveProviderModel"/>.</summary>
    public string ProviderModel { get; init; } = EffectiveProviderModel.CategoryBased;
    public bool StaffModuleEnabled { get; init; } = true;

    /// <summary>False when the business has never configured weekly operating hours.</summary>
    public bool ScheduleConfigured { get; init; }

    public IReadOnlyDictionary<DateOnly, List<(int Start, int End)>> BusinessWindows { get; init; } =
        new Dictionary<DateOnly, List<(int Start, int End)>>();

    /// <summary>Effective working windows per (staff, local date), already resolved.</summary>
    public IReadOnlyDictionary<(Guid StaffUserId, DateOnly Date), List<(int Start, int End)>> StaffWindows { get; init; } =
        new Dictionary<(Guid, DateOnly), List<(int, int)>>();

    public IReadOnlyDictionary<Guid, List<StaffBusyInterval>> BusyByStaff { get; init; } =
        new Dictionary<Guid, List<StaffBusyInterval>>();

    /// <summary>The reminder ladder JSON, read once so the write path need not ask again.</summary>
    public string? ReminderOffsetMinutesJson { get; init; }

    /// <summary>
    /// The business's existing customer record for the details a storefront visitor typed,
    /// or null when they are new. Read with everything else so the write path never has to
    /// ask who this customer is.
    /// </summary>
    public BookingCustomerMatch? CustomerMatch { get; init; }

    /// <summary>
    /// The appointment already created for this booking-intent key, or null. A retry is a
    /// replay of that row, not a new booking — and it must be answered before availability is
    /// re-checked, because the retry interval is now held by the customer's own booking.
    /// </summary>
    public Appointment? ExistingBooking { get; init; }

    public int TotalDurationMinutes => Services.Sum(s => s.DurationMinutes);

    /// <summary>The business's own booking window, in local minutes from midnight.</summary>
    public (int Start, int End) DefaultWindow => (Business.BookingOpenHour * 60, Business.BookingCloseHour * 60);

    /// <summary>
    /// The effective working windows for one member on one local date. Falls back to the
    /// business window when the member is not part of the resolved roster (which can only
    /// happen for a member the caller pinned explicitly).
    /// </summary>
    public List<(int Start, int End)> WindowsFor(Guid staffUserId, DateOnly date)
    {
        if (StaffWindows.TryGetValue((staffUserId, date), out var windows)) return windows;

        if (ScheduleConfigured)
        {
            return BusinessWindows.TryGetValue(date, out var scheduled)
                ? scheduled
                : new List<(int, int)>();
        }

        return new List<(int, int)> { DefaultWindow };
    }

    public List<StaffBusyInterval> BusyFor(Guid staffUserId) =>
        BusyByStaff.TryGetValue(staffUserId, out var busy) ? busy : new List<StaffBusyInterval>();
}

/// <summary>
/// Loads a <see cref="BookingReadContext"/> in one parallel wave.
///
/// <para>The eligibility rules are the ones <see cref="IProviderEligibilityService"/>
/// documents — an owner-based model collapses to the owner, a category-based model takes
/// the members assigned every requested service's category, and an uncategorised service
/// is open to everyone unless somebody is explicitly assigned — applied here to rows read
/// in bulk instead of one query at a time.</para>
/// </summary>
public sealed class BookingReadContextLoader
{
    private readonly BookingReadFanout? _fanout;
    private readonly ApplicationDbContext? _sequential;
    private readonly IModuleEntitlementService? _modules;

    public BookingReadContextLoader(BookingReadFanout fanout, IModuleEntitlementService? modules = null)
    {
        _fanout = fanout;
        _modules = modules;
    }

    /// <summary>
    /// Sequential fallback over an existing context. Used where no
    /// DbContextOptions is available (unit tests that construct the availability engine
    /// directly): the same reads, one at a time, on the caller's context — identical data
    /// and rules, only without the parallelism.
    /// </summary>
    public BookingReadContextLoader(ApplicationDbContext context, IModuleEntitlementService? modules = null)
    {
        _sequential = context;
        _modules = modules;
    }

    /// <summary>
    /// Reads the context for the requested services across local dates
    /// [startDate, endDate].
    /// </summary>
    /// <param name="includePaymentConfig">Load the business's payment configuration too.</param>
    /// <param name="includeReminderPolicy">Load the reminder ladder too (booking writes).</param>
    public async Task<BookingReadContext?> LoadAsync(
        Guid businessId,
        IReadOnlyCollection<Guid> serviceIds,
        DateOnly startDate,
        DateOnly endDate,
        bool includePaymentConfig = false,
        bool includeReminderPolicy = false,
        string? customerEmail = null,
        string? customerPhone = null,
        string? bookingRequestKey = null,
        CancellationToken ct = default)
    {
        var distinctRequested = serviceIds.Distinct().ToArray();

        // Widened UTC bounds: a business-local day spans at most 14h around UTC, so one
        // day of slack on each side is a superset. The grid filters per local day, which
        // keeps the busy read a single query.
        var rangeStartUtc = startDate.AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var rangeEndUtc = endDate.AddDays(2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var reservingStatuses = AppointmentReservations.ReservingStatuses.ToArray();

        var reads = new List<Func<ApplicationDbContext, Task<object>>>
        {
            // 0 - the business itself.
            async db => (await db.Businesses.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == businessId, ct))!,

            // 1 - the selected services, tenant-scoped and active.
            async db => await db.ServiceCatalogItems.AsNoTracking()
                .Where(s => s.BusinessId == businessId && distinctRequested.Contains(s.Id) && s.IsActive)
                .ToListAsync(ct),

            // 2 - the roster (owner + active staff) with its user rows in the same query.
            async db => await db.BusinessMemberships.AsNoTracking()
                .Where(m => m.BusinessId == businessId
                    && m.Status == BusinessMembershipStatus.Active
                    && (m.Role == BusinessMembershipRole.Owner || m.Role == BusinessMembershipRole.Staff))
                .Select(m => new ProviderRow(
                    m.UserId, m.Role, m.IsBookable,
                    db.Users.Where(u => u.Id == m.UserId).Select(u => u.FullName).FirstOrDefault() ?? string.Empty,
                    db.Users.Where(u => u.Id == m.UserId).Select(u => u.AvatarUrl).FirstOrDefault(),
                    db.Users.Where(u => u.Id == m.UserId).Select(u => u.PhoneNumber).FirstOrDefault(),
                    db.Users.Where(u => u.Id == m.UserId).Select(u => u.Email).FirstOrDefault()))
                .ToListAsync(ct),

            // 3 - every provider-to-category assignment of this business (small, one query).
            async db => await db.StaffServiceCategories.AsNoTracking()
                .Where(a => a.BusinessId == businessId)
                .Select(a => new StaffCategoryRow(a.StaffUserId, a.ServiceCategoryId))
                .ToListAsync(ct),

            // 4 - explicit legacy service assignments (uncategorised services only).
            async db => await db.StaffServiceAssignments.AsNoTracking()
                .Where(a => a.BusinessId == businessId)
                .Select(a => new StaffServiceRow(a.StaffUserId, a.ServiceCatalogItemId))
                .ToListAsync(ct),

            // 5 - the business's weekly operating hours and their periods.
            async db => await db.BusinessOperatingHours.AsNoTracking()
                .Include(h => h.Periods)
                .Where(h => h.BusinessId == businessId)
                .ToListAsync(ct),
            // 6 - dated schedule exceptions inside the window.
            async db => await db.BusinessScheduleExceptions.AsNoTracking()
                .Include(e => e.Periods)
                .Where(e => e.BusinessId == businessId && e.Date >= startDate && e.Date <= endDate)
                .ToListAsync(ct),

            // 7 - dated rosters inside the window.
            async db => await db.StaffShifts.AsNoTracking()
                .Where(s => s.BusinessId == businessId && s.Date >= startDate && s.Date <= endDate)
                .ToListAsync(ct),

            // 8 - the recurring working week.
            async db => await db.StaffAvailability.AsNoTracking()
                .Where(a => a.BusinessId == businessId)
                .ToListAsync(ct),

            // 9 - appointments already holding time (reserving statuses only).
            async db => await db.Appointments.AsNoTracking()
                .Where(a => a.BusinessId == businessId
                    && a.StaffUserId != null
                    && reservingStatuses.Contains(a.Status)
                    && a.ScheduledAt < rangeEndUtc && a.EndAt > rangeStartUtc)
                .Select(a => new StaffBusyInterval(a.StaffUserId!.Value, a.ScheduledAt, a.EndAt, a.Id))
                .ToListAsync(ct),

            // 10 - payment configuration (booking writes only).
            async db => includePaymentConfig
                ? (await db.BusinessPaymentConfigs.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.BusinessId == businessId, ct))!
                : null!,

            // 11 - reminder ladder (booking writes only).
            async db => includeReminderPolicy
                ? (await db.BusinessReminderPolicies.AsNoTracking()
                    .Where(p => p.BusinessId == businessId)
                    .Select(p => p.OffsetMinutesJson)
                    .FirstOrDefaultAsync(ct))!
                : null!,

            // 12 - the customer this booking belongs to (storefront writes only). Email
            //      identifies an account; a phone is only trusted when this business
            //      already knows it, which is what the enrollment subquery enforces.
            //      "No match" is a valid outcome, so the null is carried through the
            //      object-typed read list exactly like the optional reads above.
            async db => (await FindCustomerMatchAsync(
                db, businessId, customerEmail, customerPhone, ct))!,

            // 13 - the booking this idempotency key already produced, if any.
            async db => string.IsNullOrWhiteSpace(bookingRequestKey)
                ? null!
                : (await db.Appointments.AsNoTracking()
                    .Include(a => a.Resources)
                    .FirstOrDefaultAsync(a => a.BusinessId == businessId
                        && a.BookingRequestKey == bookingRequestKey, ct))!
        };

        var results = _fanout != null
            ? await _fanout.RunAsync(reads)
            : await RunSequentiallyAsync(reads);

        if (results[0] is not Business business) return null;
        var services = (List<ServiceCatalogItem>)results[1];
        var providers = (List<ProviderRow>)results[2];
        var categoryAssignments = (List<StaffCategoryRow>)results[3];
        var serviceAssignments = (List<StaffServiceRow>)results[4];
        var hours = (List<BusinessOperatingHour>)results[5];
        var exceptions = (List<BusinessScheduleException>)results[6];
        var shifts = (List<StaffShift>)results[7];
        var weekly = (List<StaffAvailability>)results[8];
        var busy = (List<StaffBusyInterval>)results[9];
        var paymentConfig = results[10] as BusinessPaymentConfig;
        var reminderJson = results[11] as string;
        var customerMatch = results[12] as BookingCustomerMatch;
        var existingBooking = results[13] as Appointment;

        // The Staff module decides whether a multi-staff roster exists at all. Resolved
        // through the platform entitlement service (memory-cached), never a local flag.
        var staffModuleEnabled = _modules == null || await _modules.IsModuleEnabledAsync(businessId, "staff");

        var (eligible, model) = ResolveEligibleProviders(
            business, providers, services, categoryAssignments, serviceAssignments, staffModuleEnabled);

        var providerUsers = eligible
            .OrderBy(p => p.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(p => new User
            {
                Id = p.UserId,
                FullName = p.FullName,
                AvatarUrl = p.AvatarUrl,
                PhoneNumber = p.PhoneNumber,
                Email = p.Email ?? string.Empty
            })
            .ToList();

        var businessWindows = BuildBusinessWindows(hours, exceptions, startDate, endDate);
        var scheduleConfigured = businessWindows.Count > 0;

        var windowsByStaffDate = BuildStaffWindows(
            providerUsers, shifts, weekly, startDate, endDate,
            (business.BookingOpenHour * 60, business.BookingCloseHour * 60),
            businessWindows, scheduleConfigured);

        return new BookingReadContext
        {
            Business = business,
            PaymentConfig = paymentConfig,
            Services = services,
            Providers = providerUsers,
            ProviderModel = model,
            StaffModuleEnabled = staffModuleEnabled,
            ScheduleConfigured = scheduleConfigured,
            BusinessWindows = businessWindows,
            StaffWindows = windowsByStaffDate,
            BusyByStaff = busy.GroupBy(b => b.StaffUserId).ToDictionary(g => g.Key, g => g.ToList()),
            ReminderOffsetMinutesJson = reminderJson,
            CustomerMatch = customerMatch,
            ExistingBooking = existingBooking
        };
    }

    /// <summary>
    /// The business's customer record for the typed details, or null. One query: the account
    /// match and its enrollment tag come back together.
    /// </summary>
    private static async Task<BookingCustomerMatch?> FindCustomerMatchAsync(
        ApplicationDbContext db, Guid businessId, string? email, string? phone, CancellationToken ct)
    {
        var loweredEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
        var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());
        var suffix = digits.Length <= 9 ? digits : digits[^9..];
        if (loweredEmail == null && suffix.Length < 9) return null;

        return await db.Users
            .AsNoTracking()
            .Where(u => u.Role == UserRole.Customer && !u.IsDeleted
                && ((loweredEmail != null && u.Email != null && u.Email.ToLower() == loweredEmail)
                    || (suffix.Length == 9 && u.PhoneNumber != null
                        && u.PhoneNumber.EndsWith(suffix)
                        && db.CustomerBusinessEnrollments.Any(e =>
                            e.CustomerId == u.Id && e.BusinessId == businessId))))
            .OrderBy(u => u.CreatedAt)
            .Select(u => new BookingCustomerMatch(
                u.Id,
                u.FullName,
                u.Email,
                u.PhoneNumber,
                u.AvatarUrl,
                db.CustomerBusinessEnrollments
                    .Where(e => e.CustomerId == u.Id && e.BusinessId == businessId)
                    .Select(e => (CustomerType?)e.Type)
                    .FirstOrDefault(),
                db.CustomerBusinessEnrollments
                    .Where(e => e.CustomerId == u.Id && e.BusinessId == businessId)
                    .Select(e => (CustomerBusinessEnrollmentStatus?)e.Status)
                    .FirstOrDefault()))
            .FirstOrDefaultAsync(ct);
    }

    private async Task<object[]> RunSequentiallyAsync(IReadOnlyList<Func<ApplicationDbContext, Task<object>>> reads)
    {
        var results = new object[reads.Count];
        for (var i = 0; i < reads.Count; i++)
            results[i] = await reads[i](_sequential!);
        return results;
    }

    // -- eligibility ----------------------------------------------------------

    private static (List<ProviderRow> Eligible, string Model) ResolveEligibleProviders(
        Business business,
        List<ProviderRow> providers,
        List<ServiceCatalogItem> services,
        List<StaffCategoryRow> categoryAssignments,
        List<StaffServiceRow> serviceAssignments,
        bool staffModuleEnabled)
    {
        var owners = providers.Where(p => p.Role == BusinessMembershipRole.Owner).ToList();
        var staff = providers.Where(p => p.Role == BusinessMembershipRole.Staff).ToList();

        // Entitlement decides whether a *person* picks the provider; the operating mode and
        // roster decide how much capacity exists. One policy answers both — see
        // ProviderEligibilityPolicy for why they must not be conflated.
        var model = ProviderEligibilityPolicy.ResolveModel(
            business.OperatingMode, staffModuleEnabled, hasBookableStaff: staff.Count > 0);

        if (ProviderEligibilityPolicy.CollapsesToOwner(model))
        {
            var owner = owners.FirstOrDefault(p => p.IsBookable) ?? owners.FirstOrDefault();
            return (owner == null ? new List<ProviderRow>() : new List<ProviderRow> { owner }, model);
        }

        var requested = services.Select(s => s.Id).ToArray();
        var categoryByService = services.ToDictionary(s => s.Id, s => s.CategoryId);

        var categoriesByProvider = categoryAssignments
            .GroupBy(a => a.StaffUserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ServiceCategoryId).ToHashSet());

        var uncategorised = requested
            .Where(id => categoryByService.TryGetValue(id, out var category) && category == null)
            .ToHashSet();

        var explicitAssignments = new HashSet<(Guid Provider, Guid Service)>();
        var servicesWithExplicitAssignment = new HashSet<Guid>();
        foreach (var row in serviceAssignments.Where(r => uncategorised.Contains(r.ServiceCatalogItemId)))
        {
            servicesWithExplicitAssignment.Add(row.ServiceCatalogItemId);
            explicitAssignments.Add((row.StaffUserId, row.ServiceCatalogItemId));
        }

        bool Covers(ProviderRow provider, Guid serviceId)
        {
            var categoryId = categoryByService[serviceId];
            var hasCategory = categoryId is Guid category &&
                categoriesByProvider.TryGetValue(provider.UserId, out var cats) && cats.Contains(category);

            return ProviderEligibilityPolicy.ProviderCoversService(
                categoryId,
                hasCategory,
                providerHasExplicitAssignment: explicitAssignments.Contains((provider.UserId, serviceId)),
                serviceHasAnyExplicitAssignment: servicesWithExplicitAssignment.Contains(serviceId));
        }

        var eligible = providers
            .Where(p => p.IsBookable)
            .Where(p => requested.All(serviceId => Covers(p, serviceId)))
            .ToList();

        // `automatic` and the owner-fallback models hide the provider picker; `category_based`
        // offers it. Either way the eligible set is the real, filtered roster.
        return (eligible, model);
    }

    // -- business schedule ----------------------------------------------------

    /// <summary>
    /// The effective business windows per local date: a dated exception wins over the
    /// weekday, an omitted weekday is closed, and an empty result means the business has
    /// never configured a schedule (the legacy booking window then applies).
    /// </summary>
    private static Dictionary<DateOnly, List<(int Start, int End)>> BuildBusinessWindows(
        List<BusinessOperatingHour> hours,
        List<BusinessScheduleException> exceptions,
        DateOnly startDate,
        DateOnly endDate)
    {
        var result = new Dictionary<DateOnly, List<(int Start, int End)>>();
        if (hours.Count == 0) return result;

        var weekly = hours.GroupBy(h => h.DayOfWeek).ToDictionary(g => g.Key, g => g.First());
        var byDate = exceptions.GroupBy(e => e.Date).ToDictionary(g => g.Key, g => g.First());

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            if (byDate.TryGetValue(date, out var exception))
            {
                result[date] = exception.IsClosed
                    ? new List<(int, int)>()
                    : exception.Periods.Select(p => (p.StartMinute, p.EndMinute)).OrderBy(p => p.Item1).ToList();
                continue;
            }

            result[date] = weekly.TryGetValue((int)date.DayOfWeek, out var row)
                ? (row.IsClosed
                    ? new List<(int, int)>()
                    : row.Periods.Select(p => (p.StartMinute, p.EndMinute)).OrderBy(p => p.Item1).ToList())
                : new List<(int, int)>();
        }

        return result;
    }

    private static Dictionary<(Guid, DateOnly), List<(int Start, int End)>> BuildStaffWindows(
        List<User> providers,
        List<StaffShift> shifts,
        List<StaffAvailability> weekly,
        DateOnly startDate,
        DateOnly endDate,
        (int Start, int End) defaultWindow,
        Dictionary<DateOnly, List<(int Start, int End)>> businessWindows,
        bool scheduleConfigured)
    {
        var shiftsByStaffDate = shifts
            .GroupBy(s => (s.StaffUserId, s.Date))
            .ToDictionary(g => g.Key, g => g.ToList());

        var weeklyByStaff = weekly
            .GroupBy(a => a.StaffUserId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(a => a.DayOfWeek));

        var result = new Dictionary<(Guid, DateOnly), List<(int Start, int End)>>();

        foreach (var provider in providers)
        {
            weeklyByStaff.TryGetValue(provider.Id, out var pattern);
            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                shiftsByStaffDate.TryGetValue((provider.Id, date), out var rostered);
                IReadOnlyList<(int Start, int End)>? windowsForDate = null;
                if (scheduleConfigured)
                    windowsForDate = businessWindows.TryGetValue(date, out var scheduled)
                        ? scheduled
                        : new List<(int, int)>();

                result[(provider.Id, date)] = ResolveStaffWindows(date, rostered, pattern, defaultWindow, windowsForDate);
            }
        }

        return result;
    }

    /// <summary>
    /// A staff member's working windows for one local date, in minutes from local
    /// midnight: a dated roster row wins, else the recurring weekday, else the business's
    /// own window. A break splits the window so a booking never spans it.
    ///
    /// <para>This mirrors the availability engine resolver exactly; the grid and the
    /// booking write must agree on the roster.</para>
    /// </summary>
    internal static List<(int Start, int End)> ResolveStaffWindows(
        DateOnly date,
        List<StaffShift>? rostered,
        Dictionary<int, StaffAvailability>? weekly,
        (int Start, int End) defaultWindow,
        IReadOnlyList<(int Start, int End)>? businessWindows)
    {
        var hasRostered = rostered is { Count: > 0 };
        StaffAvailability? pattern = null;
        if (weekly != null && weekly.TryGetValue((int)date.DayOfWeek, out var found)) pattern = found;

        if (!hasRostered && pattern == null)
        {
            if (businessWindows != null) return businessWindows.OrderBy(w => w.Start).ToList();
            return new List<(int Start, int End)> { defaultWindow };
        }

        List<(bool Working, int Start, int End, int? BreakStart, int? BreakEnd)> source;
        if (hasRostered)
        {
            source = rostered!
                .Select(s => (
                    s.IsWorking,
                    s.StartMinute ?? s.StartHour * 60,
                    s.EndMinute ?? s.EndHour * 60,
                    s.BreakStartMinute,
                    s.BreakEndMinute))
                .ToList();
        }
        else
        {
            source = new List<(bool, int, int, int?, int?)>
            {
                (pattern!.IsWorking, pattern.StartMinute, pattern.EndMinute,
                    pattern.BreakStartMinute, pattern.BreakEndMinute)
            };
        }

        var windows = new List<(int Start, int End)>();
        foreach (var (working, start, end, breakStart, breakEnd) in source)
        {
            if (!working || end <= start) continue;

            var hasBreak = breakStart.HasValue && breakEnd.HasValue
                && breakStart.Value >= start && breakEnd.Value <= end
                && breakEnd.Value > breakStart.Value;

            if (!hasBreak)
            {
                windows.Add((start, end));
                continue;
            }

            if (breakStart!.Value > start) windows.Add((start, breakStart.Value));
            if (breakEnd!.Value < end) windows.Add((breakEnd.Value, end));
        }

        if (businessWindows == null) return windows.OrderBy(w => w.Start).ToList();

        var constrained = new List<(int Start, int End)>();
        foreach (var s in windows)
        {
            foreach (var b in businessWindows)
            {
                var start = Math.Max(s.Start, b.Start);
                var end = Math.Min(s.End, b.End);
                if (end > start) constrained.Add((start, end));
            }
        }

        return constrained.OrderBy(w => w.Start).ToList();
    }

    private sealed record ProviderRow(
        Guid UserId, BusinessMembershipRole Role, bool IsBookable, string FullName, string? AvatarUrl,
        string? PhoneNumber, string? Email);

    private sealed record StaffCategoryRow(Guid StaffUserId, Guid ServiceCategoryId);

    private sealed record StaffServiceRow(Guid StaffUserId, Guid ServiceCatalogItemId);
}
