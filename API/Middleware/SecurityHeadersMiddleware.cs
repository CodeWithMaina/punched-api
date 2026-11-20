namespace PunchedApi.API.Middleware;

/// <summary>
/// Adds the browser-hardening response headers every API response should carry.
///
/// <para>The API serves JSON, a 1×1 pixel and stored media — never HTML — so the
/// directives are deliberately narrow: <c>nosniff</c> stops a browser from
/// re-interpreting a JSON or image response as a script, <c>X-Frame-Options:
/// DENY</c> stops the API (and any media it serves) from being framed, and
/// <c>no-referrer</c> keeps tenant slugs and signed media URLs out of the
/// <c>Referer</c> header of third-party requests. Headers already set by an
/// endpoint (the card-asset and media delivery controllers set their own) are
/// never overwritten.</para>
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        // Set on starting so the values are present for every response — including
        // ones written by exception handling and by static/media endpoints.
        context.Response.OnStarting(() =>
        {
            if (!headers.ContainsKey("X-Content-Type-Options"))
                headers["X-Content-Type-Options"] = "nosniff";

            if (!headers.ContainsKey("X-Frame-Options"))
                headers["X-Frame-Options"] = "DENY";

            if (!headers.ContainsKey("Referrer-Policy"))
                headers["Referrer-Policy"] = "no-referrer";

            // The API returns data, not documents: no resource can legitimately
            // execute script or load nested content, so everything but the media
            // types this API emits is denied.
            if (!headers.ContainsKey("Content-Security-Policy"))
                headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";

            return Task.CompletedTask;
        });

        return _next(context);
    }
}
