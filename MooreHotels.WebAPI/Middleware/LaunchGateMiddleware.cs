using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MooreHotels.WebAPI.Configuration;

namespace MooreHotels.WebAPI.Middleware;

public sealed class LaunchGateMiddleware
{
    public const string ValidationHeader = "X-Moore-Launch-Validation";

    private readonly RequestDelegate _next;
    private readonly LaunchGateSettings _settings;
    private readonly byte[] _expectedKeyHash;

    public LaunchGateMiddleware(RequestDelegate next, IOptions<LaunchGateSettings> settings)
    {
        _next = next;
        _settings = settings.Value;
        _expectedKeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(_settings.ValidationKey));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_settings.Enabled || IsPublicPreLaunchPath(context) ||
            context.User.Identity?.IsAuthenticated == true || HasValidKey(context))
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.RetryAfter = "300";
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "Service preparing for launch",
            Detail = "This service is not accepting public requests yet.",
            Instance = context.Request.Path,
            Extensions = { ["traceId"] = context.TraceIdentifier }
        });
    }

    private bool HasValidKey(HttpContext context)
    {
        var supplied = context.Request.Headers[ValidationHeader].ToString();
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(_expectedKeyHash, suppliedHash);
    }

    private static bool IsPublicPreLaunchPath(HttpContext context) =>
        HttpMethods.IsOptions(context.Request.Method) ||
        context.Request.Path.Equals("/health/live", StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.Equals("/health/ready", StringComparison.OrdinalIgnoreCase) ||
        (HttpMethods.IsPost(context.Request.Method) &&
         context.Request.Path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase));
}
