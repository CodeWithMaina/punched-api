using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using PunchedApi.API.Middleware;
using PunchedApi.Application.Attendance;
using PunchedApi.Application.Attendance.Verification;
using PunchedApi.Application.Assets;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.Loyalty;
using PunchedApi.Application.Mappings;
using PunchedApi.Application.Modules;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Media;
using PunchedApi.Application.Services;
using PunchedApi.Application.Settings;
using PunchedApi.Application.Validators;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Data.Seeding;
using PunchedApi.Infrastructure.Services.Payments;
using PunchedApi.Infrastructure.Data.Seeding.Steps;
using PunchedApi.Infrastructure.Notifications;
using PunchedApi.Infrastructure.Repositories;
using PunchedApi.Infrastructure.Services;
using PunchedApi.Infrastructure.Services.Storage;
using Serilog;

// ═══════════════════════════════════════════════════════════════
//  SERILOG BOOTSTRAP
// ═══════════════════════════════════════════════════════════════
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting Punched API...");

    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog ─────────────────────────────────────────────
    builder.Host.UseSerilog((context, config) =>
        config.ReadFrom.Configuration(context.Configuration)
              .WriteTo.Console());

    // ═══════════════════════════════════════════════════════════
    //  SERVICE REGISTRATIONS
    // ═══════════════════════════════════════════════════════════

    // ── Database (PostgreSQL via Neon) ──────────────────────
    // DbContext pooling: contexts are stateless here and resolved per-scope,
    // so pooled instances are safely reset and reused across requests. This
    // removes per-request context allocation cost without changing semantics.
    var connectionString = ResolveConnectionString(builder.Configuration);

    builder.Services.AddDbContextPool<ApplicationDbContext>(options =>
        options.UseNpgsql(
            connectionString,
            o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));

    // ── Caching / tenant scope resolution ───────────────────
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<IBusinessScopeResolver, BusinessScopeResolver>();

    // ── JWT Settings ────────────────────────────────────────
    var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName);
    builder.Services.Configure<JwtSettings>(jwtSettings);
    builder.Services.Configure<SeedOptions>(
        builder.Configuration.GetSection(SeedOptions.SectionName));

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        var secret = jwtSettings["Secret"]
            ?? throw new InvalidOperationException("JWT Secret is not configured.");

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            ValidateIssuer = true,
            ValidIssuer = jwtSettings["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwtSettings["Audience"],
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };

        // Allow SSE connections to pass token via query string (EventSource has no header support)
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"].FirstOrDefault();
                if (!string.IsNullOrEmpty(token) &&
                    ctx.Request.Path.StartsWithSegments("/v1/sse"))
                {
                    ctx.Token = token;
                }
                return Task.CompletedTask;
            }
        };
    });

    builder.Services.AddAuthorization(options =>
    {
        DenyByDefaultAuthorization.Configure(options);
    });

    // ── Repositories & Unit of Work ─────────────────────────
    builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
    builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

    // ── Email Settings ───────────────────────────────────────
    builder.Services.Configure<EmailSettings>(
        builder.Configuration.GetSection(EmailSettings.SectionName));

    // ── Application Services ────────────────────────────────
    builder.Services.AddScoped<JwtTokenService>();
    builder.Services.AddScoped<IAuthService, AuthService>();

    // Use ConsoleEmailService for development; switch to SmtpEmailService for production
    if (builder.Environment.IsDevelopment())
        builder.Services.AddScoped<IEmailService, ConsoleEmailService>();
    else
        builder.Services.AddScoped<IEmailService, SmtpEmailService>();

    builder.Services.Configure<PublicAppSettings>(
        builder.Configuration.GetSection(PublicAppSettings.SectionName));

    // Stamping background jobs (win-back nudges) — Phase 4
    builder.Services.Configure<StampingSettings>(
        builder.Configuration.GetSection(StampingSettings.SectionName));
    builder.Services.AddScoped<IStampingMaintenanceService, StampingMaintenanceService>();

    // Staff invitations (invitation-only staff onboarding)
    builder.Services.AddScoped<IInvitationService, InvitationService>();

    builder.Services.AddScoped<IUserService, UserService>();
    builder.Services.AddScoped<IBusinessService, BusinessService>();
    builder.Services.AddScoped<ILandingPageService, LandingPageService>();

    // Business subdomain URLs (java-house.punched.app): slug generation,
    // validation, and the one-time-per-boot legacy backfill.
    builder.Services.AddScoped<IBusinessSlugGenerator, BusinessSlugGenerator>();
    builder.Services.AddScoped<IBusinessSlugBackfill, BusinessSlugBackfill>();

    builder.Services.AddScoped<ILoyaltyService, LoyaltyService>();
            builder.Services.AddScoped<IStampService, StampService>();

    // ── Loyalty module (earning rules, stamp ledger, rewards) ──
    // Subscription-gated via the existing module entitlement system:
    // ModuleCatalog["loyalty"] + [RequireModule("loyalty")] + service-layer checks.
    builder.Services.AddScoped<ILoyaltyScopeResolver, LoyaltyScopeResolver>();
    builder.Services.AddScoped<ILoyaltyStampingService, LoyaltyStampingService>();
    builder.Services.AddScoped<ILoyaltyEarningRuleService, LoyaltyEarningRuleService>();
    builder.Services.AddScoped<ILoyaltyRewardService, LoyaltyRewardService>();

    // Domain-event dispatch for automatic earning. Producers (Appointments,
    // Referrals) publish facts they own; Loyalty is the only subscriber.
    builder.Services.AddScoped<ILoyaltyEventBus, LoyaltyEventBus>();
    builder.Services.AddScoped<ILoyaltyEventHandler, LoyaltyAutomaticEarningHandler>();
    builder.Services.AddScoped<IStampCardService, StampCardService>();
    builder.Services.AddScoped<ICardDesignService, CardDesignService>();
    builder.Services.AddScoped<ICardDesignResolver, CardDesignResolver>();
    // Card branding assets: local filesystem storage behind the ICardAssetStorage
    // abstraction, so an object store can replace it without touching the service.
    builder.Services.AddScoped<ICardAssetService, CardAssetService>();
    builder.Services.AddSingleton<ICardAssetStorage, LocalCardAssetStorage>();
    builder.Services.AddDataProtection().SetApplicationName("PunchedApi");
    builder.Services.AddSingleton<ICardAssetDeliveryTokenService, CardAssetDeliveryTokenService>();
    builder.Services.Configure<CardAssetSettings>(
        builder.Configuration.GetSection(CardAssetSettings.SectionName));

    // Centralized media: production always uses R2; the in-memory store is test-only.
    builder.Services.Configure<MediaStorageOptions>(
        builder.Configuration.GetSection(MediaStorageOptions.SectionName));
    builder.Services.AddSingleton<IMediaKeyFactory, MediaKeyFactory>();
    builder.Services.AddSingleton<IMediaUrlFactory, MediaUrlFactory>();
    builder.Services.AddSingleton<IMediaValidator, MediaValidator>();
    builder.Services.AddScoped<IMediaProcessor, MediaProcessor>();
    builder.Services.AddScoped<IObjectStore, R2ObjectStore>();
    builder.Services.AddHostedService<MediaProcessingWorker>();
    builder.Services.AddHostedService<MediaCleanupWorker>();
    builder.Services.AddScoped<IMediaService, MediaService>();
    builder.Services.AddScoped<PunchedApi.Application.Programs.IProgramRuleEngine, PunchedApi.Application.Programs.ProgramRuleEngine>();
    builder.Services.AddScoped<IIdempotencyService, IdempotencyService>();
    builder.Services.AddScoped<INotificationsService, NotificationsService>();

    // ── Notification module (Phase 1: registry, preferences, in-app inbox) ──
    // Producers touch INotificationService.SendAsync only; providers, the outbox
    // worker and each external channel stay behind INotificationChannel, registered
    // one AddScoped line at a time (email = Phase 4, sms = Phase 6, push = Phase 7).
    builder.Services.AddScoped<INotificationService, NotificationService>();
    builder.Services.AddScoped<IPreferenceResolver, PreferenceResolver>();
    builder.Services.Configure<NotificationWorkerOptions>(
        builder.Configuration.GetSection(NotificationWorkerOptions.SectionName));
    builder.Services.Configure<SmsSettings>(builder.Configuration.GetSection(SmsSettings.SectionName));
    builder.Services.Configure<VapidSettings>(builder.Configuration.GetSection(VapidSettings.SectionName));
    builder.Services.AddScoped<NotificationOutboxStore>();
    builder.Services.AddScoped<ITemplateRenderer, TemplateRenderer>();
    builder.Services.AddScoped<INotificationSmtpDelivery, MailKitNotificationSmtpDelivery>();
    builder.Services.AddScoped<INotificationChannel, EmailChannel>();
    builder.Services.AddHttpClient<AfricaTalkingClient>();
    builder.Services.AddScoped<INotificationChannel, SmsChannel>();
    builder.Services.AddScoped<INotificationChannel, PushChannel>();
    builder.Services.AddScoped<IQrService, QrService>();
    builder.Services.AddScoped<ICustomerEnrollmentService, CustomerEnrollmentService>();
    builder.Services.AddScoped<IRedemptionService, RedemptionService>();
    builder.Services.AddScoped<IReferralService, ReferralService>();
    builder.Services.AddScoped<IAdminService, AdminService>();
    builder.Services.AddScoped<AdminNotificationOperationsService>();
    builder.Services.AddScoped<IAnalyticsAggregationService, AnalyticsAggregationService>();
    builder.Services.AddScoped<ISegmentationService, SegmentationService>();
    builder.Services.AddScoped<IInsightService, InsightService>();
    builder.Services.AddScoped<IPayoutService, PayoutService>();
    builder.Services.AddScoped<IRewardPayoutGateway, FakeMpesaPayoutGateway>();

    // ── Booking (Phase 2/3) ─────────────────────────────────
    builder.Services.AddScoped<IAppointmentService, AppointmentService>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddScoped<IReviewService, ReviewService>();
    builder.Services.AddScoped<AppointmentAvailabilityService>();
    builder.Services.AddScoped<IServiceCatalogService, ServiceCatalogService>();

    // ── Attendance module (Phase 2: verification engine + QR credentials) ──
    // Verifiers are registered as IAttendanceVerifier so the engine receives
    // them all through IEnumerable and policy decides which ones run (§7.2).
    builder.Services.AddScoped<IAttendanceVerifier, AuthenticatedUserVerifier>();
    builder.Services.AddScoped<IAttendanceVerifier, QrVerifier>();
    builder.Services.AddScoped<IAttendanceVerificationEngine, AttendanceVerificationEngine>();
    builder.Services.AddScoped<IAttendanceLocationService, AttendanceLocationService>();
    builder.Services.AddScoped<IAttendancePolicyService, AttendancePolicyService>();
    // Phase 3: the staff hot path (status / clock-in / clock-out / history).
    builder.Services.AddScoped<IAttendanceService, AttendanceService>();


    // ── Module entitlements (plugin architecture Phases 1-3) ─
    builder.Services.AddScoped<IModuleEntitlementService, ModuleEntitlementService>();

    // ── Subscription lifecycle & billing (Steps 7) ──────────
    builder.Services.AddScoped<ISubscriptionLifecycleService, SubscriptionLifecycleService>();
    builder.Services.AddScoped<SubscriptionExpiryService>();
    builder.Services.AddScoped<IBillingGateway, FakeMpesaStkGateway>();
    // ── Payments module (direct-to-business payments) ───────
    // Business-owned payment accounts; no platform fees, splits or pooled funds.
    builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection(PaymentOptions.SectionName));
    builder.Services.Configure<DarajaOptions>(builder.Configuration.GetSection(DarajaOptions.SectionName));
    builder.Services.AddHttpClient<DarajaClient>();
    builder.Services.AddScoped<FakeDarajaClient>();
    builder.Services.AddScoped<IDarajaClient>(sp =>
    {
        var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DarajaOptions>>().Value;
        var env = sp.GetRequiredService<IHostEnvironment>();
        // Fake only in development with no real credentials — never fake production success.
        if (env.IsDevelopment() && opts.AllowMockClient && string.IsNullOrEmpty(opts.ConsumerKey))
            return sp.GetRequiredService<FakeDarajaClient>();
        return sp.GetRequiredService<DarajaClient>();
    });
    builder.Services.AddSingleton<PunchedApi.Application.Services.PaymentCredentialProtector>();
    builder.Services.AddScoped<IPaymentProvider, CashPaymentProvider>();
    builder.Services.AddScoped<IPaymentProvider, DarajaStkProvider>();
    builder.Services.AddScoped<IPaymentService, PaymentService>();
    builder.Services.AddScoped<IPaymentConfigService, PaymentConfigService>();
    builder.Services.AddScoped<IPaymentCallbackService, PaymentCallbackService>();
