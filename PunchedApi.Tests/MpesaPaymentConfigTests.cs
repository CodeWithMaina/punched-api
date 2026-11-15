using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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
/// The manual M-PESA configuration end to end at the service boundary: save
/// (validation + normalisation), the single active method, and the public
/// payload — which must expose ONLY the active, enabled, complete method.
/// </summary>
public class MpesaPaymentConfigTests
{
    private static PaymentCredentialProtector Protector()
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Development");
        return new PaymentCredentialProtector(Options.Create(new PaymentOptions()), env.Object);
    }

    /** A database of its own per test, so no test can see another's rows. */
    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static PaymentConfigService ConfigService(ApplicationDbContext context)
        => new(context, new UnitOfWork(context), Protector());

    private static PaymentService PaymentService(ApplicationDbContext context)
        => new(
            context,
            new UnitOfWork(context),
            new IPaymentProvider[] { new CashPaymentProvider(NullLogger<CashPaymentProvider>.Instance) },
            new PermissionService(),
            Protector(),
            Options.Create(new PaymentOptions()),
            BookingTestBase.CreateNotificationService(context),
            NullLogger<PaymentService>.Instance);

    private static async Task<Business> SeedBusinessAsync(ApplicationDbContext context)
    {
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);
        context.ChangeTracker.Clear();
        return business;
    }

    private static async Task<BusinessPaymentConfig> SeedConfigAsync(
        ApplicationDbContext context, Business business, Action<BusinessPaymentConfig> configure)
    {
        var config = new BusinessPaymentConfig
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CreatedAt = DateTime.UtcNow
        };
        configure(config);
        context.BusinessPaymentConfigs.Add(config);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return config;
    }

    private static async Task<PublicPaymentDetailsResponse?> PublicAsync(ApplicationDbContext context, Guid businessId)
    {
        var result = await PaymentService(context).GetPublicPaymentDetailsAsync(businessId);
        Assert.True(result.Success, result.Error?.Message);
        return result.Data;
    }

    // ── Save / validation ───────────────────────────────────

    [Fact]
    public async Task Save_EnablePaybillWithoutAccount_IsRejected_422Shape()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var business = await SeedBusinessAsync(context);

        var result = await ConfigService(context).SaveAsync(business.Id, new SavePaymentConfigRequest
        {
            ManualMpesaEnabled = true,
            MpesaAccountType = "paybill",
            MpesaPaybillNumber = "247247"
        });

        Assert.False(result.Success);
        Assert.Equal("VALIDATION_ERROR", result.Error!.Code);
        Assert.NotNull(result.Error.Fields);
        Assert.Contains("mpesaPaybillAccount", result.Error.Fields!.Keys);
    }

    [Fact]
    public async Task Save_SendMoney_NormalisesPhoneAndPersists()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var business = await SeedBusinessAsync(context);

        var result = await ConfigService(context).SaveAsync(business.Id, new SavePaymentConfigRequest
        {
            ManualMpesaEnabled = true,
            MpesaAccountType = "send_money",
            MpesaPhoneNumber = "0712 440 001",
            MpesaPayeeName = "AURELIA LUXE"
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("send_money", result.Data!.MpesaAccountType);
        Assert.Equal("254712440001", result.Data.MpesaPhoneNumber);
        Assert.True(result.Data.HasManualMpesaDetails);
    }

    [Fact]
    public async Task Save_InvalidPhone_IsRejected()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var business = await SeedBusinessAsync(context);

        var result = await ConfigService(context).SaveAsync(business.Id, new SavePaymentConfigRequest
        {
            ManualMpesaEnabled = true,
            MpesaAccountType = "send_money",
            MpesaPhoneNumber = "0812 000 000"
        });

        Assert.False(result.Success);
        Assert.Contains("mpesaPhoneNumber", result.Error!.Fields!.Keys);
    }

    // ── Public payload ──────────────────────────────────────

    [Fact]
    public async Task Public_Paybill_ExposesOnlyPaybillFields()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var business = await SeedBusinessAsync(context);
        await SeedConfigAsync(context, business, c =>
        {
            c.ManualMpesaEnabled = true;
            c.MpesaAccountType = "paybill";
            c.MpesaPaybillNumber = "247247";
            c.MpesaPaybillAccount = "AURELIA-1042";
            // An inactive Till must never leak.
            c.MpesaTillNumber = "556677";
            c.MpesaPayeeName = "AURELIA LUXE";
            c.BookingFeePercentage = 30m;
        });

        var details = await PublicAsync(context, business.Id)!;

        Assert.True(details!.Enabled);
        Assert.Equal("paybill", details.AccountType);
        Assert.Equal("247247", details.PaybillNumber);
        Assert.Equal("AURELIA-1042", details.PaybillAccount);
        Assert.Null(details.TillNumber);
        Assert.Null(details.PhoneNumber);
    }

    [Fact]
    public async Task Public_Till_ExposesOnlyTillField()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var business = await SeedBusinessAsync(context);
        await SeedConfigAsync(context, business, c =>
        {
            c.ManualMpesaEnabled = true;
            c.MpesaAccountType = "till";
            c.MpesaTillNumber = "5123456";
            c.BookingFeePercentage = 50m;
        });

        var details = await PublicAsync(context, business.Id)!;

        Assert.Equal("till", details!.AccountType);
        Assert.Equal("5123456", details.TillNumber);
        Assert.Null(details.PaybillNumber);
        Assert.Null(details.PhoneNumber);
    }

    [Fact]
    public async Task Public_SendMoney_ExposesOnlyPhone()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var business = await SeedBusinessAsync(context);
        await SeedConfigAsync(context, business, c =>
        {
            c.ManualMpesaEnabled = true;
            c.MpesaAccountType = "send_money";
            c.MpesaPhoneNumber = "254712440001";
            c.MpesaPayeeName = "AURELIA LUXE";
        });

        var details = await PublicAsync(context, business.Id)!;

        Assert.Equal("send_money", details!.AccountType);
        Assert.Equal("254712440001", details.PhoneNumber);
        Assert.Null(details.PaybillNumber);
        Assert.Null(details.TillNumber);
    }

    [Fact]
    public async Task Public_Disabled_ReturnsNone()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var business = await SeedBusinessAsync(context);
        await SeedConfigAsync(context, business, c =>
        {
            c.ManualMpesaEnabled = false;
            c.MpesaAccountType = "paybill";
            c.MpesaPaybillNumber = "247247";
            c.MpesaPaybillAccount = "12345";
        });

        var details = await PublicAsync(context, business.Id)!;

        Assert.False(details!.Enabled);
        Assert.Null(details.AccountType);
        Assert.Null(details.PaybillNumber);
    }

    [Fact]
    public async Task Public_Incomplete_ReturnsNone()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var business = await SeedBusinessAsync(context);
        await SeedConfigAsync(context, business, c =>
        {
            c.ManualMpesaEnabled = true;
            c.MpesaAccountType = "paybill";
            c.MpesaPaybillNumber = "247247";
            // No account number: half-configured, so nothing may be shown.
        });

        var details = await PublicAsync(context, business.Id)!;

        Assert.False(details!.Enabled);
        Assert.Null(details.PaybillNumber);
    }

    [Fact]
    public async Task Public_IsTenantScoped()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var businessA = await SeedBusinessAsync(context);
        await SeedConfigAsync(context, businessA, c =>
        {
            c.ManualMpesaEnabled = true;
            c.MpesaAccountType = "paybill";
            c.MpesaPaybillNumber = "247247";
            c.MpesaPaybillAccount = "12345";
        });

        var ownerB = BookingTestBase.CreateOwner("other@test.com");
        var businessB = BookingTestBase.CreateBusiness(ownerB.Id, "Other");
        await BookingTestBase.SeedAsync(context, ownerB, businessB);
        context.ChangeTracker.Clear();

        // Business B has no configuration of its own, so it must never be told
        // Business A's numbers.
        var details = await PublicAsync(context, businessB.Id)!;

        Assert.False(details!.Enabled);
        Assert.Null(details.PaybillNumber);
    }

    // ── Resolution (backwards compatibility) ────────────────

    [Fact]
    public void ActiveMethod_LegacyTillRow_StoredAsPaybill_StillResolvesToTill()
    {
        var config = new BusinessPaymentConfig
        {
            ManualMpesaEnabled = true,
            MpesaAccountType = "paybill", // the pre-migration default
            MpesaTillNumber = "5123456"
        };

        Assert.Equal(ManualMpesaMethods.Till, config.ActiveManualMethod());
        Assert.True(config.HasManualMpesaDetails);
    }

    [Fact]
    public void ActiveMethod_ExplicitTillWins_WhenBothMethodsConfigured()
    {
        var config = new BusinessPaymentConfig
        {
            ManualMpesaEnabled = true,
            MpesaAccountType = "till",
            MpesaPaybillNumber = "247247",
            MpesaPaybillAccount = "12345",
            MpesaTillNumber = "5123456"
        };

        Assert.Equal(ManualMpesaMethods.Till, config.ActiveManualMethod());
    }

    [Fact]
    public void ActiveMethod_SendMoneyRequiresPhone()
    {
        var missingPhone = new BusinessPaymentConfig { MpesaAccountType = "send_money" };
        Assert.Null(missingPhone.ActiveManualMethod());

        var withPhone = new BusinessPaymentConfig { MpesaAccountType = "send_money", MpesaPhoneNumber = "254712440001" };
        Assert.Equal(ManualMpesaMethods.SendMoney, withPhone.ActiveManualMethod());
    }

    // ── Storefront payment options ──────────────────────────
    // Two independent switches that decide what a customer is offered on the booking page:
    // "Payment Included" (pay during booking) and "Pay After Service". These tests pin the
    // defaults a migration and a brand-new business must both produce, and the one
    // combination the endpoint refuses.

    [Fact]
    public async Task Storefront_defaults_are_payment_included_on_and_pay_after_service_off()
    {
        using var db = NewDb();
        var business = await SeedBusinessAsync(db);

        var details = await PublicAsync(db, business.Id);

        Assert.NotNull(details);
        Assert.True(details!.PayUpfrontEnabled);
        Assert.False(details.PayAfterServiceEnabled);
        Assert.True(details.BookingAvailable);
        Assert.False(details.OffersPaymentChoice);
    }

    [Fact]
    public async Task Storefront_switches_are_saved_independently_and_read_back()
    {
        using var db = NewDb();
        var business = await SeedBusinessAsync(db);
        var service = ConfigService(db);

        var payAfterOnly = await service.SaveAsync(business.Id, new SavePaymentConfigRequest
        {
            CashEnabled = true, MpesaEnabled = false, MpesaAccountType = "paybill",
            ManualMpesaEnabled = false, PayUpfrontEnabled = false, PayAfterServiceEnabled = true
        });
        Assert.True(payAfterOnly.Success, payAfterOnly.Error?.Message);
        Assert.False(payAfterOnly.Data!.PayUpfrontEnabled);
        Assert.True(payAfterOnly.Data.PayAfterServiceEnabled);

        // Independently: turning the other one on does not disturb this one.
        var both = await service.SaveAsync(business.Id, new SavePaymentConfigRequest
        {
            CashEnabled = true, MpesaEnabled = false, MpesaAccountType = "paybill",
            ManualMpesaEnabled = false, PayUpfrontEnabled = true, PayAfterServiceEnabled = true
        });
        Assert.True(both.Success, both.Error?.Message);

        db.ChangeTracker.Clear();
        var stored = await db.BusinessPaymentConfigs.AsNoTracking().SingleAsync(c => c.BusinessId == business.Id);
        Assert.True(stored.PayUpfrontEnabled);
        Assert.True(stored.PayAfterServiceEnabled);
        Assert.True(stored.OffersPaymentChoice);
    }

    [Fact]
    public async Task Storefront_refuses_a_configuration_that_switches_both_options_off()
    {
        using var db = NewDb();
        var business = await SeedBusinessAsync(db);
        var service = ConfigService(db);

        var refused = await service.SaveAsync(business.Id, new SavePaymentConfigRequest
        {
            CashEnabled = true, MpesaEnabled = false, MpesaAccountType = "paybill",
            ManualMpesaEnabled = false, PayUpfrontEnabled = false, PayAfterServiceEnabled = false
        });

        Assert.False(refused.Success);
        Assert.Equal("VALIDATION_ERROR", refused.Error?.Code);
        // Both fields carry a message, so the owner UI can put it under each switch.
        Assert.True(refused.Error!.Fields!.ContainsKey("payUpfrontEnabled"));
        Assert.True(refused.Error.Fields.ContainsKey("payAfterServiceEnabled"));

        // Nothing was written: the business keeps taking bookings on its previous terms.
        db.ChangeTracker.Clear();
        Assert.False(await db.BusinessPaymentConfigs.AnyAsync(c => c.BusinessId == business.Id));
        var details = await PublicAsync(db, business.Id);
        Assert.True(details!.BookingAvailable);
    }

    [Fact]
    public async Task Storefront_save_that_omits_the_switches_keeps_the_stored_policy()
    {
        using var db = NewDb();
        var business = await SeedBusinessAsync(db);
        await SeedConfigAsync(db, business, c =>
        {
            c.PayUpfrontEnabled = false;
            c.PayAfterServiceEnabled = true;
        });

        // A client that does not know about these switches (an older build) must not be able
        // to turn a business's storefront policy off by leaving the fields out.
        var saved = await ConfigService(db).SaveAsync(business.Id, new SavePaymentConfigRequest
        {
            CashEnabled = true, MpesaEnabled = false, MpesaAccountType = "paybill",
            ManualMpesaEnabled = false
        });

        Assert.True(saved.Success, saved.Error?.Message);
        db.ChangeTracker.Clear();
        var stored = await db.BusinessPaymentConfigs.AsNoTracking().SingleAsync(c => c.BusinessId == business.Id);
        Assert.False(stored.PayUpfrontEnabled);
        Assert.True(stored.PayAfterServiceEnabled);
    }

    [Fact]
    public async Task Storefront_defaults_apply_to_a_business_whose_row_is_created_by_the_first_save()
    {
        using var db = NewDb();
        var business = await SeedBusinessAsync(db);
        Assert.False(await db.BusinessPaymentConfigs.AnyAsync(c => c.BusinessId == business.Id));

        var created = await ConfigService(db).SaveAsync(business.Id, new SavePaymentConfigRequest
        {
            CashEnabled = true, MpesaEnabled = false, MpesaAccountType = "paybill",
            ManualMpesaEnabled = false
        });

        Assert.True(created.Success, created.Error?.Message);
        Assert.True(created.Data!.PayUpfrontEnabled);
        Assert.False(created.Data.PayAfterServiceEnabled);
    }

    [Fact]
    public async Task Storefront_policy_is_scoped_to_its_own_business()
    {
        using var db = NewDb();
        var mine = await SeedBusinessAsync(db);
        var theirs = await SeedBusinessAsync(db);

        await ConfigService(db).SaveAsync(theirs.Id, new SavePaymentConfigRequest
        {
            CashEnabled = true, MpesaEnabled = false, MpesaAccountType = "paybill",
            ManualMpesaEnabled = false, PayUpfrontEnabled = false, PayAfterServiceEnabled = true
        });
        db.ChangeTracker.Clear();

        // This business still has no row, so it still gets the defaults — the other
        // business's policy is not visible here in any form.
        Assert.False(await db.BusinessPaymentConfigs.AnyAsync(c => c.BusinessId == mine.Id));
        Assert.True((await PublicAsync(db, mine.Id))!.PayUpfrontEnabled);
        Assert.False((await PublicAsync(db, mine.Id))!.PayAfterServiceEnabled);

        var stored = await db.BusinessPaymentConfigs.AsNoTracking().SingleAsync(c => c.BusinessId == theirs.Id);
        Assert.False(stored.PayUpfrontEnabled);
        Assert.True(stored.PayAfterServiceEnabled);
    }
}
