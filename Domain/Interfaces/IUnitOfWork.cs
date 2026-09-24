using PunchedApi.Domain.Entities;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Unit of Work interface coordinating repository operations and database transactions.
/// Ensures all changes within a business operation are committed atomically.
/// </summary>
public interface IUnitOfWork : IDisposable
{
    /// <summary>Repository for UserAuth entities.</summary>
    IRepository<UserAuth> UserAuths { get; }

    /// <summary>Repository for User entities.</summary>
    IRepository<User> Users { get; }

    /// <summary>Repository for RefreshToken entities.</summary>
    IRepository<RefreshToken> RefreshTokens { get; }

    /// <summary>Repository for Business entities.</summary>
    IRepository<Business> Businesses { get; }

    /// <summary>Repository for LoyaltyProgram entities.</summary>
    IRepository<LoyaltyProgram> LoyaltyPrograms { get; }

    /// <summary>Repository for LoyaltyCard entities.</summary>
    IRepository<LoyaltyCard> LoyaltyCards { get; }

    /// <summary>Repository for QrToken entities.</summary>
    IRepository<QrToken> QrTokens { get; }

    /// <summary>Repository for Stamp entities.</summary>
        IRepository<Stamp> Stamps { get; }

    /// <summary>Repository for Redemption entities.</summary>
    IRepository<Redemption> Redemptions { get; }

        /// <summary>Repository for StampAdjustment entities.</summary>
    IRepository<StampAdjustment> StampAdjustments { get; }

    /// <summary>Repository for IdempotencyKey entities.</summary>
    IRepository<IdempotencyKey> IdempotencyKeys { get; }

    /// <summary>Repository for ApiEventLog entities.</summary>
    IRepository<ApiEventLog> ApiEventLogs { get; }

    /// <summary>Repository for ReferralProgram entities.</summary>
    IRepository<ReferralProgram> ReferralPrograms { get; }

    /// <summary>Repository for ReferralLink entities.</summary>
    IRepository<ReferralLink> ReferralLinks { get; }

    /// <summary>Repository for Referral entities.</summary>
    IRepository<Referral> Referrals { get; }

    /// <summary>Repository for Notification entities.</summary>
    IRepository<Notification> Notifications { get; }

    /// <summary>Repository for NotificationPreference entities.</summary>
    IRepository<NotificationPreference> NotificationPreferences { get; }

    /// <summary>Repository for StaffInvitation entities.</summary>
    IRepository<StaffInvitation> StaffInvitations { get; }

    /// <summary>Repository for Appointment entities.</summary>
    IRepository<Appointment> Appointments { get; }

    /// <summary>Repository for AppointmentResource entities.</summary>
    IRepository<AppointmentResource> AppointmentResources { get; }

    /// <summary>Repository for AppointmentStatusHistory entities.</summary>
    IRepository<AppointmentStatusHistory> AppointmentStatusHistory { get; }

    /// <summary>Repository for ServiceCatalogItem entities.</summary>
    IRepository<ServiceCatalogItem> ServiceCatalogItems { get; }

    /// <summary>Repository for StaffShift entities.</summary>
    IRepository<StaffShift> StaffShifts { get; }

    /// <summary>Repository for StaffServiceAssignment entities.</summary>
    IRepository<StaffServiceAssignment> StaffServiceAssignments { get; }

    /// <summary>Repository for StampCard entities.</summary>
    IRepository<StampCard> StampCards { get; }

    /// <summary>Repository for CustomerBusinessEnrollment entities.</summary>
    IRepository<CustomerBusinessEnrollment> CustomerBusinessEnrollments { get; }

    /// <summary>Repository for CustomerStampCard entities.</summary>
    IRepository<CustomerStampCard> CustomerStampCards { get; }

    /// <summary>Repository for CardDesign entities.</summary>
    IRepository<CardDesign> CardDesigns { get; }

    /// <summary>Repository for the immutable card-design presentation history.</summary>
    IRepository<CardDesignVersion> CardDesignVersions { get; }

    /// <summary>Repository for uploaded card branding assets.</summary>
    IRepository<CardAsset> CardAssets { get; }

    /// <summary>Repository for the stamp-card business-rule change audit trail.</summary>
    IRepository<StampCardRulesChange> StampCardRulesChanges { get; }

    /// <summary>Repository for LoyaltyEarningRule entities.</summary>
    IRepository<LoyaltyEarningRule> LoyaltyEarningRules { get; }

    /// <summary>Repository for the immutable StampTransaction ledger.</summary>
    IRepository<StampTransaction> StampTransactions { get; }

    /// <summary>Repository for Reward entities.</summary>
    IRepository<Reward> Rewards { get; }

    /// <summary>Repository for RewardEntitlement entities.</summary>
    IRepository<RewardEntitlement> RewardEntitlements { get; }

    /// <summary>Repository for Payment entities.</summary>
    IRepository<Payment> Payments { get; }
    /// <summary>Repository for PaymentAttempt entities.</summary>
    IRepository<PaymentAttempt> PaymentAttempts { get; }
    /// <summary>Repository for PaymentCallback entities.</summary>
    IRepository<PaymentCallback> PaymentCallbacks { get; }
    /// <summary>Repository for BusinessPaymentConfig entities.</summary>
    IRepository<BusinessPaymentConfig> BusinessPaymentConfigs { get; }
    /// <summary>
    /// Commits all pending changes to the database.
    /// </summary>
    /// <returns>Number of state entries written to the database.</returns>
    Task<int> SaveChangesAsync();
}
