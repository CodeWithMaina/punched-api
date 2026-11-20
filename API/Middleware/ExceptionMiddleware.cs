using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;

namespace PunchedApi.API.Middleware;

/// <summary>
/// Global exception handling middleware.
/// Catches unhandled exceptions and returns consistent API error responses.
/// Logs errors with structured logging for debugging.
/// </summary>
public class ExceptionMiddleware
{
    /// <summary>
    /// Reused serializer settings: property names stay camelCase (matching the MVC
    /// pipeline's contract) without allocating options per failure.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public ExceptionMiddleware(
        RequestDelegate next,
        ILogger<ExceptionMiddleware> logger,
        IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client hung up (navigation, closed tab, aborted fetch). Nothing is
            // listening for a response, so this is a normal end to the request rather
            // than a server error — logging it as 500 noise hides real failures.
            _logger.LogInformation(
                "Request {Method} {Path} was aborted by the client.",
                context.Request.Method,
                context.Request.Path);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unhandled exception for {Method} {Path} (trace {TraceId}): {Message}",
                context.Request.Method,
                context.Request.Path,
                context.TraceIdentifier,
                ex.Message);

            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        // A response that already started (streamed file, SSE frame, partially written
        // body) cannot be rewritten: touching StatusCode/ContentType would throw
        // "response already started" and hide the original failure. The error is
        // already logged above, so aborting the response is the correct terminal state.
        if (context.Response.HasStarted)
        {
            _logger.LogWarning(
                "Cannot write an error response for {Path}: the response had already started (trace {TraceId}).",
                context.Request.Path,
                context.TraceIdentifier);
            return;
        }

        context.Response.Clear();
        context.Response.ContentType = "application/json";

        var (statusCode, errorCode, message) = exception switch
        {
            ArgumentException argEx => (
                HttpStatusCode.BadRequest,
                "INVALID_REQUEST",
                argEx.Message),

            UnauthorizedAccessException => (
                HttpStatusCode.Unauthorized,
                "UNAUTHORIZED",
                "You are not authorized to perform this action."),

            KeyNotFoundException => (
                HttpStatusCode.NotFound,
                "NOT_FOUND",
                "The requested resource was not found."),

            InvalidOperationException opEx => (
                HttpStatusCode.Conflict,
                "CONFLICT",
                opEx.Message),

            // A unique constraint / foreign key race reaching the database layer is a
            // conflict, not a server fault. The provider message (table, constraint and
            // SQL fragment) is never returned to the caller.
            DbUpdateException => (
                HttpStatusCode.Conflict,
                "CONFLICT",
                "The request could not be completed because it conflicts with the current state of the data."),

            _ => (
                HttpStatusCode.InternalServerError,
                "SERVER_ERROR",
                _env.IsDevelopment()
                    ? exception.Message
                    : "An unexpected error occurred. Please try again later.")
        };

        context.Response.StatusCode = (int)statusCode;
        context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;

        var response = ApiResponse<object>.Fail(errorCode, message);

        // Never write a stack trace, provider message or any other internal detail:
        // ApiResponse.Fail only carries the code and the (already sanitised) message.
        var json = JsonSerializer.Serialize(response, SerializerOptions);

        await context.Response.WriteAsync(json, context.RequestAborted);
    }
}
