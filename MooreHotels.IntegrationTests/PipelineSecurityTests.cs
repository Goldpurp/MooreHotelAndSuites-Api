using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using MooreHotels.WebAPI.Extensions;
using MooreHotels.WebAPI.Middleware;

namespace MooreHotels.IntegrationTests;

[Collection(ManualTransferCollection.Name)]
public sealed class AuthenticationPipelineSecurityTests
{
    private readonly ManualTransferTestFixture _fixture;

    public AuthenticationPipelineSecurityTests(ManualTransferTestFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Signed_token_with_invalid_user_identifier_fails_closed()
    {
        using var request = AuthorizedRequest(CreateToken(
            SecurityAlgorithms.HmacSha256,
            "not-a-user-id"));
        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("SESSION_REVOKED", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Alternate_hmac_algorithm_is_rejected()
    {
        using var request = AuthorizedRequest(CreateToken(
            SecurityAlgorithms.HmacSha512,
            _fixture.Admin.Id.ToString()));
        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Jwt_rate_and_transport_limits_are_explicitly_configured()
    {
        var jwt = _fixture.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        Assert.Equal(
            [SecurityAlgorithms.HmacSha256],
            jwt.TokenValidationParameters.ValidAlgorithms);

        var kestrel = _fixture.Services
            .GetRequiredService<IOptions<KestrelServerOptions>>()
            .Value;
        Assert.Equal(
            ServiceCollectionExtensions.DefaultRequestBodySize,
            kestrel.Limits.MaxRequestBodySize);

        var rateLimits = _fixture.Services
            .GetRequiredService<IOptions<RateLimiterOptions>>()
            .Value;
        Assert.NotNull(rateLimits.GlobalLimiter);

        var hsts = _fixture.Services.GetRequiredService<IOptions<HstsOptions>>().Value;
        Assert.Equal(TimeSpan.FromDays(365), hsts.MaxAge);
        Assert.True(hsts.IncludeSubDomains);

        var signalR = _fixture.Services.GetRequiredService<IOptions<HubOptions>>().Value;
        Assert.Equal(32 * 1024, signalR.MaximumReceiveMessageSize);
        Assert.Equal(1, signalR.MaximumParallelInvocationsPerClient);
    }

    private static HttpRequestMessage AuthorizedRequest(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Moore-App-Environment", "local");
        return request;
    }

    private static string CreateToken(string algorithm, string userId)
    {
        var keyValue = Environment.GetEnvironmentVariable("Jwt__Key")
            ?? throw new InvalidOperationException("The integration JWT key is unavailable.");
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keyValue)),
            algorithm);
        var token = new JwtSecurityToken(
            issuer: Environment.GetEnvironmentVariable("Jwt__Issuer"),
            audience: Environment.GetEnvironmentVariable("Jwt__Audience"),
            claims:
            [
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Role, "Admin"),
                new Claim("security_stamp", "pipeline-security-test")
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed class MiddlewareResponseSecurityTests
{
    [Fact]
    public async Task Production_argument_errors_do_not_echo_internal_messages()
    {
        const string sensitiveMessage = "internal-token-value-must-not-leak";
        var environment = ProductionEnvironment();
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new ArgumentException(sensitiveMessage),
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            environment);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.DoesNotContain(sensitiveMessage, body, StringComparison.Ordinal);
        Assert.Contains("The request is invalid.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Environment_mismatch_does_not_reflect_untrusted_header_content()
    {
        const string untrustedValue = "secret-shaped-attacker-content";
        var middleware = new EnvironmentBoundaryMiddleware(
            _ => Task.CompletedTask,
            ProductionEnvironment());
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Headers[EnvironmentBoundaryMiddleware.RequestHeader] = untrustedValue;

        await middleware.InvokeAsync(context);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.DoesNotContain(untrustedValue, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_and_hub_responses_receive_isolation_and_no_store_headers()
    {
        var middleware = new SecurityHeadersMiddleware(
            _ => Task.CompletedTask,
            ProductionEnvironment());
        foreach (var path in new[] { "/api/profile/me", "/hubs/notifications" })
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            await middleware.InvokeAsync(context);

            Assert.Equal("no-store, no-cache, must-revalidate, max-age=0", context.Response.Headers.CacheControl);
            Assert.Equal("same-origin", context.Response.Headers["Cross-Origin-Opener-Policy"]);
            Assert.Equal("same-site", context.Response.Headers["Cross-Origin-Resource-Policy"]);
        }
    }

    private static IHostEnvironment ProductionEnvironment() => new TestHostEnvironment
    {
        EnvironmentName = Environments.Production,
        ApplicationName = "MooreHotels.IntegrationTests",
        ContentRootPath = Directory.GetCurrentDirectory(),
        ContentRootFileProvider = new NullFileProvider()
    };

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
