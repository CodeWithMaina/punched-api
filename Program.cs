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
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Http.Timeouts;
using PunchedApi.API.Hosting;
using PunchedApi.API.Middleware;
using PunchedApi.Application;
using PunchedApi.Application.Attendance;
using PunchedApi.Application.Attendance.Verification;
using PunchedApi.Application.Assets;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.BackgroundProcessing;
using PunchedApi.Application.DomainEvents;
using PunchedApi.Application.Handlers;
using PunchedApi.Application.Loyalty;
using PunchedApi.Application.Modules;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Media;
using PunchedApi.Application.Scheduling;
using PunchedApi.Application.Services;
using PunchedApi.Application.Settings;
using PunchedApi.Application.Validators;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Caching;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Data.Seeding;
using PunchedApi.Infrastructure.Data.Seeding.Demo.AureliaLuxe;
using PunchedApi.Infrastructure.Services.Payments;
using PunchedApi.Infrastructure.Data.Seeding.Steps;
using PunchedApi.Infrastructure.Notifications;
using PunchedApi.Infrastructure.Repositories;
using PunchedApi.Infrastructure.Services;
using PunchedApi.Infrastructure.Services.Storage;
using Serilog;
using Serilog.Events;

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

    // ═══════════════════════════════════════════════════════════
    //  PROCESS ROLE, HOST LIMITS AND TIMEOUTS
    // ═══════════════════════════════════════════════════════════

    // PUNCHED_ROLE selects the container's job (see API/Hosting/HostRole.cs).
    // Unset means "api", which is the historical single-process behaviour.
    var hostRole = HostRole.Resolve(builder.Configuration);
    var isWorkerProcess = hostRole == HostRole.Worker;

    // ── Optional file-based secrets ────────────────────────────────────────
    // Opt-in (docker-compose.secrets.yml): a directory where the FILE NAME is the
    // configuration key and the CONTENT is the value. Root-owned, read-only, and
    // mounted at /secrets. Added AFTER the default providers so it outranks
    // environment variables, and skipped entirely when unset — the single
    // environment file stays the default model.
    var secretsDirectory = builder.Configuration["Secrets:Directory"];
    if (!string.IsNullOrWhiteSpace(secretsDirectory))
    {
        if (Directory.Exists(secretsDirectory))
        {
            builder.Configuration.AddKeyPerFile(secretsDirectory, optional: true, reloadOnChange: false);
        }
        else
        {
            Log.Warning(
                "Secrets:Directory is set to {Directory} but it does not exist; continuing with the environment file only.",
                secretsDirectory);
        }
    }

    // Kestrel is never the public listener (Caddy is), but it still owns the
    // first-line limits. Sizes are configuration-driven so one deployment can be
    // tuned without a rebuild.
    var maxRequestBodyBytes = builder.Configuration.GetValue<long?>("Http:MaxRequestBodySizeBytes")
        ?? 16L * 1024 * 1024;
    var requestTimeoutSeconds = builder.Configuration.GetValue<int?>("Http:RequestTimeoutSeconds") ?? 30;

    builder.WebHost.ConfigureKestrel(options =>
    {
        // No `Server: Kestrel` banner: version disclosure for free reconnaissance.
        options.AddServerHeader = false;

        // Media never arrives here (browsers PUT straight to R2 with a presigned
        // URL). The largest legitimate body is an 8 MB card-template asset, so a
        // 16 MB ceiling leaves multipart headroom and still bounds abuse.
        options.Limits.MaxRequestBodySize = maxRequestBodyBytes;
        options.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
        options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
        options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    });

    // Per-request ceiling that returns a consistent JSON 504 instead of letting a
    // request sit on a slow upstream until the client gives up. Endpoints that
    // stream (SSE) opt out with [DisableRequestTimeout].
    builder.Services.AddRequestTimeouts(options =>
    {
        options.DefaultPolicy = new RequestTimeoutPolicy
        {
            Timeout = TimeSpan.FromSeconds(requestTimeoutSeconds),
            TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
            // Same envelope the exception middleware uses, so a client only ever
            // has to parse one error shape.
            WriteTimeoutResponse = async context =>
            {
                if (context.Response.HasStarted)
                {
                    return;
                }

                context.Response.ContentType = "application/json";
                context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                await context.Response.WriteAsJsonAsync(
                    PunchedApi.Application.DTOs.ApiResponse<object>.Fail(
                        "REQUEST_TIMEOUT",
                        "The request took longer than the server allows. Please retry."));
            }
        };
    });

    // Graceful shutdown: an in-flight request or a worker's current job gets time
    // to finish (and hosted services see the stopping token) before SIGKILL.
    builder.Services.Configure<HostOptions>(options =>
    {
        options.ShutdownTimeout = TimeSpan.FromSeconds(
            builder.Configuration.GetValue<int?>("Http:ShutdownTimeoutSeconds") ?? 30);
    });

    // ── Configuration ──────────────────────────────────────
    // WebApplication.CreateBuilder registers the standard providers in the
    // documented precedence (appsettings.json < appsettings.{Env}.json < user
    // secrets in Development < environment variables < CLI args). User secrets
    // are loaded from the project's UserSecretsId (punched-api-secrets) — the
    // same store `dotnet user-secrets --project` reads — so no manual source
    // re-ordering is required. DATABASE_URL and ConnectionStrings__DefaultConnection
    // environment variables still take precedence over user secrets.

    // ── Serilog ─────────────────────────────────────────────
    builder.Host.UseSerilog((context, config) =>
        config.ReadFrom.Configuration(context.Configuration)
              .Enrich.FromLogContext()
              .WriteTo.Console());

    // ═══════════════════════════════════════════════════════════
    //  SERVICE REGISTRATIONS
    // ═══════════════════════════════════════════════════════════

    // ── Database (PostgreSQL via Neon) ──────────────────────
    // DbContext pooling: contexts are stateless here and resolved per-scope,
    // so pooled instances are safely reset and reused across requests. This
    // removes per-request context allocation cost without changing semantics.
    //
    // Neither interceptor changes behaviour: one measures connection acquisition
    // and failures, the other raises a best-effort wake-up after outbox work
    // commits. Both are optional infrastructure, registered separately so tests
    // that build a bare DbContext keep working.
    var connectionString = ResolveConnectionString(builder.Configuration, hostRole);

    builder.Services.AddSingleton<DatabaseRuntimeMetrics>();
    builder.Services.AddSingleton<DatabaseConnectionInterceptor>();
    builder.Services.AddSingleton<OutboxWakeInterceptor>();

    builder.Services.AddDbContextPool<ApplicationDbContext>((serviceProvider, options) =>
        options.UseNpgsql(
                connectionString,
                o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
            .AddInterceptors(
                serviceProvider.GetRequiredService<DatabaseConnectionInterceptor>(),
                serviceProvider.GetRequiredService<OutboxWakeInterceptor>()));

    // ── Background processing (adaptive polling + wake-up hints) ──
    // Polling cadence, idle backoff, batch size and scale-to-zero friendliness are
    // configuration (BackgroundProcessing:*). PostgreSQL remains the durable queue.
    builder.Services.AddBackgroundProcessing(builder.Configuration);

    // ── Caching / tenant scope resolution ───────────────────
    builder.Services.AddMemoryCache();
    // Cache-aside layer for short-lived, non-authoritative lookups: Redis when
    // Redis:Enabled=true (the compose stack), in-process memory otherwise. The
    // durable queue is PostgreSQL, not Redis (see Infrastructure/Caching).
    builder.Services.AddPunchedCache(builder.Configuration);
    // Bounded, cached dependency probes used by /health/ready (and the docker
    // health checks that gate deployment). The cache windows and timeouts are
    // configuration, because every probe that reaches the database is also activity
    // that keeps a serverless compute awake.
    builder.Services.Configure<HealthOptions>(
        builder.Configuration.GetSection(HealthOptions.SectionName));
    builder.Services.AddSingleton<ReadinessProbe>();
    builder.Services.AddSingleton<IBusinessScopeResolver, BusinessScopeResolver>();

    // ── JWT Settings ────────────────────────────────────────
    var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName);
    builder.Services.Configure<JwtSettings>(jwtSettings);
    builder.Services.Configure<SeedOptions>(
        builder.Configuration.GetSection(SeedOptions.SectionName));

    // Fail fast on a signing secret that was never replaced. appsettings.json ships a
    // documented placeholder so the project boots locally; a deployed environment that
    // still carries it would sign real access tokens with a publicly known key, which
    // is a complete authentication bypass. Refusing to start is the only safe outcome.
    if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
    {
        var configuredSecret = jwtSettings["Secret"];
        var isPlaceholder =
            string.IsNullOrWhiteSpace(configuredSecret)
            || configuredSecret.Length < 32
            || configuredSecret.Contains("CHANGE_THIS", StringComparison.OrdinalIgnoreCase)
            || configuredSecret.Contains("dev-secret-key", StringComparison.OrdinalIgnoreCase);

        if (isPlaceholder)
        {
            throw new InvalidOperationException(
                "JwtSettings:Secret is missing, shorter than 32 characters, or still a development " +
                "placeholder. Set JwtSettings__Secret (or JwtSettings:Secret in user secrets) to a random " +
                "value of at least 32 characters before running outside Development.");
        }
    }

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
        // Tenant policies (BusinessOwner / BusinessStaff / …): membership-based.
        MembershipPolicies.Configure(options);
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

    // Use real SMTP whenever the deployment is configured for it (Email:Enabled +
    // Host + FromAddress), in every environment — so local development exercises
    // the exact path production uses. When email is not configured we fall back to
    // the console sink, which logs the message and still writes the same
    // NotificationLog ledger row, so nothing silently disappears.
    var emailSettings = builder.Configuration
        .GetSection(EmailSettings.SectionName)
        .Get<EmailSettings>() ?? new EmailSettings();
    var emailConfigured = emailSettings.Enabled
        && !string.IsNullOrWhiteSpace(emailSettings.Host)
        && !string.IsNullOrWhiteSpace(emailSettings.FromAddress);
    if (emailConfigured)
        builder.Services.AddScoped<IEmailService, SmtpEmailService>();
    else
        builder.Services.AddScoped<IEmailService, ConsoleEmailService>();

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
    // The concrete type is also registered: CustomerDashboardController injects
    // BusinessService directly (its bootstrap composes several partial-class
    // sections), so resolving only the interface leaves that controller
    // unresolvable and the customer dashboard bootstrap answers 500.
    builder.Services.AddScoped<BusinessService>();
    builder.Services.AddScoped<ILandingPageService, LandingPageService>();

    // Storefront Builder (v1/storefront/builder/* owner + public/{id} read):
    // draft/published configuration, template switching and the public projection.
    builder.Services.AddScoped<IStorefrontBuilderService, StorefrontBuilderService>();

    // Storefront revision history (v1/storefront/builder/revisions*): immutable
    // snapshots created by draft save / publish / rollback / reset, plus the
    // structured comparison and the restore operation.
    builder.Services.AddScoped<IStorefrontRevisionService, StorefrontRevisionService>();

    // Storefront enquiries (POST /v1/businesses/{id}/enquiries): recorded against
    // the business and delivered to its owner through the notification pipeline.
    builder.Services.AddScoped<IStorefrontEnquiryService, StorefrontEnquiryService>();

    // Public storefront reads (GET /v1/businesses/{id}/gallery | opening-hours |
    // search). Anonymous and tenant-narrowed; the module entitlement gate is
    // applied inside each service because an anonymous caller has no token for
    // [RequireModule] to resolve.
    builder.Services.AddScoped<IStorefrontGalleryService, StorefrontGalleryService>();
    builder.Services.AddScoped<IStorefrontHoursService, StorefrontHoursService>();
    builder.Services.AddScoped<IStorefrontSearchService, StorefrontSearchService>();

    // Public storefront booking (POST /v1/businesses/{id}/public-bookings[/{appointmentId}/payment-proof]):
    // the account-free path that does not require proving ownership of anything. It resolves the
    // business-specific customer record from the details the customer typed, books the appointment
    // immediately, and records any pasted M-PESA confirmation as evidence awaiting the business's
    // verification.
    builder.Services.AddScoped<IPublicBookingService, PublicBookingService>();

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
    // DataProtection backs the card-asset delivery tokens and unsubscribe links.
    // Its default key ring lives in the process user profile, which is read-only
    // (and wiped on restart) inside the container: without a persistent, shared
    // location every restart invalidates outstanding emailed links, and the worker
    // could not decrypt what the API protected.
    var dataProtection = builder.Services
        .AddDataProtection()
        .SetApplicationName("PunchedApi");

    var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
    if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
    {
        var keyDirectory = new DirectoryInfo(dataProtectionKeysPath);
        if (!keyDirectory.Exists)
        {
            keyDirectory.Create();
        }

        dataProtection.PersistKeysToFileSystem(keyDirectory);
    }
    builder.Services.AddSingleton<ICardAssetDeliveryTokenService, CardAssetDeliveryTokenService>();
    builder.Services.Configure<CardAssetSettings>(
        builder.Configuration.GetSection(CardAssetSettings.SectionName));

    // Centralized media. Two providers behind one IObjectStore abstraction:
    //   • r2    — Cloudflare R2, the production object store (presigned PUT + CDN).
    //   • local — the filesystem store, so the whole upload → process → deliver
    //             pipeline runs in development and CI without cloud credentials.
    // Only the selected provider is registered, so a misconfigured R2 environment
    // can never be accidentally exercised by a local run (and vice versa).
    builder.Services.Configure<MediaStorageOptions>(
        builder.Configuration.GetSection(MediaStorageOptions.SectionName));
    builder.Services.AddSingleton<IMediaKeyFactory, MediaKeyFactory>();
    builder.Services.AddSingleton<IMediaUrlFactory, MediaUrlFactory>();
    builder.Services.AddSingleton<IMediaValidator, MediaValidator>();
    builder.Services.AddScoped<IMediaProcessor, MediaProcessor>();
    builder.Services.AddSingleton<ILocalMediaUrlSigner, LocalMediaUrlSigner>();

    var mediaProvider = builder.Configuration
        .GetSection(MediaStorageOptions.SectionName)
        .GetValue<string>(nameof(MediaStorageOptions.Provider)) ?? MediaStorageOptions.ProviderR2;

    if (string.Equals(mediaProvider, MediaStorageOptions.ProviderLocal, StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<LocalObjectStore>();
        builder.Services.AddSingleton<IObjectStore>(sp => sp.GetRequiredService<LocalObjectStore>());
        builder.Services.AddSingleton<ILocalMediaStore>(sp => sp.GetRequiredService<LocalObjectStore>());
    }
    else
    {
        builder.Services.AddScoped<IObjectStore, R2ObjectStore>();
    }

    // The filesystem store is a development/CI affordance: objects live on the
    // instance's own disk, which is ephemeral and not shared between replicas, so
    // it must never back a deployed environment. Refusing it here (rather than
    // letting a stray configuration value win) is what makes "no silent local
    // fallback in production" a guarantee instead of a convention.
    if (string.Equals(mediaProvider, MediaStorageOptions.ProviderLocal, StringComparison.OrdinalIgnoreCase)
        && !builder.Environment.IsDevelopment()
        && !builder.Environment.IsEnvironment("Testing"))
    {
        throw new InvalidOperationException(
            $"MediaStorage:Provider is 'local' but the environment is '{builder.Environment.EnvironmentName}'. " +
            "The filesystem store is for local development and CI only. Set MediaStorage__Provider=r2 and inject the R2 credentials for a deployed environment.");
    }

    // Fail fast on a misconfigured object store. R2ObjectStore validates its own
    // options, but it is resolved lazily (and scoped), so without this check a
    // deployment with missing or placeholder R2 settings would boot "healthy" and
    // only fail on the first upload. requireR2: true is correct for both
    // providers — Validate() checks the local provider's own requirements and
    // returns before the R2 rules are reached.
    var mediaStorageOptions = new MediaStorageOptions();
    builder.Configuration.GetSection(MediaStorageOptions.SectionName).Bind(mediaStorageOptions);
    try
    {
        mediaStorageOptions.Validate(requireR2: true);
    }
    catch (InvalidOperationException ex)
    {
        throw new InvalidOperationException($"Invalid MediaStorage configuration: {ex.Message}", ex);
    }

    // WORKERS_ENABLED (Phase 1.5): one flag selects the API-only vs API+worker
    // role. The env var WORKERS_ENABLED=false outranks appsettings.json; the
    // default is enabled so existing deployments keep every worker until
    // configured otherwise.
    var workersEnabled = builder.Configuration.GetValue("WORKERS_ENABLED", true);
    if (workersEnabled)
    {
        builder.Services.AddHostedService<MediaProcessingWorker>();
        builder.Services.AddHostedService<MediaCleanupWorker>();
    }
    builder.Services.AddScoped<IMediaService, MediaService>();
    builder.Services.AddScoped<PunchedApi.Application.Programs.IProgramRuleEngine, PunchedApi.Application.Programs.ProgramRuleEngine>();
    builder.Services.AddScoped<IIdempotencyService, IdempotencyService>();
    builder.Services.AddScoped<INotificationsService, NotificationsService>();

    // ── Notification module (Phase 1: registry, preferences, in-app inbox) ──
    // Producers touch INotificationService.SendAsync only; providers, the outbox
    // worker and each external channel stay behind INotificationChannel, registered
    // one AddScoped line at a time (email = Phase 4, sms = Phase 6, push = Phase 7).
    builder.Services.AddScoped<INotificationService, NotificationService>();
    // One resolver instance backs both interfaces in a scope: the preference view
    // and the "which channels can this deployment deliver on" question. The
    // notification core consults the latter on the SECURITY Force path so an OTP
    // never queues a channel the deployment cannot deliver (which would only
    // dead-letter).
    builder.Services.AddScoped<PreferenceResolver>();
    builder.Services.AddScoped<IPreferenceResolver>(sp => sp.GetRequiredService<PreferenceResolver>());
    builder.Services.AddScoped<INotificationChannelAvailability>(sp => sp.GetRequiredService<PreferenceResolver>());
    // Phase 1.6: suppression / frequency / quiet-hours / budget guard consulted
    // before external dispatch (and for in-app at acceptance).
    builder.Services.AddScoped<INotificationDispatchGuard, NotificationDispatchGuard>();
    builder.Services.Configure<NotificationWorkerOptions>(
        builder.Configuration.GetSection(NotificationWorkerOptions.SectionName));
    builder.Services.Configure<SmsSettings>(builder.Configuration.GetSection(SmsSettings.SectionName));
    builder.Services.Configure<VapidSettings>(builder.Configuration.GetSection(VapidSettings.SectionName));
    // Platform support channels surfaced to the Settings → Support section.
    builder.Services.Configure<SupportSettings>(builder.Configuration.GetSection(SupportSettings.SectionName));
    builder.Services.AddScoped<NotificationOutboxStore>();
    builder.Services.AddScoped<ITemplateRenderer, TemplateRenderer>();
    // Win-back cycles: cycle math is pure; the scanner/cooldown are scoped over
    // the request/work-unit DbContext like the rest of the notification stack.
    builder.Services.AddScoped<LapsedCustomerScanner>();
    builder.Services.AddScoped<WinBackCooldownService>();
    builder.Services.AddScoped<EmailLayoutRenderer>();
    builder.Services.AddScoped<IUnsubscribeTokenService, UnsubscribeTokenService>();
    builder.Services.AddScoped<INotificationSmtpDelivery, MailKitNotificationSmtpDelivery>();
    builder.Services.AddScoped<INotificationOutboxService, NotificationOutboxService>();

    // ── Phase 2: template management (overrides + version history + delivery-path resolution) ──
    builder.Services.AddScoped<ITemplateOverrideResolver, TemplateOverrideResolver>();
    builder.Services.AddScoped<TemplateOverrideService>();
    builder.Services.AddScoped<INotificationChannel, EmailChannel>();
    builder.Services.AddHttpClient<AfricaTalkingClient>();
    builder.Services.AddScoped<INotificationChannel, SmsChannel>();
    // The WebPush client is wrapped behind IPushNotificationSender so the channel
    // stays unit-testable and the HttpClient/ECDSA resources it owns are created
    // and disposed in one place.
    builder.Services.AddScoped<IPushNotificationSender, WebPushNotificationSender>();
    builder.Services.AddScoped<INotificationChannel, PushChannel>();
    builder.Services.AddScoped<IQrService, QrService>();
    builder.Services.AddScoped<ICustomerEnrollmentService, CustomerEnrollmentService>();
    builder.Services.AddScoped<IRedemptionService, RedemptionService>();
    builder.Services.AddScoped<IReferralService, ReferralService>();
    builder.Services.AddScoped<IAdminService, AdminService>();
    builder.Services.AddScoped<AdminNotificationOperationsService>();
    builder.Services.AddScoped<NotificationSuppressionService>();

    // ── Notification management (admin module + business workspace) ──
    // Both services reuse the existing delivery ledger, template resolver and
    // provider registrations; they add no second pipeline.
    builder.Services.AddScoped<AdminNotificationManagementService>();
    builder.Services.AddScoped<BusinessNotificationSettingsService>();
    builder.Services.AddScoped<IAnalyticsAggregationService, AnalyticsAggregationService>();
    builder.Services.AddScoped<IAnalyticsBackfillRunner, AnalyticsBackfillRunner>();
    builder.Services.AddScoped<ISegmentationService, SegmentationService>();
    builder.Services.AddScoped<IInsightService, InsightService>();
    builder.Services.AddScoped<IPayoutService, PayoutService>();
    builder.Services.AddScoped<IRewardPayoutGateway, FakeMpesaPayoutGateway>();

    // ── Booking (Phase 2/3) ─────────────────────────────────
    builder.Services.AddScoped<IAppointmentService, AppointmentService>();
    builder.Services.AddScoped<IBusinessScheduleService, BusinessScheduleService>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddScoped<IReviewService, ReviewService>();
    // Booking reads run as one parallel wave: the fan-out owns the extra short-lived
    // contexts, the loader turns their rows into one booking/availability snapshot.
    builder.Services.AddScoped<BookingReadFanout>();
    // Explicit factory: the loader also has a sequential constructor for unit tests, so
    // container activation would be ambiguous.
    builder.Services.AddScoped<BookingReadContextLoader>(sp => new BookingReadContextLoader(
        sp.GetRequiredService<BookingReadFanout>(),
        sp.GetService<IModuleEntitlementService>()));
    builder.Services.AddScoped<AppointmentAvailabilityService>();
    // Temporary slot claims taken while a booking fee is paid. They block availability exactly
    // like a booking and expire on their own, so an abandoned payment never leaves a phantom slot.
    builder.Services.AddScoped<BookingHoldService>();

    builder.Services.AddScoped<IServiceCatalogService, ServiceCatalogService>();
    // Owner-side customer creation for the booking flow (wizard Step 1).
    builder.Services.AddScoped<ICustomerDirectoryService, CustomerDirectoryService>();

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

    // ── Financial management (ledger, expenses, cashbook, reconciliation, analytics) ──
    // The ledger service is the payment module's only coupling to the books: it recognises a settled
    // payment exactly once. It is registered before PaymentService/PaymentCallbackService resolve it
    // through their optional constructor parameter, so a settlement writes the ledger without the
    // payment code carrying any financial rules of its own.
    builder.Services.AddScoped<IFinancialLedgerService, FinancialLedgerService>();
    builder.Services.AddScoped<IFinancialService, FinancialService>();
    builder.Services.AddScoped<IExpenseService, ExpenseService>();
    builder.Services.AddScoped<IFinancialAnalyticsService, FinancialAnalyticsService>();
    builder.Services.AddScoped<IMpesaReconciliationService, MpesaReconciliationService>();

    // ── Payment → ledger outbox ──────────────────────────────────────────────────────────────
    // Settlement writes a durable obligation in the same transaction as the payment status; the
    // processor turns obligations into income, and the worker retries anything the request could not
    // finish. Registered before PaymentService/PaymentCallbackService so their optional parameters
    // resolve (and so DI no longer falls back to the legacy "best effort" path).
    builder.Services.Configure<PaymentLedgerOptions>(
        builder.Configuration.GetSection(PaymentLedgerOptions.SectionName));
    builder.Services.AddScoped<IPaymentLedgerOutbox, PaymentLedgerOutbox>();
    builder.Services.AddScoped<IPaymentLedgerProcessor, PaymentLedgerProcessor>();
    builder.Services.AddScoped<IPaymentLedgerReconciliationService, PaymentLedgerReconciliationService>();
    builder.Services.AddScoped<IPaymentDecisionGuard, PaymentDecisionGuard>();
    if (workersEnabled)
        builder.Services.AddHostedService<PaymentLedgerWorker>();
    builder.Services.Configure<BillingOptions>(builder.Configuration.GetSection(BillingOptions.SectionName));
    builder.Services.AddScoped<ISubscriptionProvisioningService, SubscriptionProvisioningService>();

    // ── Subscription admin module (tier lifecycle + audit) ──
    builder.Services.AddScoped<ISubscriptionAuditService, SubscriptionAuditService>();
    builder.Services.AddScoped<IAdminTierService, AdminTierService>();
    builder.Services.AddScoped<IAdminSubscriptionService, AdminSubscriptionService>();
    // Business management (list/detail/update/status/members/audit) + subscription
    // lifecycle actions for the platform administration console.
    builder.Services.AddScoped<IAdminBusinessService, AdminBusinessService>();
    // Evicts cached storefront payloads after a mutation that can change a
    // business's effective modules (the storefront bootstrap is output-cached 30 s).
    builder.Services.AddSingleton<IStorefrontCacheInvalidator, StorefrontCacheInvalidator>();

    // ── Module enforcement (plugin architecture Phases 4-6) ──
    // Fail-closed by default — enforcement is unconditional (Step 8).
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<IBusinessContext, BusinessContext>();
    builder.Services.AddScoped<IPermissionService, PermissionService>();
    // Tenant-scoped RBAC authority: resolves effective permissions from
    // business_roles + business_role_permissions + role assignments, and
    // provisions the platform's default role templates per business lazily and
    // idempotently.
    builder.Services.AddScoped<IBusinessPermissionService, BusinessPermissionService>();

    // Business operations completion: service categories, staff positions and
    // RBAC role management. Each resolves the caller's business server-side and
    // re-checks business-scoped permissions inside the service.
    builder.Services.AddScoped<IServiceCategoryService, ServiceCategoryService>();
    builder.Services.AddScoped<IProviderEligibilityService, ProviderEligibilityService>();
    builder.Services.AddScoped<IOperatingModeService, OperatingModeService>();
    builder.Services.AddScoped<IStaffPositionService, StaffPositionService>();

    // Staff management: one member's profile, services, working week and state.
    // Separate from BusinessService so the staff console's per-member surface has a
    // single owner and cannot drift from the staff list it sits beside.
    builder.Services.AddScoped<IStaffManagementService, StaffManagementService>();
    builder.Services.AddScoped<IStaffReportingService, StaffReportingService>();
    builder.Services.AddScoped<IStaffAuditService, StaffAuditService>();
    builder.Services.AddScoped<IStaffPermissionOnboardingService, StaffPermissionOnboardingService>();
    builder.Services.AddScoped<IBusinessRoleService, BusinessRoleService>();
    // Commission configuration resolution (specificity-ranked: staff+service >
    // service > staff > business default) and the ledger service itself.
    builder.Services.AddScoped<ICommissionResolver, CommissionResolver>();
    builder.Services.AddScoped<ICommissionService, CommissionService>();

    // ── Tenant context (business subdomains: java-house.punched.app) ──
    // Host chooses the page; the token chooses the data. None of these
    // services grant access — membership/authorization stay server-side.
    builder.Services.Configure<SessionCookieSettings>(builder.Configuration.GetSection(SessionCookieSettings.SectionName));
    // The shared session cookie carries the refresh token, so it must never travel
    // over plain HTTP in a deployed environment. `RequireSecure` is forced on outside
    // Development/Testing (where TLS is terminated in front of the app and the request
    // reaching Kestrel can look like HTTP), so a missing or stale appsettings value
    // cannot silently downgrade it.
    if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
    {
        builder.Services.PostConfigure<SessionCookieSettings>(settings => settings.RequireSecure = true);
    }
    builder.Services.AddSingleton<ITenantHostResolver, TenantHostResolver>();
    builder.Services.AddSingleton<ITenantUrlBuilder, TenantUrlBuilder>();
    builder.Services.AddSingleton<ISessionCookieService, SessionCookieService>();
    builder.Services.AddScoped<IBusinessMembershipResolver, BusinessMembershipResolver>();
    // The single writer of business_memberships. Shares the request's
    // DbContext so membership rows commit inside the caller's transaction
    // (business provisioning, invitation acceptance, customer enrollment).
    builder.Services.AddScoped<IBusinessMembershipService, BusinessMembershipService>();
    // Tenant authorization: every [Authorize(Policy = MembershipPolicies.*)]
    // resolves an ACTIVE membership from business_memberships, never a persona.
    builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
        BusinessMembershipAuthorizationHandler>();
    // Active tenant for the request: written once by TenantConsistencyMiddleware
    // (slug → business id from the page host), read by tenant-scoped services.
    // Scoped so the context can never leak across requests or users.
    builder.Services.AddScoped<TenantContext>();
    builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

    // ── Seed framework ─────────────────────────────────────
    builder.Services.AddSingleton<ISeedRandom, SeedRandom>();
    builder.Services.AddScoped<IDatabaseSeeder, DatabaseSeeder>();
    builder.Services.AddScoped<IDatabaseCliRunner, DatabaseCliRunner>();
    // Data hygiene for demo/verification bookings: identifies rows by seeder id or
    // verification marker, never by date, customer or status.
    builder.Services.AddScoped<ITestAppointmentCleanupService, TestAppointmentCleanupService>();
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
    // ── Aurelia Luxe Hair Atelier demo tenant (see Infrastructure/Data/Seeding/Demo) ──
    // Adopts the existing atelier business and populates the full salon: profile,
    // entitlements, catalogue, team, loyalty, appointments, payments, attendance,
    // reviews and engagement. Both steps are idempotent and tenant-scoped.
    builder.Services.AddScoped<ISeedStep, AureliaLuxeSetupSeedStep>();
    builder.Services.AddScoped<ISeedStep, AureliaLuxeActivitySeedStep>();
    builder.Services.AddScoped<ISeedStep, ValidationAndReportSeedStep>();

    // SSE broker: singleton so all requests share the same in-process channels
    builder.Services.AddSingleton<ISseService, SseService>();

    // Periodic cleanup of expired tokens, QR tokens, and stale verification codes
    if (workersEnabled)
    {
        builder.Services.AddHostedService<CleanupService>();
        builder.Services.AddHostedService<NotificationDeliveryWorker>();
        builder.Services.AddHostedService<PaymentExpiryWorker>();
        builder.Services.AddHostedService<RescheduleExpiryWorker>();
        builder.Services.AddHostedService<PayoutWorker>();
        builder.Services.AddHostedService<AnalyticsWorker>();
        builder.Services.AddHostedService<SubscriptionExpiryWorker>();
    }
    if (workersEnabled)
    {
        builder.Services.AddHostedService<DomainEventRelayWorker>();
        builder.Services.AddHostedService<ScheduledJobSchedulerWorker>();
    }

    // ── Domain-event relay (Phase 2) ────────────────────────
    builder.Services.AddScoped<IDomainEventPublisher, DomainEventRelayService>();
    builder.Services.AddScoped<IDomainEventHandler, AppointmentCompletedHandler>();
    builder.Services.AddScoped<IDomainEventHandler, AppointmentCancelledHandler>();
    builder.Services.AddScoped<IDomainEventHandler, AppointmentCreatedHandler>();
    builder.Services.AddScoped<IDomainEventHandler, SubscriptionExpiringHandler>();
    builder.Services.AddScoped<AutomationRuleEvaluator>();

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

        // Staff invitations: every invitation sends an email and mints a token, so it is a
        // mail-relay and token-minting surface, not an ordinary write. 20 per hour per
        // (IP + user) is far above a real invite round (a team of ten) and far below what
        // makes the endpoint useful as a spam relay or a token-guessing oracle.
        //
        // The same budget covers the public validate/accept routes, partitioned by IP,
        // because those are anonymous and take a token in the path.
        options.AddPolicy("staff-invitation", httpContext =>
        {
            var userId = httpContext.User?.FindFirst("userId")?.Value ?? "anon";
            var key = $"{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}:{userId}";
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = RateLimit("staff-invitation", 20),
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            });
        });

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
        // Media upload intents: 200 per hour per (IP + user). The ceiling is a
        // burst guard, not a product quota: onboarding legitimately needs ~100
        // intents (a 30-image gallery, three photos per service, logo and cover)
        // plus retries, and the previous 30/hour rejected a real owner mid-setup
        // with a 429 that the uploader can only render as "failed". Abuse stays
        // bounded by the per-purpose byte limits, the gallery/service image caps
        // and the fact that every intent is authenticated.
        options.AddPolicy("media-upload", httpContext =>
        {
            var userId = httpContext.User?.FindFirst("userId")?.Value ?? "anon";
            var key = $"{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}:{userId}";
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = RateLimit("media-upload", 200),
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
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

        // Template override management: 60 per hour per authenticated user (protects
        // the version-history write path from abuse).
        options.AddPolicy("template-overrides", httpContext =>
        {
            var userId = httpContext.User?.FindFirst("userId")?.Value ?? "anon";
            var partitionKey = $"{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}:{userId}";
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RateLimit("template-overrides", 60),
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0
                });
        });

        // Storefront enquiries: 10 per hour per IP. This is the only anonymous
        // write on a public business page, so it is bounded on its own rather than
        // sharing the general bucket — a contact form is exactly the surface a
        // script would otherwise use to flood an owner's inbox.
        options.AddPolicy("storefront-enquiry", httpContext =>
        {
            var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RateLimit("storefront-enquiry", 10),
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0
                });
        });

        // Storefront search: 120 per hour per IP. Search is the heaviest anonymous
        // read on a public page (it scans the business's catalogue and roster), so
        // it gets its own, tighter budget than the general bucket rather than
        // letting a crawler issue it freely.
        options.AddPolicy("storefront-search", httpContext =>
        {
            var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RateLimit("storefront-search", 120),
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0
                });
        });

        // Public storefront booking. Partitioned per IP because the identity is in the
        // request body and this endpoint creates records; the appointment itself proves
        // nothing about who is asking.
        options.AddPolicy("public-booking", httpContext =>
        {
            var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RateLimit("public-booking", 30),
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

        // Public storefront bootstrap (30s, per slug): anonymous + tenant-safe.
        // The slug is IN the path (a different address is a different cache
        // entry), and the tenant header joins the key so a spoofed host can
        // never serve another business's cached payload.
        options.AddPolicy("storefront-bootstrap", builder =>
            builder.Expire(TimeSpan.FromSeconds(30))
                   .SetVaryByHeader(TenantConsistencyMiddleware.TenantHeader)
                   .Tag(StorefrontCacheInvalidator.StorefrontBootstrapTag));
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

    // ── Fail-fast deployment configuration checks ───────────
    // Runs before any request is served (and before migrations): a deployment
    // with an http public URL, a localhost CORS origin, a passwordless Redis
    // connection or a production identity pointed at a developer machine fails at
    // startup instead of surfacing hours later. Returns the resolved environment
    // identity (local | development | staging | production).
    var environmentIdentity = ProductionConfiguration.ValidateForDeployment(
        app.Configuration, app.Environment, app.Logger);

    app.Logger.LogInformation(
        "Process role: {Role} (PUNCHED_ROLE), environment identity: {Identity} (APP_ENV_NAME), " +
        "ASPNETCORE_ENVIRONMENT: {AspNetEnvironment}, workers enabled: {WorkersEnabled}",
        hostRole,
        environmentIdentity,
        app.Environment.EnvironmentName,
        app.Configuration.GetValue("WORKERS_ENABLED", true));

    // ── Background processing: validate and publish the effective plan ──────
    // A bad polling configuration (a floor under Neon's 5-minute window, a zero
    // interval, an out-of-range multiplier) fails startup with every problem listed,
    // rather than producing a silent busy loop that burns the compute allowance.
    app.Services.ValidateBackgroundProcessing();

    if (app.Configuration.GetValue("WORKERS_ENABLED", true))
    {
        var backgroundOptions = app.Services.GetRequiredService<BackgroundProcessingOptions>();
        foreach (var line in backgroundOptions.DescribeLines())
        {
            app.Logger.LogInformation("Background processing | {Line}", line);
        }

        if (backgroundOptions.ScaleToZeroFriendly)
        {
            app.Logger.LogInformation(
                "Scale-to-zero-friendly polling is ON. Idle workers wake the database once per shared tick; " +
                "outbox writes still wake them immediately through the wake-up channel, so worst-case latency " +
                "without a wake signal is one tick. Set BackgroundProcessing__ScaleToZeroFriendlyMode=false " +
                "(paid Neon plan) for latency-first polling.");
        }
    }

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
    // In production, migrations run via an explicit release-step init job
    // (dotnet ef database update) BEFORE the application starts. Boot-time
    // MigrateAsync is disabled in production to prevent concurrent migration
    // execution across multiple instances and to decouple schema changes from
    // application deployment. Development/staging still auto-migrate for
    // developer ergonomics.
    var isProduction = app.Environment.IsProduction();
    if (!isProduction)
    {
        using (var scope = app.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await dbContext.Database.MigrateAsync();
            Log.Information("Database migrations applied successfully (non-production).");
        }
    }
    else
    {
        Log.Information("Skipping boot-time migrations in production. Migrations must be applied via the release-step init job before deployment.");
    }

    // ── Lightweight initialization only (bounded, predictable) ────
    // Normal startup NEVER runs synthetic seeding or historical analytics
    // backfills. Both are explicit CLI operations:
    //   dotnet run -- db seed                                   (synthetic demo data)
    //   dotnet run -- db backfill --from <yyyy-MM-dd> --to ...  (historical analytics)
    // The seeder self-skips unless Seed:RunOnStartup=true, and its analytics
    // step self-skips unless Seed:IncludeAnalyticsBackfill=true.
    using (var scope = app.Services.CreateScope())
    {
        // Slug backfill, seeding, and admin bootstrapping are idempotent and
        // safe to run in every environment (they do not mutate schema).
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

        // Give any business that has no categories yet the default starter set.
        // Idempotent (skips businesses that already have any category) and
        // best-effort — a seed failure must never block startup.
        try
        {
            var operatingModeService = scope.ServiceProvider
                .GetRequiredService<IOperatingModeService>();
            var seededBusinesses = await operatingModeService.BackfillDefaultCategoriesAsync();
            if (seededBusinesses > 0)
                Log.Information(
                    "Seeded default service categories for {Count} business(es).", seededBusinesses);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Default service category backfill skipped.");
        }
    }

    // ── Middleware Pipeline ──────────────────────────────────
    app.UseMiddleware<ExceptionMiddleware>();
    // Browser-hardening headers for every response (set before anything can write).
    app.UseMiddleware<SecurityHeadersMiddleware>();

    // One structured completion line per request: method, path, status code and
    // elapsed time. This is the cheapest useful latency signal in the stack and it
    // needs no collector.
    //
    // Privacy: only the path is templated (never the query string, body or any
    // header). Health probes and the SSE stream are logged at Verbose so they fall
    // below the configured minimum level and are dropped — EventSource carries
    // `?access_token=` in the query string and must never reach a log sink.
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
        options.GetLevel = (httpContext, _, exception) =>
        {
            if (exception is not null || httpContext.Response.StatusCode >= 500)
            {
                return LogEventLevel.Error;
            }

            var path = httpContext.Request.Path;
            return path.StartsWithSegments("/health") || path.StartsWithSegments("/v1/sse")
                ? LogEventLevel.Verbose
                : LogEventLevel.Information;
        };
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            // Correlation id only: the ASP.NET trace identifier is what
            // X-Correlation-ID echoes back, so an operator can pivot from a
            // customer report to every line of one request.
            diagnosticContext.Set("CorrelationId", httpContext.TraceIdentifier);
            diagnosticContext.Set("ClientIp", httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        };
    });

    app.UseMiddleware<ApiEventLoggingMiddleware>();

    // ── Forwarded headers (TLS-terminating proxy) ────────────
    // Caddy terminates TLS and forwards plain HTTP to Kestrel, so
    // `Request.IsHttps` would be false (and the session cookie would lose its
    // Secure flag) and every request would appear to come from the proxy IP
    // (collapsing all per-IP rate limits into one bucket).
    //
    // Trust is explicit and narrow:
    //   • ForwardedHeaders:Enabled=true opts in.
    //   • KnownNetworks/KnownProxies are honoured from configuration and default
    //     to the Docker frontend subnet, i.e. only Caddy can assert these headers.
    //     A directly exposed deployment therefore cannot be spoofed with a forged
    //     X-Forwarded-For.
    //   • ASPNETCORE_FORWARDEDHEADERS_ENABLED=true keeps the previous managed-
    //     platform behaviour (any proxy) for environments without a fixed subnet.
    var forwardedHeadersEnabled =
        builder.Configuration.GetValue("ForwardedHeaders:Enabled", false)
        || string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    if (forwardedHeadersEnabled)
    {
        var forwardedOptions = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
            ForwardLimit = 2
        };

        var knownNetworks = builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>();
        var knownProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>();

        if ((knownNetworks?.Length ?? 0) == 0 && (knownProxies?.Length ?? 0) == 0
            && !string.Equals(
                Environment.GetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "ForwardedHeaders:Enabled is true but no ForwardedHeaders:KnownNetworks/KnownProxies are " +
                "configured. Set ForwardedHeaders__KnownNetworks__0 to the proxy network " +
                "(the compose stack uses the frontend subnet) so forwarded headers cannot be spoofed by " +
                "a directly connected client.");
        }

        forwardedOptions.KnownNetworks.Clear();
        forwardedOptions.KnownProxies.Clear();

        foreach (var network in knownNetworks ?? [])
        {
            // CIDR → prefix address + prefix length. Parsed by hand because the
            // ASP.NET Core known-network collection takes its own IPNetwork type.
            var separator = network.IndexOf('/');
            if (separator <= 0
                || !System.Net.IPAddress.TryParse(network[..separator], out var prefix)
                || !int.TryParse(network[(separator + 1)..], out var prefixLength))
            {
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownNetworks entry '{network}' is not a valid CIDR range.");
            }

            forwardedOptions.KnownNetworks.Add(
                new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLength));
        }

        foreach (var proxy in knownProxies ?? [])
        {
            if (System.Net.IPAddress.TryParse(proxy, out var address))
            {
                forwardedOptions.KnownProxies.Add(address);
            }
            else
            {
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownProxies entry '{proxy}' is not a valid IP address.");
            }
        }

        app.UseForwardedHeaders(forwardedOptions);
    }

    // Bound every request (GatewayTimeout with a JSON body) instead of letting it
    // hang. Streaming endpoints opt out individually via [DisableRequestTimeout].
    app.UseRequestTimeouts();

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
    // Only the API sits behind the TLS-terminating proxy. The worker has no HTTPS
    // port to redirect to (and receives only plain-HTTP health probes from
    // Docker), so the middleware there would just log a warning per probe.
    if (!isWorkerProcess)
    {
        app.UseHttpsRedirection();
    }
    app.UseRateLimiter();
    app.UseAuthentication();
    // Host ↔ token consistency is UX-only: it detects a token minted for a
    // different tenant than the page host and returns 409 TENANT_MISMATCH so
    // the frontend can offer switch/re-auth. It never grants data access —
    // module gating ([RequireModule]) + identity-scoped authorization stay
    // authoritative. Runs after authentication so claims are available.
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseMiddleware<TenantConsistencyMiddleware>();
    // Module gating is enforced per-endpoint via [RequireModule] filters
    // (MODULE_SYSTEM_STATUS_AND_PLAN.md Step 4); no coarse middleware by design.
    app.UseAuthorization();
    app.UseOutputCache();

    // ── Endpoint mapping ────────────────────────────────────
    // The worker container runs hosted services only: mapping controllers there
    // would expose an un-hardened HTTP surface with no traffic and no purpose.
    if (!isWorkerProcess)
    {
        app.MapControllers();

        app.MapGet("/", () => Results.Ok(new { status = "healthy", service = "Punched API", version = "1.0.0" }))
            .AllowAnonymous();
    }

    // /health/live, /health/ready and the diagnostic /health are mapped in both
    // roles: docker gates rollouts on /health/live for the API and the worker
    // alike, and none of them performs an expensive query.
    app.MapPunchedHealthEndpoints();

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
    // A fatal startup failure must be visible to the supervisor: without a
    // non-zero exit code a container platform (or `dotnet ef`) records the run
    // as successful even though the API never served a request.
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

