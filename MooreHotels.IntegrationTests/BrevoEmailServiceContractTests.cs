using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Infrastructure.Services;

namespace MooreHotels.IntegrationTests;

public sealed class BrevoEmailServiceContractTests
{
    [Fact]
    public async Task Transient_failure_is_retried_with_one_idempotency_key()
    {
        var handler = new RecordingHandler(
            Response(HttpStatusCode.InternalServerError, """{"message":"temporary"}"""),
            Response(HttpStatusCode.Created, """{"messageId":"<test-message@brevo>"}"""));
        var service = CreateService(handler);

        await service.SendBookingConfirmationAsync(
            "guest@example.test",
            "Guest Tester",
            "MHS00123",
            "Test Suite",
            "Standard",
            2,
            DateTime.UtcNow.AddDays(1),
            DateTime.UtcNow.AddDays(2),
            1,
            50000m);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(
                "https://api.brevo.com/v3/smtp/email",
                request.Uri.ToString());
            Assert.Equal("test-api-key-not-real", request.ApiKey);
            Assert.DoesNotContain("test-api-key-not-real", request.Body);
        });

        using var first = JsonDocument.Parse(handler.Requests[0].Body);
        using var second = JsonDocument.Parse(handler.Requests[1].Body);
        var firstKey = first.RootElement
            .GetProperty("headers")
            .GetProperty("idempotencyKey")
            .GetString();
        var secondKey = second.RootElement
            .GetProperty("headers")
            .GetProperty("idempotencyKey")
            .GetString();
        Assert.True(Guid.TryParse(firstKey, out _));
        Assert.Equal(firstKey, secondKey);
        Assert.Equal(
            "noreply@example.test",
            first.RootElement.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal(
            "guest@example.test",
            first.RootElement.GetProperty("to")[0].GetProperty("email").GetString());
        var html = first.RootElement.GetProperty("htmlContent").GetString();
        Assert.False(string.IsNullOrWhiteSpace(html));
        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("role='article'", html);
        Assert.Contains("aria-roledescription='email'", html);
        Assert.DoesNotContain("Official guest communication", html);
        Assert.Contains("Reservation details", html);
        Assert.Contains("@media only screen and (max-width: 680px)", html);
        Assert.Contains("mobile-content", html);
        Assert.Contains("mobile-stack", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);

        var previewPath = Environment.GetEnvironmentVariable(
            "MOORE_EMAIL_PREVIEW_PATH");
        if (!string.IsNullOrWhiteSpace(previewPath))
        {
            await File.WriteAllTextAsync(previewPath, html);
        }
    }

    [Fact]
    public async Task Permanent_provider_rejection_is_not_retried()
    {
        var handler = new RecordingHandler(
            Response(HttpStatusCode.BadRequest, """{"message":"invalid sender"}"""));
        var service = CreateService(handler);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            service.SendPasswordResetAsync(
                "guest@example.test",
                "Guest Tester",
                "https://example.test/reset"));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Created_response_without_message_id_is_rejected()
    {
        var handler = new RecordingHandler(
            Response(HttpStatusCode.Created, """{"message":"accepted"}"""));
        var service = CreateService(handler);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            service.SendCheckOutThankYouAsync(
                "guest@example.test",
                "Guest Tester",
                "MHS00124",
                "Test Suite"));
    }

    private static EmailService CreateService(RecordingHandler handler)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.brevo.com/")
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PublicAppUrl"] = "https://example.test",
                ["DashboardUrl"] = "https://admin.example.test"
            })
            .Build();
        return new EmailService(
            Options.Create(new EmailSettings
            {
                DeliveryMode = "Brevo",
                ApiPass = "test-api-key-not-real",
                SenderEmail = "noreply@example.test",
                SenderName = "Moore Hotels Test",
                AdminNotificationEmail = "admin@example.test",
                MaxRetryAttempts = 3
            }),
            NullLogger<EmailService>.Instance,
            new FixedHttpClientFactory(client),
            configuration);
    }

    private static HttpResponseMessage Response(
        HttpStatusCode statusCode,
        string body) =>
        new(statusCode)
        {
            Content = new StringContent(
                body,
                Encoding.UTF8,
                "application/json")
        };

    private sealed record RecordedRequest(
        Uri Uri,
        string? ApiKey,
        string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public RecordingHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.RequestUri!,
                request.Headers.TryGetValues("api-key", out var values)
                    ? values.Single()
                    : null,
                body));
            return _responses.Count > 0
                ? _responses.Dequeue()
                : throw new InvalidOperationException(
                    "No fake Brevo response remains.");
        }
    }

    private sealed class FixedHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FixedHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name)
        {
            Assert.Equal(EmailService.HttpClientName, name);
            return _client;
        }
    }
}
