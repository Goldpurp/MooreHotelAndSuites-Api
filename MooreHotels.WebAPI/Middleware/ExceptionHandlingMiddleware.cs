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
            if (ex is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
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
            DbUpdateException updateException when IsDatabaseUnavailable(updateException) =>
                (HttpStatusCode.ServiceUnavailable, "Service Unavailable"),
            DbUpdateException => (HttpStatusCode.Conflict, "Data Conflict"),
            PostgresException postgresException when IsDatabaseUnavailable(postgresException) =>
                (HttpStatusCode.ServiceUnavailable, "Service Unavailable"),
            PostgresException postgresException when
                IsDataConflict(postgresException) || postgresException.SqlState == "P0001" =>
                (HttpStatusCode.Conflict, "Data Conflict"),
            PostgresException => (HttpStatusCode.InternalServerError, "Internal Server Error"),
            NpgsqlException => (HttpStatusCode.ServiceUnavailable, "Service Unavailable"),
            _ => (HttpStatusCode.InternalServerError, "Internal Server Error")
        };

        context.Response.StatusCode = (int)statusCode;

        if ((int)statusCode >= 500)
        {
            if (_env.IsLocal())
            {
                _logger.LogError(exception, "An unhandled request failure occurred.");
            }
            else
            {
                _logger.LogError(
                    "Request failure {ExceptionType} returned {StatusCode}; trace {TraceId}.",
                    exception.GetType().FullName,
                    (int)statusCode,
                    context.TraceIdentifier);
            }
        }

        var problem = new ProblemDetails
        {
            Status = (int)statusCode,
            Title = title,
            Detail = GetSafeDetail(statusCode, exception),
            Instance = context.Request.Path,
            Extensions = { ["traceId"] = context.TraceIdentifier }
        };

        var json = JsonSerializer.Serialize(problem, JsonOptions);

        await context.Response.WriteAsync(json);
    }

    private string GetSafeDetail(HttpStatusCode statusCode, Exception exception)
    {
        if (_env.IsLocal()) return exception.Message;

        return exception switch
        {
            BadRequestException or NotFoundException or UnauthorizedAccessException or ConflictException =>
                exception.Message,
            ArgumentException => "The request is invalid.",
            KeyNotFoundException => "The requested resource was not found.",
            _ when statusCode == HttpStatusCode.Conflict =>
                "The requested change conflicts with existing data. Refresh and try again.",
            _ when statusCode == HttpStatusCode.ServiceUnavailable =>
                "A required service is temporarily unavailable. Please try again shortly.",
            _ => "An unexpected error occurred. Please contact system support."
        };
    }

    private static bool IsDataConflict(Exception exception)
    {
        var postgres = exception as PostgresException ??
                       exception.GetBaseException() as PostgresException;
        return postgres is not null &&
               (postgres.SqlState.StartsWith("23", StringComparison.Ordinal) ||
                postgres.SqlState is PostgresErrorCodes.SerializationFailure or
                    PostgresErrorCodes.DeadlockDetected);
    }

    private static bool IsDatabaseUnavailable(Exception exception)
    {
        var postgres = exception as PostgresException ??
                       exception.GetBaseException() as PostgresException;
        return postgres is not null &&
               (postgres.SqlState.StartsWith("08", StringComparison.Ordinal) ||
                postgres.SqlState.StartsWith("53", StringComparison.Ordinal) ||
                postgres.SqlState.StartsWith("57", StringComparison.Ordinal) ||
                postgres.SqlState.StartsWith("58", StringComparison.Ordinal));
    }
}
