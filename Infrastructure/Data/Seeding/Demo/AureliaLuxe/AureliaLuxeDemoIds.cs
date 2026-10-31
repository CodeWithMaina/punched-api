using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Seeding.Demo.AureliaLuxe;

/// <summary>
/// Deterministic identifier factory for every row the Aurelia demo seeder owns.
/// Re-running the seeder therefore resolves the *same* ids and updates in place
/// instead of inserting duplicates, and the ids are the marker that tells a
/// seeder-managed row apart from one a human created in the app.
/// </summary>
internal static class AureliaLuxeDemoIds
{
    private const string Scope = "aurelia-demo";

    public static Guid Service(string serviceKey) => DeterministicSeed.GuidFor(Scope, $"service:{serviceKey}");
    public static Guid StaffAssignment(string staffKey, string serviceKey) =>
        DeterministicSeed.GuidFor(Scope, $"staff-service:{staffKey}:{serviceKey}");
    public static Guid Shift(string staffKey, DateOnly date) =>
        DeterministicSeed.GuidFor(Scope, $"shift:{staffKey}:{date:yyyy-MM-dd}");
    public static Guid Membership(string userKey, string role) =>
        DeterministicSeed.GuidFor(Scope, $"membership:{userKey}:{role}");
    public static Guid StampCard(string programName, string cardKey) =>
        DeterministicSeed.GuidFor(Scope, $"stamp-card:{programName}:{cardKey}");
    public static Guid Reward(string rewardKey) => DeterministicSeed.GuidFor(Scope, $"reward:{rewardKey}");
    public static Guid EarningRule(string ruleKey) => DeterministicSeed.GuidFor(Scope, $"earning-rule:{ruleKey}");
    public static Guid AttendanceLocation(string name) =>
        DeterministicSeed.GuidFor(Scope, $"attendance-location:{name}");
    public static Guid AttendanceCredential(string name) =>
        DeterministicSeed.GuidFor(Scope, $"attendance-credential:{name}");
    public static Guid MessagingProfile() => DeterministicSeed.GuidFor(Scope, "messaging-profile");
    public static Guid PaymentConfig() => DeterministicSeed.GuidFor(Scope, "payment-config");
    public static Guid LandingPage() => DeterministicSeed.GuidFor(Scope, "landing-page");
    public static Guid AutomationRule(string ruleKey) => DeterministicSeed.GuidFor(Scope, $"automation-rule:{ruleKey}");
    public static Guid SubscriptionAudit(string key) => DeterministicSeed.GuidFor(Scope, $"subscription-audit:{key}");

