using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Infrastructure.Repositories;

/// <summary>
/// Unit of Work implementation coordinating multiple repository operations.
/// Ensures atomic commits across related entity changes.
/// </summary>
public class UnitOfWork : IUnitOfWork
{
    private readonly ApplicationDbContext _context;
    private bool _disposed = false;

    // Lazy-initialized repositories
    private IRepository<UserAuth>? _userAuths;
    private IRepository<User>? _users;
    private IRepository<RefreshToken>? _refreshTokens;
    private IRepository<Business>? _businesses;
    private IRepository<LoyaltyProgram>? _loyaltyPrograms;
    private IRepository<LoyaltyCard>? _loyaltyCards;
    private IRepository<QrToken>? _qrTokens;
    private IRepository<Stamp>? _stamps;
            private IRepository<Redemption>? _redemptions;
    private IRepository<StampAdjustment>? _stampAdjustments;
    private IRepository<IdempotencyKey>? _idempotencyKeys;
    private IRepository<ApiEventLog>? _apiEventLogs;
    private IRepository<ReferralProgram>? _referralPrograms;
    private IRepository<ReferralLink>? _referralLinks;
    private IRepository<Referral>? _referrals;
    private IRepository<Notification>? _notifications;
    private IRepository<NotificationPreference>? _notificationPreferences;
    private IRepository<StaffInvitation>? _staffInvitations;
    private IRepository<Appointment>? _appointments;
    private IRepository<AppointmentResource>? _appointmentResources;
    private IRepository<AppointmentStatusHistory>? _appointmentStatusHistory;
    private IRepository<ServiceCatalogItem>? _serviceCatalogItems;
    private IRepository<StaffShift>? _staffShifts;
    private IRepository<StaffServiceAssignment>? _staffServiceAssignments;
    private IRepository<StampCard>? _stampCards;
    private IRepository<CustomerBusinessEnrollment>? _enrollments;
    private IRepository<CustomerStampCard>? _customerStampCards;
    private IRepository<CardDesign>? _cardDesigns;
    private IRepository<CardDesignVersion>? _cardDesignVersions;
    private IRepository<CardAsset>? _cardAssets;
    private IRepository<StampCardRulesChange>? _stampCardRulesChanges;
    private IRepository<LoyaltyEarningRule>? _loyaltyEarningRules;
    private IRepository<StampTransaction>? _stampTransactions;
    private IRepository<Reward>? _rewards;
    private IRepository<RewardEntitlement>? _rewardEntitlements;
    private IRepository<Payment>? _payments;
    private IRepository<PaymentAttempt>? _paymentAttempts;
    private IRepository<PaymentCallback>? _paymentCallbacks;
    private IRepository<BusinessPaymentConfig>? _businessPaymentConfigs;

