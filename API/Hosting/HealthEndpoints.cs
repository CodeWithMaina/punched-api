using Microsoft.Extensions.Options;
using PunchedApi.Application.Settings;

namespace PunchedApi.API.Hosting;

/// <summary>
/// Health endpoints, mapped identically in both process roles.
///
/// <para><b>Exposure.</b> Caddy answers <c>/api/health*</c> with 404, so these are
/// reachable only from the Docker network (compose probes) — the payloads
/// describe internal dependencies and configuration state and must never be
/// published. They are anonymous because a probe carries no token.</para>
///
/// <para><b>Split.</b></para>
/// <list type="bullet">
///   <item><c>/health/live</c> — process is up. No dependency calls, so a slow
///   database can never make Docker restart a healthy container.</item>
///   <item><c>/health/ready</c> — essential dependencies answered (PostgreSQL,
///   and Redis when enabled). Returns 503 when not ready so a load balancer or
///   orchestrator stops sending traffic instead of failing requests.</item>
///   <item><c>/health</c> — operator diagnostics (channel configuration, worker
///   role, optional queue depth). Kept for backwards compatibility with existing
///   monitoring; it is the only one that reports configuration detail.</item>
/// </list>
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapPunchedHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }))
            .AllowAnonymous();

        app.MapGet("/health/ready", async (ReadinessProbe probe, CancellationToken cancellationToken) =>
        {
            var result = await probe.CheckAsync(cancellationToken);

            var payload = new
            {
                status = result.Ready ? "ready" : "not_ready",
                checks = result.Checks.Select(check => new
                {
                    name = check.Name,
                    healthy = check.Healthy,
                    detail = check.Detail
                })
            };

            return result.Ready
                ? Results.Ok(payload)
                : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();

        app.MapGet("/health", async (
            ReadinessProbe probe,
            IOptions<EmailSettings> emailOptions,
            IOptions<VapidSettings> vapidOptions,
            IOptions<SmsSettings> smsOptions,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var readiness = await probe.CheckAsync(cancellationToken);

            // The pending-outbox count is a database aggregate; only run it when a
            // human explicitly asks (`/health?detail=1`), so routine probing stays
            // free of it.
            var wantsDetail = httpContext.Request.Query.TryGetValue("detail", out var detailValue)
                && (detailValue == "1" || string.Equals(detailValue, "true", StringComparison.OrdinalIgnoreCase));

            long? queueDepth = wantsDetail
                ? await probe.GetNotificationQueueDepthAsync(cancellationToken)
                : null;

            var email = emailOptions.Value;
            var vapid = vapidOptions.Value;
            var sms = smsOptions.Value;

            var payload = new
            {
                status = readiness.Ready ? "healthy" : "unhealthy",
                database = readiness.Checks.FirstOrDefault(c => c.Name == "database")?.Detail ?? "unknown",
                redis = readiness.Checks.FirstOrDefault(c => c.Name == "redis")?.Detail ?? "disabled",
                // Which role this process plays; a worker container reports
                // "disabled" here because WORKERS_ENABLED=false is only set on the
                // API container.
                workers = IsWorkersEnabled(httpContext.RequestServices) ? "enabled" : "disabled",
                queue_depth = queueDepth ?? -1,
                channels = new
                {
                    in_app = "ready",
                    email = email.Enabled
                            && !string.IsNullOrWhiteSpace(email.Host)
                            && !string.IsNullOrWhiteSpace(email.FromAddress)
                        ? "ready" : "unconfigured",
                    push = vapid.Enabled
                           && !string.IsNullOrWhiteSpace(vapid.PublicKey)
                           && !string.IsNullOrWhiteSpace(vapid.PrivateKey)
                        ? "ready" : "unconfigured",
                    sms = sms.Enabled && !string.IsNullOrWhiteSpace(sms.ApiKey)
                        ? "ready" : "unconfigured"
                }
            };

            // A failing dependency is reported as 503 as well: a monitor that only
            // looks at the status code must not read an unhealthy process as green.
            return readiness.Ready
                ? Results.Ok(payload)
                : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();

        return app;
    }

    private static bool IsWorkersEnabled(IServiceProvider services)
    {
        var configuration = services.GetService<IConfiguration>();
        return configuration?.GetValue("WORKERS_ENABLED", true) ?? true;
    }
}