static string ResolveConnectionString(IConfiguration configuration, string role)
{
    var databaseUrl = configuration["DATABASE_URL"]
                             ?? Environment.GetEnvironmentVariable("DATABASE_URL");
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

        // Every sslmode value Neon documents is honoured — previously only
        // `require` was mapped and `verify-full` was silently ignored, which
        // downgraded certificate verification without a word.
        var requestedSslMode = query["sslmode"];
        if (!string.IsNullOrWhiteSpace(requestedSslMode))
        {
            var normalized = requestedSslMode.Replace("-", string.Empty).Replace("_", string.Empty);
            if (!Enum.TryParse<Npgsql.SslMode>(normalized, ignoreCase: true, out var sslMode))
            {
                throw new InvalidOperationException(
                    $"DATABASE_URL has an unrecognised sslmode '{requestedSslMode}'. " +
                    "Use one of: disable, allow, prefer, require, verify-ca, verify-full.");
            }

            builder.SslMode = sslMode;
        }
        else if (!uri.IsLoopback && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            // A remote database without an explicit mode must not be allowed to
            // fall back to an unencrypted connection. Local development keeps
            // Npgsql's permissive default so a stock local PostgreSQL still works.
            builder.SslMode = Npgsql.SslMode.Require;
        }

        if (!string.IsNullOrWhiteSpace(query["channel_binding"]))
        {
            builder["Channel Binding"] = query["channel_binding"];
        }

        // Encryption without verification accepts any certificate that claims to be
        // the database host. Neon publishes a chain that validates cleanly, so a
        // production URL can and should say `sslmode=verify-full`; say so once at
        // startup rather than leaving it as folklore.
        if (builder.SslMode is not (Npgsql.SslMode.VerifyFull or Npgsql.SslMode.VerifyCA))
        {
            Log.Warning(
                "DATABASE_URL uses sslmode={SslMode}: the connection is encrypted but the server certificate is " +
                "not verified against a trusted CA. Use sslmode=verify-full (and channel_binding=require) to close that gap.",
                builder.SslMode);
        }

        // ── Connection lifecycle (pool + keepalive) ─────────────────────────
        // Bound into one validated object so the numbers are judged together and the
        // same values are logged. Two keepalives are deliberately distinguished:
        //
        //   KeepAliveSeconds           Npgsql APPLICATION-level keepalive. Npgsql's
        //                              documented behaviour is to act on an otherwise
        //                              idle connection; its own default is 0 (off).
        //                              Enabled, it is traffic a scale-to-zero database
        //                              never asked for, so the Free profile forbids it
        //                              (see ConnectionLifecycleSettings.Validate).
        //   TcpKeepAlive / *Time       OPERATING-SYSTEM keepalive on the socket, for
        //                              NAT/proxy reliability. Explicit opt-in only; it
        //                              never sends application data and is not a
        //                              substitute for the setting above.
        //
        //   Minimum Pool Size          0 — let the pool drain, so an idle deployment
        //                              holds no socket open (MinimumPoolSize > 0 is
        //                              still allowed, but warns in scale-to-zero mode).
        //   Connection Idle Lifetime   how long an idle pooled socket survives.
        //   Connection Pruning         how often the pool looks for expired sockets.
        //   Connection Lifetime        forced recycle, 0 = never.
        //   Timeout                    connect timeout; a dead link fails fast.
        //
        // None of these influences WHEN Neon suspends: suspension is the dashboard's
        // (or Neon API's) decision. These only decide what this process does between
        // queries.
        var lifecycle = configuration.GetSection(ConnectionLifecycleSettings.SectionName)
                            .Get<ConnectionLifecycleSettings>()
                        ?? new ConnectionLifecycleSettings();

        // The deployment's own scale-to-zero switch decides whether an application
        // keepalive is acceptable. Read from the same section the workers use, so one
        // profile flip moves both.
        var scaleToZeroFriendly = configuration.GetValue(
            $"{BackgroundProcessingOptions.SectionName}:ScaleToZeroFriendlyMode", false);

        var lifecycleProblems = lifecycle.Validate(scaleToZeroFriendly);
        if (lifecycleProblems.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid database connection lifecycle configuration:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, lifecycleProblems.Select(problem => " - " + problem)));
        }

        foreach (var warning in lifecycle.Warnings(scaleToZeroFriendly))
        {
            Log.Warning("Database connection lifecycle: {Warning}", warning);
        }

        builder.MinPoolSize = Math.Clamp(lifecycle.MinimumPoolSize, 0, 100);
        builder.MaxPoolSize = Math.Clamp(lifecycle.MaximumPoolSize, Math.Max(builder.MinPoolSize, 1), 200);
        builder.KeepAlive = Math.Clamp(lifecycle.KeepAliveSeconds, 0, 300);
        builder.TcpKeepAlive = lifecycle.TcpKeepAlive;
        if (lifecycle.TcpKeepAliveTimeSeconds > 0)
        {
            builder.TcpKeepAliveTime = lifecycle.TcpKeepAliveTimeSeconds;
        }

        if (lifecycle.TcpKeepAliveIntervalSeconds > 0)
        {
            builder.TcpKeepAliveInterval = lifecycle.TcpKeepAliveIntervalSeconds;
        }

        builder.ConnectionIdleLifetime = Math.Clamp(lifecycle.ConnectionIdleLifetimeSeconds, 5, 3_600);
        builder.ConnectionPruningInterval = Math.Clamp(lifecycle.ConnectionPruningIntervalSeconds, 1, 300);
        builder.ConnectionLifetime = Math.Clamp(lifecycle.ConnectionLifetimeSeconds, 0, 86_400);
        builder.Timeout = Math.Clamp(lifecycle.TimeoutSeconds, 3, 120);
        builder.CommandTimeout = Math.Clamp(lifecycle.CommandTimeoutSeconds, 5, 300);

        // ── Connection budget ───────────────────────────────────────────────
        // Neon's per-compute limit is max_connections (112 on the 0.25 CU free
        // compute, 7 of them reserved for the superuser → 105 usable). Each
        // container has its OWN Npgsql pool, so the deployment's consumption is
        // the SUM of every role, not one shared number.
        //
        //   pooled endpoint  (-pooler, PgBouncer, transaction mode)
        //       client connections ≤ 10,000; the pooler multiplexes them onto
        //       ~90% of max_connections server sockets. This is what the API and
        //       the worker must use.
        //   direct endpoint
        //       costs one real backend per connection and is shared with the
        //       console, psql, pg_dump and migrations — it is reserved for them.
        //
        // A single role may never claim more than the budget, so a copy/paste of
        // the same env file into both containers cannot silently double it.
        var connectionBudget = Math.Clamp(
            int.TryParse(configuration["Database:ConnectionBudget"], out var configuredBudget) ? configuredBudget : 40,
            1,
            105);

        if (builder.MaxPoolSize > connectionBudget)
        {
            throw new InvalidOperationException(
                $"Database:MaximumPoolSize ({builder.MaxPoolSize}) exceeds Database:ConnectionBudget " +
                $"({connectionBudget}) for role '{configuration[HostRole.ConfigurationKey] ?? HostRole.Api}'. " +
                "Neon's 0.25 CU free compute allows 112 connections in total (105 usable); the API and the worker " +
                "each hold their own pool, so one role must stay well inside that shared limit.");
        }

        // ── Deployment-wide connection plan ─────────────────────────────────
        // The per-role ceiling above stops one container from claiming the whole
        // budget; this check proves the SUM still fits the compute it runs against.
        // Both are configuration, so a paid plan (or a bigger compute) is a values
        // change, not a code change.
        var budgetPlan = configuration.GetSection(DatabaseBudget.SectionName).Get<DatabaseBudget>()
                         ?? new DatabaseBudget();

        var budgetProblems = budgetPlan.Validate(role, builder.MaxPoolSize, connectionBudget);
        if (budgetProblems.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid database connection budget:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, budgetProblems.Select(problem => " - " + problem)));
        }

        var usesPooler = builder.Host.Contains("-pooler", StringComparison.OrdinalIgnoreCase);
        if (!usesPooler && !uri.IsLoopback && !string.Equals(uri.Host, "localhost", StringComparison.Ordinal))
        {
            // Direct connections to a remote Neon compute are expensive and scarce:
            // 105 usable sockets shared with the console, migrations and tooling.
            // Pointing application traffic here is a capacity incident waiting for
            // load, so say it once, loudly.
            Log.Warning(
                "DATABASE_URL points at the DIRECT endpoint ({Host}). Application traffic should use the pooled " +
                "endpoint (…-pooler.…neon.tech), which multiplexes many client connections onto a small server " +
                "pool; reserve the direct endpoint for migrations and administrative work.",
                builder.Host);
        }

        // Logged after every value is final: this line is the single place an
        // operator can see what the deployment is actually using. It names no
        // credentials and includes no connection string.
        Log.Information(
            "Database connection resolved: host={Host}, database={Database}, ssl={SslMode}, endpoint={Endpoint}, " +
            "role budget={Budget}",
            builder.Host,
            builder.Database,
            builder.SslMode,
            usesPooler ? "pooled" : "direct",
            connectionBudget);

        // The effective, non-secret lifecycle settings. `appKeepalive` is the one to
        // watch for scale-to-zero: it must read "off" on the Free profile.
        Log.Information("Database connection lifecycle: {Lifecycle}", lifecycle.Describe());

        Log.Information("Database connection plan: {Plan}", budgetPlan.Describe(role, builder.MaxPoolSize));

        return builder.ConnectionString;
    }

    // ── Fallback: ConnectionStrings:DefaultConnection ────────
    // appsettings.json ships a localhost placeholder. Reaching it means the
    // real database configuration was never supplied, so fail loudly instead
    // of silently talking to a local Postgres that does not exist.
    var fallback = configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(fallback))
    {
        throw new InvalidOperationException(
            "Configure DATABASE_URL (user secrets or environment) or ConnectionStrings__DefaultConnection. " +
            "The database connection string could not be resolved.");
    }

    if (fallback.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
        fallback.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "The resolved database connection string points at localhost, which means DATABASE_URL was not " +
            "configured for this environment. Set DATABASE_URL (via user secrets, an environment variable, or " +
            "ConnectionStrings__DefaultConnection) to the Neon PostgreSQL URL before starting the API.");
    }

    Log.Information("Database connection resolved from ConnectionStrings:DefaultConnection (DATABASE_URL was not set).");
    return fallback;
}