    public UnitOfWork(ApplicationDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public IRepository<UserAuth> UserAuths =>
        _userAuths ??= new Repository<UserAuth>(_context);

    /// <inheritdoc />
    public IRepository<User> Users =>
        _users ??= new Repository<User>(_context);

    /// <inheritdoc />
    public IRepository<RefreshToken> RefreshTokens =>
        _refreshTokens ??= new Repository<RefreshToken>(_context);

    /// <inheritdoc />
    public IRepository<Business> Businesses =>
        _businesses ??= new Repository<Business>(_context);

    /// <inheritdoc />
    public IRepository<LoyaltyProgram> LoyaltyPrograms =>
        _loyaltyPrograms ??= new Repository<LoyaltyProgram>(_context);

    /// <inheritdoc />
    public IRepository<LoyaltyCard> LoyaltyCards =>
        _loyaltyCards ??= new Repository<LoyaltyCard>(_context);

    /// <inheritdoc />
    public IRepository<QrToken> QrTokens =>
        _qrTokens ??= new Repository<QrToken>(_context);

    /// <inheritdoc />
    public IRepository<Stamp> Stamps =>
        _stamps ??= new Repository<Stamp>(_context);

        public IRepository<Redemption> Redemptions =>
        _redemptions ??= new Repository<Redemption>(_context);

    public IRepository<StampAdjustment> StampAdjustments =>
        _stampAdjustments ??= new Repository<StampAdjustment>(_context);

        public IRepository<IdempotencyKey> IdempotencyKeys =>
        _idempotencyKeys ??= new Repository<IdempotencyKey>(_context);

    public IRepository<ApiEventLog> ApiEventLogs =>
        _apiEventLogs ??= new Repository<ApiEventLog>(_context);

    public IRepository<ReferralProgram> ReferralPrograms =>
        _referralPrograms ??= new Repository<ReferralProgram>(_context);

    /// <inheritdoc />
    public IRepository<ReferralLink> ReferralLinks =>
        _referralLinks ??= new Repository<ReferralLink>(_context);

    /// <inheritdoc />
    public IRepository<Referral> Referrals =>
        _referrals ??= new Repository<Referral>(_context);

    /// <inheritdoc />
    public IRepository<Notification> Notifications =>
        _notifications ??= new Repository<Notification>(_context);

    /// <inheritdoc />
    public IRepository<StaffInvitation> StaffInvitations =>
        _staffInvitations ??= new Repository<StaffInvitation>(_context);

    /// <inheritdoc />
    public IRepository<Appointment> Appointments =>
        _appointments ??= new Repository<Appointment>(_context);

    /// <inheritdoc />
    public IRepository<AppointmentResource> AppointmentResources =>
        _appointmentResources ??= new Repository<AppointmentResource>(_context);

    /// <inheritdoc />
    public IRepository<AppointmentStatusHistory> AppointmentStatusHistory =>
        _appointmentStatusHistory ??= new Repository<AppointmentStatusHistory>(_context);

    /// <inheritdoc />
    public IRepository<ServiceCatalogItem> ServiceCatalogItems =>
        _serviceCatalogItems ??= new Repository<ServiceCatalogItem>(_context);

    /// <inheritdoc />
    public IRepository<StaffShift> StaffShifts =>
        _staffShifts ??= new Repository<StaffShift>(_context);

    /// <inheritdoc />
    public IRepository<StaffServiceAssignment> StaffServiceAssignments =>
        _staffServiceAssignments ??= new Repository<StaffServiceAssignment>(_context);

    /// <inheritdoc />
    public IRepository<StampCard> StampCards =>
        _stampCards ??= new Repository<StampCard>(_context);

    /// <inheritdoc />
    public IRepository<CustomerBusinessEnrollment> CustomerBusinessEnrollments =>
        _enrollments ??= new Repository<CustomerBusinessEnrollment>(_context);

    /// <inheritdoc />
    public IRepository<CustomerStampCard> CustomerStampCards =>
        _customerStampCards ??= new Repository<CustomerStampCard>(_context);

    /// <inheritdoc />
    public IRepository<CardDesign> CardDesigns =>
        _cardDesigns ??= new Repository<CardDesign>(_context);

    /// <inheritdoc />
    public IRepository<CardDesignVersion> CardDesignVersions =>
        _cardDesignVersions ??= new Repository<CardDesignVersion>(_context);

    /// <inheritdoc />
    public IRepository<CardAsset> CardAssets =>
        _cardAssets ??= new Repository<CardAsset>(_context);

    /// <inheritdoc />
    public IRepository<StampCardRulesChange> StampCardRulesChanges =>
        _stampCardRulesChanges ??= new Repository<StampCardRulesChange>(_context);

    /// <inheritdoc />
    public IRepository<LoyaltyEarningRule> LoyaltyEarningRules =>
        _loyaltyEarningRules ??= new Repository<LoyaltyEarningRule>(_context);

    /// <inheritdoc />
    public IRepository<StampTransaction> StampTransactions =>
        _stampTransactions ??= new Repository<StampTransaction>(_context);

    /// <inheritdoc />
    public IRepository<Reward> Rewards =>
        _rewards ??= new Repository<Reward>(_context);

        /// <inheritdoc />
    public IRepository<RewardEntitlement> RewardEntitlements =>
        _rewardEntitlements ??= new Repository<RewardEntitlement>(_context);

    /// <inheritdoc />
    public IRepository<Payment> Payments =>
        _payments ??= new Repository<Payment>(_context);

    /// <inheritdoc />
    public IRepository<PaymentAttempt> PaymentAttempts =>
        _paymentAttempts ??= new Repository<PaymentAttempt>(_context);

    /// <inheritdoc />
    public IRepository<PaymentCallback> PaymentCallbacks =>
        _paymentCallbacks ??= new Repository<PaymentCallback>(_context);

    /// <inheritdoc />
    public IRepository<BusinessPaymentConfig> BusinessPaymentConfigs =>
        _businessPaymentConfigs ??= new Repository<BusinessPaymentConfig>(_context);

    /// <inheritdoc />
    public IRepository<NotificationPreference> NotificationPreferences =>
        _notificationPreferences ??= new Repository<NotificationPreference>(_context);

    /// <inheritdoc />
    public async Task<int> SaveChangesAsync()
    {
        return await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Disposes the DbContext and releases resources.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _context.Dispose();
        }
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
