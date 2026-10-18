using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Serilog.Context;

namespace PunchedApi.API.Middleware;

/// <summary>
/// Correlation-ID middleware: extracts <c>X-Correlation-ID</c> from the request
/// header (or generates a new GUID), pushes it into Serilog's log context so every
/// log line during the request carries the same correlation ID, and echoes it back
/// on the <c>X-Correlation-ID</c> response header for client-side tracing.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    private const string HeaderName = "X-Correlation-ID";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault()
            ?? Guid.NewGuid().ToString("D");

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            context.Response.OnStarting(() =>
            {
                if (!context.Response.Headers.ContainsKey(HeaderName))
                    context.Response.Headers[HeaderName] = correlationId;
                return Task.CompletedTask;
            });

            await _next(context);
        }
    }
}