    // ── Activity (refreshed on every run) ───────────────────────
    public static Guid Appointment(int index) => DeterministicSeed.GuidFor(Scope, $"appointment:{index:D5}");
    public static Guid AppointmentResource(int index, int line) =>
        DeterministicSeed.GuidFor(Scope, $"appointment-resource:{index:D5}:{line}");
    public static Guid AppointmentHistory(int index, int step) =>
        DeterministicSeed.GuidFor(Scope, $"appointment-history:{index:D5}:{step}");
    public static Guid RescheduleRequest(int index) =>
        DeterministicSeed.GuidFor(Scope, $"reschedule:{index:D5}");
    public static Guid Payment(int index) => DeterministicSeed.GuidFor(Scope, $"payment:{index:D5}");
    public static Guid PaymentAttempt(int index, int attempt) =>
        DeterministicSeed.GuidFor(Scope, $"payment-attempt:{index:D5}:{attempt}");
    public static Guid Stamp(string customerKey, int number) =>
        DeterministicSeed.GuidFor(Scope, $"stamp:{customerKey}:{number:D3}");
    public static Guid QrToken(string customerKey, int number) =>
        DeterministicSeed.GuidFor(Scope, $"qr:{customerKey}:{number:D3}");
    public static Guid StampTransaction(string customerKey, int number) =>
        DeterministicSeed.GuidFor(Scope, $"stamp-tx:{customerKey}:{number:D3}");
    public static Guid RewardEntitlement(string customerKey, int cycle) =>
        DeterministicSeed.GuidFor(Scope, $"entitlement:{customerKey}:{cycle}");
    public static Guid Redemption(string customerKey, int cycle) =>
        DeterministicSeed.GuidFor(Scope, $"redemption:{customerKey}:{cycle}");
    public static Guid StampAdjustment(string customerKey) =>
        DeterministicSeed.GuidFor(Scope, $"stamp-adjustment:{customerKey}");
    public static Guid AttendanceSession(string staffKey, DateOnly date) =>
        DeterministicSeed.GuidFor(Scope, $"attendance-session:{staffKey}:{date:yyyy-MM-dd}");
    public static Guid AttendanceEvent(string staffKey, DateOnly date, string kind) =>
        DeterministicSeed.GuidFor(Scope, $"attendance-event:{staffKey}:{date:yyyy-MM-dd}:{kind}");
    public static Guid Review(int index) => DeterministicSeed.GuidFor(Scope, $"review:{index:D5}");
    public static Guid Notification(string key) => DeterministicSeed.GuidFor(Scope, $"notification:{key}");
    public static Guid NotificationLog(string key) => DeterministicSeed.GuidFor(Scope, $"notification-log:{key}");
    public static Guid Segment(string customerKey) => DeterministicSeed.GuidFor(Scope, $"segment:{customerKey}");
    public static Guid Insight(string key) => DeterministicSeed.GuidFor(Scope, $"insight:{key}");
    public static Guid ReferralLink(string customerKey) =>
        DeterministicSeed.GuidFor(Scope, $"referral-link:{customerKey}");
    public static Guid Referral(string referrerKey, string refereeKey) =>
        DeterministicSeed.GuidFor(Scope, $"referral:{referrerKey}:{refereeKey}");
    public static Guid StaffInvitation(string email) => DeterministicSeed.GuidFor(Scope, $"staff-invitation:{email}");

    /// <summary>Deterministic password hash (BCrypt) used for the demo accounts.</summary>
    public static string HashPassword(string plaintext) => DeterministicSeed.HashPassword(plaintext);

    /// <summary>Deterministic opaque token value (used for QR/credential hashes).</summary>
    public static string Token(string tokenScope, string key) => DeterministicSeed.TokenFor($"{Scope}:{tokenScope}", key);
}

/// <summary>
/// A small, self-contained deterministic generator. It is deliberately NOT the
/// shared <c>ISeedRandom</c>: the demo output must not shift when unrelated seed
/// steps change how many random draws they make.
/// </summary>
internal sealed class AureliaDemoRandom
{
    private readonly Random _random;

    public AureliaDemoRandom(int salt)
        => _random = new Random(AureliaLuxeDemoData.RandomSeed + salt);

    public int Next(int minInclusive, int maxExclusive)
        => maxExclusive <= minInclusive ? minInclusive : _random.Next(minInclusive, maxExclusive);

    public double NextDouble() => _random.NextDouble();

    public bool Chance(double probability) => _random.NextDouble() < probability;

    public T Pick<T>(IReadOnlyList<T> values) => values[Next(0, values.Count)];

    public T Pick<T>(IEnumerable<T> values)
    {
        var list = values as IReadOnlyList<T> ?? values.ToList();
        return Pick(list);
    }
}

/// <summary>
/// Resolved, tenant-scoped handles the demo seeder works with. Everything is
/// looked up from the database (or from the in-flight seed context) rather than
/// assumed, so the seeder can never attach data to the wrong business.
/// </summary>
internal sealed class AureliaLuxeDemoContext
{
    public required Business Business { get; init; }
    public required User Owner { get; init; }
    public required IReadOnlyList<AureliaLuxeStaffHandle> Staff { get; init; }
    public required IReadOnlyList<AureliaLuxeCustomerHandle> Customers { get; init; }

    public Guid BusinessId => Business.Id;

    public Guid StaffId(string userKey) => Staff.First(s => s.Key == userKey).User.Id;

    public IEnumerable<Guid> CustomerIds => Customers.Select(c => c.User.Id);

    public string CustomerKeyFor(Guid userId) =>
        Customers.FirstOrDefault(c => c.User.Id == userId)?.Key ?? userId.ToString("N");
}

internal sealed record AureliaLuxeStaffHandle(
    string Key,
    User User,
    AureliaLuxeDemoData.StaffDefinition Definition);

internal sealed record AureliaLuxeCustomerHandle(string Key, User User);
