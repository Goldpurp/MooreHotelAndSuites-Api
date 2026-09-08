using MooreHotels.WebAPI.Configuration;

namespace MooreHotels.WebAPI.Middleware;

public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHostEnvironment _environment;

    public SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment)
    {
        _next = next;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
        headers["X-Permitted-Cross-Domain-Policies"] = "none";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-site";
        headers["Content-Security-Policy"] = _environment.IsDeployed()
            ? "default-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'"
            : "default-src 'none'; base-uri 'none'; frame-ancestors 'none'; " +
              "form-action 'self'; img-src 'self' data:; font-src 'self'; " +
              "connect-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'";

        if (_environment.IsDeployed())
        {
            // The public HTTPS edge may use an HTTP hop to Kestrel. Browsers
            // must still receive the API's transport-security policy.
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        }

        if (context.Request.Path.StartsWithSegments("/api") ||
            context.Request.Path.StartsWithSegments("/hubs"))
        {
            headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            headers["Pragma"] = "no-cache";
        }

        await _next(context);
    }
}