builder.Services.Configure<BillingOptions>(builder.Configuration.GetSection(BillingOptions.SectionName));
builder.Services.AddScoped<ISubscriptionProvisioningService, SubscriptionProvisioningService>();

    // ── Subscription admin module (tier lifecycle + audit) ──
    builder.Services.AddScoped<ISubscriptionAuditService, SubscriptionAuditService>();
    builder.Services.AddScoped<IAdminTierService, AdminTierService>();
    builder.Services.AddScoped<IAdminSubscriptionService, AdminSubscriptionService>();

    // ── Module enforcement (plugin architecture Phases 4-6) ──
    // Fail-closed by default — enforcement is unconditional (Step 8).
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<IBusinessContext, BusinessContext>();
    builder.Services.AddScoped<IPermissionService, PermissionService>();

    // ── Tenant context (business subdomains: java-house.punched.app) ──
    // Host chooses the page; the token chooses the data. None of these
    // services grant access — membership/authorization stay server-side.
    builder.Services.Configure<SessionCookieSettings>(builder.Configuration.GetSection(SessionCookieSettings.SectionName));
    builder.Services.AddSingleton<ITenantHostResolver, TenantHostResolver>();
    builder.Services.AddSingleton<ITenantUrlBuilder, TenantUrlBuilder>();
    builder.Services.AddSingleton<ISessionCookieService, SessionCookieService>();
    builder.Services.AddScoped<IBusinessMembershipResolver, BusinessMembershipResolver>();
    // Active tenant for the request: written once by TenantConsistencyMiddleware
    // (slug → business id from the page host), read by tenant-scoped services.
    // Scoped so the context can never leak across requests or users.
    builder.Services.AddScoped<TenantContext>();
    builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

    // ── Seed framework ─────────────────────────────────────
    builder.Services.AddSingleton<ISeedRandom, SeedRandom>();
    builder.Services.AddScoped<IDatabaseSeeder, DatabaseSeeder>();
    builder.Services.AddScoped<IDatabaseCliRunner, DatabaseCliRunner>();
    builder.Services.AddScoped<IAdminBootstrapper, AdminBootstrapper>();
    builder.Services.AddScoped<IModuleCatalogSeeder, ModuleCatalogSeeder>();
    builder.Services.AddScoped<ISeedStep, DatabasePreparationSeedStep>();
    builder.Services.AddScoped<ISeedStep, IdentitySeedStep>();
    builder.Services.AddScoped<ISeedStep, BusinessSeedStep>();
    builder.Services.AddScoped<ISeedStep, StaffLinkSeedStep>();
    builder.Services.AddScoped<ISeedStep, LoyaltyProgramSeedStep>();
    builder.Services.AddScoped<ISeedStep, ReferralProgramSeedStep>();
    builder.Services.AddScoped<ISeedStep, LoyaltyActivitySeedStep>();
    builder.Services.AddScoped<ISeedStep, AnalyticsBackfillSeedStep>();
    builder.Services.AddScoped<ISeedStep, ReferralSeedStep>();
    builder.Services.AddScoped<ISeedStep, SessionSeedStep>();
    builder.Services.AddScoped<ISeedStep, UnsupportedDomainsSeedStep>();
    builder.Services.AddScoped<ISeedStep, ValidationAndReportSeedStep>();

    // SSE broker: singleton so all requests share the same in-process channels
    builder.Services.AddSingleton<ISseService, SseService>();

    // Periodic cleanup of expired tokens, QR tokens, and stale verification codes
    builder.Services.AddHostedService<CleanupService>();
    builder.Services.AddHostedService<NotificationDeliveryWorker>();
    builder.Services.AddHostedService<PaymentExpiryWorker>();
    builder.Services.AddHostedService<PayoutWorker>();
    builder.Services.AddHostedService<AnalyticsWorker>();
    builder.Services.AddHostedService<SubscriptionExpiryWorker>();

    // ── AutoMapper ──────────────────────────────────────────
    builder.Services.AddAutoMapper(typeof(MappingProfile));

    // ── FluentValidation ────────────────────────────────────
    builder.Services.AddFluentValidationAutoValidation();
    builder.Services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();

    // ── Controllers ─────────────────────────────────────────
    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            // Allow string enum values in request/response payloads.
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

    // ── CORS ────────────────────────────────────────────────
    // Origins are matched through CorsOriginPolicy so business subdomains can
    // be allow-listed with a single-label wildcard (http://*.localhost:3000,
    // https://*.punched.app). CorsPolicyBuilder.WithOrigins compares literals
    // only and silently never matched those patterns, so NO tenant origin ever
    // received Access-Control-Allow-Origin and every storefront request from
    // java-house.localhost:3000 was blocked by the browser.
    var corsOrigins = builder.Configuration.GetSection("CorsOrigins").Get<string[]>()
        ?? CorsOriginPolicy.DefaultAllowedOrigins;

    builder.Services.AddCors(options =>
    {
        options.AddPolicy(CorsOriginPolicy.PolicyName, policy =>
            policy.SetIsOriginAllowed(origin => CorsOriginPolicy.IsAllowedOrigin(origin, corsOrigins))
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials());
    });

    // ── Rate Limiting (.NET 8 built-in) ─────────────────────
    // Permit limits are configuration-driven (RateLimiting:<policy>:PermitLimit) with
    // the production values below as defaults, so an environment can tune them without a
    // code change. The Playwright E2E harness raises them because a browser-driven suite
    // issues many auth calls from a single IP.
    int RateLimit(string policy, int fallback) =>
        int.TryParse(builder.Configuration[$"RateLimiting:{policy}:PermitLimit"], out var configured) && configured > 0
            ? configured
            : fallback;

    builder.Services.AddRateLimiter(options =>
    {
        // OTP / verification code requests: 3 per 15 minutes per IP
        options.AddPolicy("otp", httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RateLimit("otp", 3),
                    Window = TimeSpan.FromMinutes(15),
                    QueueLimit = 0
                }));

        // Login attempts: 5 per 30 minutes per IP
        options.AddPolicy("login", httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RateLimit("login", 5),
                    Window = TimeSpan.FromMinutes(30),
                    QueueLimit = 0
                }));

        // Session refresh / cross-subdomain hydration: deliberately generous.
        //
        // Every origin silently exchanges the shared session cookie for its own
        // access token the first time it is loaded (platform root, then each
        // business subdomain the user visits), and a 401 anywhere retries once.
        // These are legitimate, self-authenticating calls — NOT credential
        // guesses — so they must not consume the "login" budget, and the budget
        // is per-IP (a shop's shared Wi-Fi is one IP).
        options.AddPolicy("refresh", httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RateLimit("refresh", 300),
                    Window = TimeSpan.FromMinutes(5),
                    QueueLimit = 0
                }));

        // General API: 1000 per hour per IP
        options.AddPolicy("general", httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RateLimit("general", 1000),
                    Window = TimeSpan.FromHours(1),
                                        QueueLimit = 0
                }));

        // Media upload grants: 30 per hour per authenticated user and IP.
        options.AddPolicy("media-upload", httpContext =>
        {
            var userId = httpContext.User?.FindFirst("userId")?.Value ?? "anon";
            var key = $"{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}:{userId}";
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = RateLimit("media-upload", 30), Window = TimeSpan.FromHours(1), QueueLimit = 0
            });
        });

        // Manual phone lookup: 5 per hour per (IP + user)
        options.AddPolicy("manual-lookup", httpContext =>
        {
            var userId = httpContext.User?.FindFirst("userId")?.Value ?? "anon";
            var partitionKey = $"{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}:{userId}";
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RateLimit("manual-lookup", 5),
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0
                });
        });

        // Stamp awarding: 20 per hour per (IP + user)
        options.AddPolicy("stamp-award", httpContext =>
        {
            var userId = httpContext.User?.FindFirst("userId")?.Value ?? "anon";
            var partitionKey = $"{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}:{userId}";
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RateLimit("stamp-award", 20),
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0
                });
        });

        // Enroll-and-stamp: 20 per hour per (IP + user)
        options.AddPolicy("stamp-enroll", httpContext =>
        {
            var userId = httpContext.User?.FindFirst("userId")?.Value ?? "anon";
            var partitionKey = $"{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}:{userId}";
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RateLimit("stamp-enroll", 20),
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0
                });
        });

        // Attendance clock-in/out: 60 scans per hour per (IP + user) — the
        // double-tap safe-by-construction bound (429 never reaches the service).
        options.AddPolicy("attendance-clock", httpContext =>
        {
            var userId = httpContext.User?.FindFirst("userId")?.Value ?? "anon";
            var partitionKey = $"{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}:{userId}";
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RateLimit("attendance-clock", 60),
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0
                });
        });

        // Card-asset uploads: 30 per hour per (IP + user). Bounds upload flooding
        // and storage abuse independently of the general API bucket (§21).
        options.AddPolicy("asset-upload", httpContext =>
        {
            var userId = httpContext.User?.FindFirst("userId")?.Value ?? "anon";
            var partitionKey = $"{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}:{userId}";
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RateLimit("asset-upload", 30),
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0
                });
        });

        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    });

    // ── Swagger / OpenAPI ───────────────────────────────────
    builder.Services.AddEndpointsApiExplorer();

    // ── Response Compression ────────────────────────────────
    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
        options.MimeTypes = ResponseCompressionDefaults.MimeTypes;
    });
    builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
        options.Level = CompressionLevel.Fastest);
    builder.Services.Configure<GzipCompressionProviderOptions>(options =>
        options.Level = CompressionLevel.Fastest);

    // ── Output Caching ──────────────────────────────────────
    // IMPORTANT (tenant security): these endpoints resolve the business from the
    // authenticated user's JWT claims, NOT from the URL. ASP.NET Core OutputCache
    // keys responses by path + query string only, so without varying on the
    // Authorization header two different business owners hitting the same
    // `/v1/businesses/me/dashboard` URL would receive each other's cached data
    // (a cross-tenant leak). Varying by `Authorization` guarantees each user's
    // cached response is keyed to their token. The BusinessId itself is never read
    // from a client-supplied query parameter.
    builder.Services.AddOutputCache(options =>
    {
        // Short cache for analytics endpoints (30s) — vary by token + period/range.
        // X-Punched-Tenant joins Authorization in the key: the same page URL can
        // be requested for different businesses (request 8), and no
        // tenant-specific response may ever be served from a shared entry.
        options.AddPolicy("analytics", builder =>
            builder.Expire(TimeSpan.FromSeconds(30))
                   .SetVaryByHeader("Authorization, " + TenantConsistencyMiddleware.TenantHeader)
                   .SetVaryByQuery("period", "start", "end", "prev")
                   .Tag("analytics"));

        // Very short cache for dashboard metrics (10s) — vary by token + tenant host.
        options.AddPolicy("dashboard", builder =>
            builder.Expire(TimeSpan.FromSeconds(10))
                   .SetVaryByHeader("Authorization, " + TenantConsistencyMiddleware.TenantHeader)
                   .Tag("dashboard"));
    });

    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Punched Loyalty API",
            Version = "v1",
            Description = "MVP API for Punched Loyalty Platform — Authentication & Core Endpoints"
        });

        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Enter your JWT token"
        });

        c.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                },
                Array.Empty<string>()
            }
        });
    });

    // ═══════════════════════════════════════════════════════════
    //  BUILD APP
    // ═══════════════════════════════════════════════════════════
    var app = builder.Build();

    if (args.Length > 0)
    {
        using var cliScope = app.Services.CreateScope();
        var cliRunner = cliScope.ServiceProvider.GetRequiredService<IDatabaseCliRunner>();
        if (await cliRunner.TryRunAsync(args))
        {
            return;
        }
    }

    // ── Apply pending database migrations on startup ────────
    using (var scope = app.Services.CreateScope())
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await dbContext.Database.MigrateAsync();
        Log.Information("Database migrations applied successfully.");

        // Assign subdomain slugs to any business still missing one (legacy
        // rows, or rows inserted by an older app version during a rolling
        // deploy). Idempotent; runs before the seeder so seeded slug
        // availability checks see a fully provisioned table.
        var slugBackfill = scope.ServiceProvider.GetRequiredService<IBusinessSlugBackfill>();
        await slugBackfill.EnsureSlugsAsync();

        var seeder = scope.ServiceProvider.GetRequiredService<IDatabaseSeeder>();
        await seeder.RunAsync();
        var adminBootstrapper = scope.ServiceProvider.GetRequiredService<IAdminBootstrapper>();
        await adminBootstrapper.EnsureDefaultAdminAsync();

        // Module catalog (modules/plans/plan_modules) — idempotent, runs in
        // every environment because entitlement resolution depends on it.
        var moduleCatalogSeeder = scope.ServiceProvider.GetRequiredService<IModuleCatalogSeeder>();
        await moduleCatalogSeeder.EnsureModuleCatalogAsync();
        Log.Information("Module catalog verified.");
    }

    // ── Middleware Pipeline ──────────────────────────────────
    app.UseMiddleware<ExceptionMiddleware>();
    app.UseMiddleware<ApiEventLoggingMiddleware>();

    app.UseResponseCompression();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "Punched API v1");
            c.RoutePrefix = "swagger";
        });
    }

    // CORS must answer before HTTPS redirection: a 307 on a preflight would
    // drop the CORS headers and fail the browser request.
    app.UseCors(CorsOriginPolicy.PolicyName);
    app.UseHttpsRedirection();
    app.UseRateLimiter();
    app.UseAuthentication();
    // Host ↔ token consistency is UX-only: it detects a token minted for a
    // different tenant than the page host and returns 409 TENANT_MISMATCH so
    // the frontend can offer switch/re-auth. It never grants data access —
    // module gating ([RequireModule]) + identity-scoped authorization stay
    // authoritative. Runs after authentication so claims are available.
    app.UseMiddleware<TenantConsistencyMiddleware>();
    // Module gating is enforced per-endpoint via [RequireModule] filters
    // (MODULE_SYSTEM_STATUS_AND_PLAN.md Step 4); no coarse middleware by design.
    app.UseAuthorization();
    app.UseOutputCache();

    app.MapControllers();

    // ── Health check endpoint ───────────────────────────────
    // Explicitly anonymous: the deny-by-default fallback policy protects every
    // endpoint that does not opt out, and liveness probes carry no token.
    app.MapGet("/", () => Results.Ok(new { status = "healthy", service = "Punched API", version = "1.0.0" }))
        .AllowAnonymous();
    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
        .AllowAnonymous();

    app.Run();
}
catch (Microsoft.Extensions.Hosting.HostAbortedException)
{
    // Expected during EF Core design-time commands (migrations/update).
    Log.Information("Host aborted during EF design-time execution.");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

static string ResolveConnectionString(IConfiguration configuration)
{
    var databaseUrl = configuration["DATABASE_URL"];
    if (!string.IsNullOrWhiteSpace(databaseUrl))
    {
        if (!Uri.TryCreate(databaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
        {
            throw new InvalidOperationException("DATABASE_URL must be a valid postgresql:// connection URL.");
        }

        var builder = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = uri.AbsolutePath.Trim('/'),
            Username = Uri.UnescapeDataString(uri.UserInfo.Split(':', 2)[0]),
            Password = uri.UserInfo.Contains(':')
                ? Uri.UnescapeDataString(uri.UserInfo.Split(':', 2)[1])
                : string.Empty
        };

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (string.Equals(query["sslmode"], "require", StringComparison.OrdinalIgnoreCase))
        {
            builder.SslMode = Npgsql.SslMode.Require;
        }

        if (!string.IsNullOrWhiteSpace(query["channel_binding"]))
        {
            builder["Channel Binding"] = query["channel_binding"];
        }

        return builder.ConnectionString;
    }

    return configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "Configure DATABASE_URL or ConnectionStrings__DefaultConnection.");
}
