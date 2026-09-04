using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.Exceptions;
using MooreHotels.WebAPI.Configuration;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text.Json;
using Npgsql;

namespace MooreHotels.WebAPI.Middleware;

public class ExceptionHandlingMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger, IHostEnvironment env)
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
        catch (Exception ex)
        {
            if (context.Response.HasStarted)
            {
                throw;
            }
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        context.Response.ContentType = "application/problem+json";

        var (statusCode, title) = exception switch
        {
            BadRequestException or ArgumentException => (HttpStatusCode.BadRequest, "Bad Request"),
            NotFoundException or KeyNotFoundException => (HttpStatusCode.NotFound, "Resource Not Found"),
            UnauthorizedAccessException => (HttpStatusCode.Forbidden, "Forbidden"),
            ConflictException => (HttpStatusCode.Conflict, "Request Conflict"),
            ServiceUnavailableException => (HttpStatusCode.ServiceUnavailable, "Service Unavailable"),
            DbUpdateConcurrencyException => (HttpStatusCode.Conflict, "Concurrent Update Conflict"),
            DbUpdateException => (HttpStatusCode.Conflict, "Data Conflict"),
            PostgresException => (HttpStatusCode.Conflict, "Data Conflict"),
            _ => (HttpStatusCode.InternalServerError, "Internal Server Error")
        };

        context.Response.StatusCode = (int)statusCode;

        if (statusCode == HttpStatusCode.InternalServerError)
        {
            _logger.LogError(exception, "An unhandled exception occurred in the production pipeline.");
        }

        var problem = new ProblemDetails
        {
            Status = (int)statusCode,
            Title = title,
            Detail = statusCode switch
            {
                HttpStatusCode.InternalServerError when !_env.IsLocal() =>
                    "An unexpected error occurred. Please contact system support.",
                HttpStatusCode.Conflict when exception is DbUpdateException or PostgresException =>
                    "The requested change conflicts with existing data. Refresh and try again.",
                HttpStatusCode.ServiceUnavailable =>
                    "A required external service is temporarily unavailable. Please try again shortly.",
                _ => exception.Message
            },
            Instance = context.Request.Path,
            Extensions = { ["traceId"] = context.TraceIdentifier }
        };

        var json = JsonSerializer.Serialize(problem, JsonOptions);

        await context.Response.WriteAsync(json);
    }
}
