using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data;

/// <summary>
/// Entity Framework Core DbContext for the Punched platform.
/// Configures all 8 core entities + RefreshToken with Fluent API.
/// Uses PostgreSQL (Neon) as the database provider.
/// </summary>
public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options) { }

    // ── DbSets ──────────────────────────────────────────────
    public DbSet<UserAuth> UserAuths => Set<UserAuth>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<LoyaltyProgram> LoyaltyPrograms => Set<LoyaltyProgram>();
    public DbSet<LoyaltyCard> LoyaltyCards => Set<LoyaltyCard>();
    public DbSet<QrToken> QrTokens => Set<QrToken>();
    public DbSet<Stamp> Stamps => Set<Stamp>();
    public DbSet<Redemption> Redemptions => Set<Redemption>();
    public DbSet<ReferralProgram> ReferralPrograms => Set<ReferralProgram>();
    public DbSet<ReferralLink> ReferralLinks => Set<ReferralLink>();
    public DbSet<Referral> Referrals => Set<Referral>();
    public DbSet<BusinessDailyAnalytics> BusinessDailyAnalytics => Set<BusinessDailyAnalytics>();
    public DbSet<StaffDailyAnalytics> StaffDailyAnalytics => Set<StaffDailyAnalytics>();
    public DbSet<StaffShift> StaffShifts => Set<StaffShift>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<ApiEventLog> ApiEventLogs => Set<ApiEventLog>();
    public DbSet<LoyaltyProgramHistory> LoyaltyProgramHistory => Set<LoyaltyProgramHistory>();
    public DbSet<CustomerSegment> CustomerSegments => Set<CustomerSegment>();
    public DbSet<Insight> Insights => Set<Insight>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AppointmentStatusHistory> AppointmentStatusHistory => Set<AppointmentStatusHistory>();
    public DbSet<AppointmentResource> AppointmentResources => Set<AppointmentResource>();
    public DbSet<ServiceCatalogItem> ServiceCatalogItems => Set<ServiceCatalogItem>();
    public DbSet<StaffServiceAssignment> StaffServiceAssignments => Set<StaffServiceAssignment>();
    public DbSet<Notification> Notifications => Set<Notification>();

    /// <summary>Sparse preference overrides (business kill-switches + user overrides).</summary>
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

    public DbSet<RescheduleRequest> RescheduleRequests => Set<RescheduleRequest>();
    public DbSet<StaffInvitation> StaffInvitations => Set<StaffInvitation>();
    public DbSet<Module> Modules => Set<Module>();
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<PlanModule> PlanModules => Set<PlanModule>();
    public DbSet<BusinessSubscription> BusinessSubscriptions => Set<BusinessSubscription>();
    public DbSet<BusinessModule> BusinessModules => Set<BusinessModule>();
    public DbSet<StampAdjustment> StampAdjustments => Set<StampAdjustment>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<SubscriptionAuditLog> SubscriptionAuditLogs => Set<SubscriptionAuditLog>();
    public DbSet<StampCard> StampCards => Set<StampCard>();
    public DbSet<CardDesign> CardDesigns => Set<CardDesign>();
    public DbSet<CardDesignVersion> CardDesignVersions => Set<CardDesignVersion>();
    public DbSet<CardAsset> CardAssets => Set<CardAsset>();
    public DbSet<StampCardRulesChange> StampCardRulesChanges => Set<StampCardRulesChange>();
    public DbSet<CustomerBusinessEnrollment> CustomerBusinessEnrollments => Set<CustomerBusinessEnrollment>();
    public DbSet<CustomerStampCard> CustomerStampCards => Set<CustomerStampCard>();


    // ── Payments module (direct-to-business payments) ──────
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();
    public DbSet<PaymentCallback> PaymentCallbacks => Set<PaymentCallback>();
    public DbSet<BusinessPaymentConfig> BusinessPaymentConfigs => Set<BusinessPaymentConfig>();

    // ── Attendance module ─────────────────────────────────────
    public DbSet<AttendancePolicy> AttendancePolicies => Set<AttendancePolicy>();
    public DbSet<AttendanceLocation> AttendanceLocations => Set<AttendanceLocation>();
    public DbSet<AttendanceQrCredential> AttendanceQrCredentials => Set<AttendanceQrCredential>();
    public DbSet<AttendanceEvent> AttendanceEvents => Set<AttendanceEvent>();
    public DbSet<AttendanceSession> AttendanceSessions => Set<AttendanceSession>();

    // ── Loyalty programs: earning rules, transactions, rewards ──
    public DbSet<LoyaltyEarningRule> LoyaltyEarningRules => Set<LoyaltyEarningRule>();
    public DbSet<StampTransaction> StampTransactions => Set<StampTransaction>();
    public DbSet<Reward> Rewards => Set<Reward>();
    public DbSet<RewardEntitlement> RewardEntitlements => Set<RewardEntitlement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all Fluent API configurations from the Configurations folder
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        // Global filters ensure soft-deleted records are excluded from normal app flows.
        modelBuilder.Entity<User>().HasQueryFilter(u => !u.IsDeleted);
        modelBuilder.Entity<Business>().HasQueryFilter(b => !b.IsDeleted);
    }
}
