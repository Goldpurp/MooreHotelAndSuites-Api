using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MooreHotels.WebAPI.Configuration;
using MooreHotels.WebAPI.Middleware;

namespace MooreHotels.IntegrationTests;

public sealed class LaunchGateMiddlewareTests
{
    private const string ValidationKey = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Enabled_gate_blocks_anonymous_public_requests()
    {
        var called = false;
        var middleware = CreateMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        var context = Context("GET", "/api/rooms");

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("300", context.Response.Headers.RetryAfter);
    }

    [Fact]
    public async Task Rejected_bearer_token_returns_unauthorized_instead_of_launch_unavailable()
    {
        var called = false;
        var middleware = CreateMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        var context = Context("POST", "/api/bookings");
        context.Request.Headers.Authorization = "Bearer expired-or-invalid-token";

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal((int)HttpStatusCode.Unauthorized, context.Response.StatusCode);
        Assert.Equal("Bearer", context.Response.Headers.WWWAuthenticate);
        context.Response.Body.Position = 0;
        var responseBody = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("SESSION_EXPIRED", responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_gate_allows_the_exact_validation_key()
    {
        var called = false;
        var middleware = CreateMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        var context = Context("POST", "/api/bookings");
        context.Request.Headers[LaunchGateMiddleware.ValidationHeader] = ValidationKey;

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }

    [Fact]
    public async Task Enabled_gate_allows_authenticated_staff_and_staff_login()
    {
        foreach (var context in new[]
                 {
                     Context("GET", "/api/health", authenticated: true),
                     Context("POST", "/api/auth/login")
                 })
        {
            var called = false;
            var middleware = CreateMiddleware(_ =>
            {
                called = true;
                return Task.CompletedTask;
            });

            await middleware.InvokeAsync(context);

            Assert.True(called);
        }
    }

    [Theory]
    [InlineData("GET", "/health/live")]
    [InlineData("GET", "/health/ready")]
    [InlineData("GET", "/health/operations")]
    [InlineData("HEAD", "/health/live")]
    [InlineData("HEAD", "/health/ready")]
    [InlineData("HEAD", "/health/operations")]
    [InlineData("OPTIONS", "/api/bookings")]
    public async Task Enabled_gate_allows_safe_public_operational_requests(string method, string path)
    {
        var called = false;
        var middleware = CreateMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(Context(method, path));

        Assert.True(called);
    }

    [Fact]
    public async Task Disabled_gate_does_not_change_requests()
    {
        var called = false;
        var middleware = new LaunchGateMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            Options.Create(new LaunchGateSettings()));

        await middleware.InvokeAsync(Context("POST", "/api/bookings"));

        Assert.True(called);
    }

    private static LaunchGateMiddleware CreateMiddleware(RequestDelegate next) =>
        new(next, Options.Create(new LaunchGateSettings
        {
            Enabled = true,
            ValidationKey = ValidationKey
        }));

    private static DefaultHttpContext Context(string method, string path, bool authenticated = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (authenticated)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
                "test"));
        }
        return context;
    }
}
