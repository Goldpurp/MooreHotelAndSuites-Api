using Microsoft.AspNetCore.Mvc;
using MooreHotels.WebAPI.Configuration;

namespace MooreHotels.WebAPI.Middleware;

public sealed class EnvironmentBoundaryMiddleware
{
    public const string RequestHeader = "X-Moore-App-Environment";
    public const string ResponseHeader = "X-Moore-API-Environment";

    private readonly RequestDelegate _next;
    private readonly string _environment;

    public EnvironmentBoundaryMiddleware(RequestDelegate next, IHostEnvironment environment)
    {
        _next = next;
        _environment = environment.ToClientName();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers[ResponseHeader] = _environment;

        var requestedEnvironment = context.Request.Headers[RequestHeader].ToString()
            .Trim()
            .ToLowerInvariant();

        if (requestedEnvironment.Length > 0 && requestedEnvironment != _environment)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Environment mismatch",
                Detail = $"The {_environment} API cannot accept a request marked for {requestedEnvironment}.",
                Instance = context.Request.Path,
                Extensions = { ["traceId"] = context.TraceIdentifier }
            });
            return;
        }

        await _next(context);
    }
}
