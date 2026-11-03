using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DomainEvents;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>
/// Test double for <see cref="IBusinessScopeResolver"/>. The production resolver
/// is a singleton that creates its own DI scope per lookup; the tests already
/// own the DbContext, so this stub answers from it directly.
/// </summary>
internal sealed class TestBusinessScopeResolver : IBusinessScopeResolver
{
    private readonly ApplicationDbContext _context;

    public TestBusinessScopeResolver(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Guid?> GetMemberBusinessIdAsync(Guid userId)
    {
        var memberships = await _context.BusinessMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.Status == BusinessMembershipStatus.Active)
            .OrderByDescending(m => m.Role == BusinessMembershipRole.Owner)
            .ThenByDescending(m => m.Role == BusinessMembershipRole.Staff)
            .Select(m => (Guid?)m.BusinessId)
            .ToListAsync();

        return memberships.FirstOrDefault();
    }

    public async Task<Guid?> GetOwnedBusinessIdAsync(Guid ownerId) =>
        await _context.BusinessMemberships
            .AsNoTracking()
            .Where(m => m.UserId == ownerId &&
                        m.Role == BusinessMembershipRole.Owner &&
                        m.Status == BusinessMembershipStatus.Active)
            .Select(m => (Guid?)m.BusinessId)
            .FirstOrDefaultAsync();

    public void InvalidateOwner(Guid ownerId) { }
}

/// <summary>
/// Fixture helpers for the commission / RBAC / appointment-lifecycle tests.
/// Shares the SQLite in-memory conventions of <see cref="BookingTestBase"/>.
/// </summary>
internal static class BusinessOperationsTestBase
{
    /// <summary>A resolver that answers from the given context (no DI scope needed).</summary>
    public static IBusinessScopeResolver CreateScopeResolver(ApplicationDbContext context) =>
        new TestBusinessScopeResolver(context);

    public static IBusinessPermissionService CreatePermissionService(ApplicationDbContext context) =>
        new BusinessPermissionService(
            context,
            new MemoryCache(new MemoryCacheOptions()),
            CreateScopeResolver(context));

    public static ICommissionResolver CreateCommissionResolver(ApplicationDbContext context) =>
        new CommissionResolver(context);

    /// <summary>
    /// A <see cref="CommissionService"/> whose notification side effects are
    /// recorded in the same context (the outbox writes real rows).
    /// </summary>
    public static ICommissionService CreateCommissionService(ApplicationDbContext context) =>
        new CommissionService(
            context,
            CreateCommissionResolver(context),
            CreateScopeResolver(context),
            new PermissionService(CreatePermissionService(context)),
            BookingTestBase.CreateNotificationService(context),
            TestHelpers.CreateLogger<CommissionService>());

    /// <summary>
    /// An <see cref="AppointmentService"/> wired to a real commission ledger, so
    /// completion actually accrues.
    ///
    /// <para><paramref name="timeProvider"/> pins the clock the booking rules read
    /// (lead time, "must be in the future", proposal expiry), so a test can assert
    /// them without depending on when the suite runs.</para>
    /// </summary>
    public static AppointmentService CreateAppointmentService(
        ApplicationDbContext context,
        ICommissionService? commissions = null,
        TimeProvider? timeProvider = null) =>
        new(
            new UnitOfWork(context),
            context,
            new AppointmentAvailabilityService(
                context, TestHelpers.CreateLogger<AppointmentAvailabilityService>(), timeProvider),
            BookingTestBase.CreateMapper(),
            new PermissionService(CreatePermissionService(context)),
            BookingTestBase.CreateNotificationService(context),
            new Mock<IDomainEventPublisher>().Object,
            TestHelpers.CreateLogger<AppointmentService>(),
            tenant: null,
            commissions: commissions ?? CreateCommissionService(context),
            timeProvider: timeProvider);

    /// <summary>Provisions the platform default roles for a business.</summary>
    public static Task ProvisionAsync(ApplicationDbContext context, Guid businessId) =>
        CreatePermissionService(context).EnsureProvisionedAsync(businessId);

    /// <summary>An enabled percentage commission configuration for a business.</summary>
    public static CommissionConfig EnabledPercentageConfig(
        Guid businessId,
        decimal percentage = 10m,
        bool requireSettlement = true,
        bool requireApproval = false,
        bool requireEligibility = true) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        IsEnabled = true,
        CalculationType = CommissionCalculationType.Percentage,
        DefaultPercentage = percentage,
        Currency = "KES",
        RequireSettlement = requireSettlement,
        RequireApproval = requireApproval,
        RequireStaffServiceEligibility = requireEligibility,
        AllowPartialSettlement = true,
        RoundingDecimals = 2,
        UpdatedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow
    };

    /// <summary>A successful payment covering the appointment.</summary>
    public static Payment SuccessfulPayment(Guid businessId, Guid appointmentId, Guid customerId, decimal amount) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        AppointmentId = appointmentId,
        CustomerId = customerId,
        Type = PaymentType.FullPayment,
        Method = PaymentMethod.Cash,
        Provider = PaymentProviderKind.Cash,
        Status = PaymentStatus.Success,
        Amount = amount,
        Currency = "KES",
        Reference = $"PMT-{Guid.NewGuid():N}"[..20],
        CompletedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow
    };
}
