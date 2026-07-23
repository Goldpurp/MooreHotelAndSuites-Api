using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MooreHotels.Application.Exceptions;
using MooreHotels.Domain.Common;
using MooreHotels.Infrastructure.Services;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace MooreHotels.IntegrationTests;

public sealed class MonnifyServiceContractTests
{
    [Fact]
    public async Task Initialization_accepts_only_matching_reference_and_trusted_checkout()
    {
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith(
                "/api/v1/auth/login",
                StringComparison.Ordinal)
                ? JsonResponse(AuthResponse("token-one"))
                : JsonResponse(
                    """
                    {
                      "requestSuccessful": true,
                      "responseMessage": "success",
                      "responseCode": "0",
                      "responseBody": {
                        "transactionReference": "MNFY|TEST|000001",
                        "paymentReference": "PAY-001",
                        "apiKey": "MK_TEST_SAFE",
                        "checkoutUrl": "https://sandbox.sdk.monnify.com/checkout/MNFY%7CTEST%7C000001"
                      }
                    }
                    """));
        var service = CreateService(handler);

        var result = await service.InitializeMonnifyPaymentAsync(
            "guest@example.test",
            "Guest Tester",
            100000m,
            Guid.NewGuid(),
            "MHS00001",
            "PAY-001",
            "https://example.test/booking-status?code=MHS00001");

        Assert.Equal("PAY-001", result.PaymentReference);
        Assert.Equal("MNFY|TEST|000001", result.TransactionReference);
        Assert.StartsWith(
            "https://sandbox.sdk.monnify.com/",
            result.CheckoutUrl);
    }

    [Theory]
    [InlineData("PAY-ATTACKER", "https://sandbox.sdk.monnify.com/checkout/valid")]
    [InlineData("PAY-001", "https://monnify.com.attacker.example/checkout")]
    [InlineData("PAY-001", "http://sdk.monnify.com/checkout")]
    public async Task Initialization_rejects_provider_response_substitution(
        string returnedReference,
        string checkoutUrl)
    {
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith(
                "/api/v1/auth/login",
                StringComparison.Ordinal)
                ? JsonResponse(AuthResponse("token-one"))
                : JsonResponse(
                    $$"""
                    {
                      "requestSuccessful": true,
                      "responseMessage": "success",
                      "responseCode": "0",
                      "responseBody": {
                        "transactionReference": "MNFY|TEST|000001",
                        "paymentReference": "{{returnedReference}}",
                        "apiKey": "MK_TEST_SAFE",
                        "checkoutUrl": "{{checkoutUrl}}"
                      }
                    }
                    """));
        var service = CreateService(handler);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            service.InitializeMonnifyPaymentAsync(
                "guest@example.test",
                "Guest Tester",
                100000m,
                Guid.NewGuid(),
                "MHS00001",
                "PAY-001",
                "https://example.test/booking-status?code=MHS00001"));
    }

    [Fact]
    public async Task Verification_parses_authoritative_binding_fields()
    {
        var bookingId = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith(
                "/api/v1/auth/login",
                StringComparison.Ordinal)
                ? JsonResponse(AuthResponse("token-one"))
                : JsonResponse(
                    $$"""
                    {
                      "requestSuccessful": true,
                      "responseMessage": "success",
                      "responseCode": "0",
                      "responseBody": {
                        "transactionReference": "MNFY|TEST|000001",
                        "paymentReference": "PAY-001",
                        "amountPaid": 100000,
                        "totalPayable": 100000,
                        "settlementAmount": 99800,
                        "paidOn": "2026-07-23T06:30:00.000+0000",
                        "paymentStatus": "PAID",
                        "paymentMethod": "ACCOUNT_TRANSFER",
                        "currencyCode": "NGN",
                        "fee": 200,
                        "customerDTO": {
                          "email": "guest@example.test",
                          "name": "Guest Tester"
                        },
                        "metaData": {
                          "bookingId": "{{bookingId}}",
                          "bookingCode": "MHS00001"
                        }
                      }
                    }
                    """));
        var service = CreateService(handler);

        var result = await service.VerifyTransactionAsync("PAY-001");

        Assert.NotNull(result);
        Assert.Equal("MNFY|TEST|000001", result.TransactionReference);
        Assert.Equal("MHS00001", result.BookingCode);
        Assert.Equal(bookingId, result.BookingId);
        Assert.Equal("guest@example.test", result.CustomerEmail);
        Assert.Equal(100000m, result.AmountPaid);
        Assert.Equal("NGN", result.CurrencyCode);
        Assert.Equal(
            DateTimeKind.Utc,
            result.PaidAtUtc!.Value.Kind);
    }

    [Fact]
    public async Task Access_token_is_cached_across_concurrent_calls()
    {
        var authenticationCalls = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref authenticationCalls);
            return JsonResponse(AuthResponse("shared-token"));
        });
        var service = CreateService(handler);

        var tokens = await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(_ => service.GetAccessTokenAsync()));

        Assert.All(tokens, token => Assert.Equal("shared-token", token));
        Assert.Equal(1, authenticationCalls);
    }

    [Fact]
    public async Task Unauthorized_token_is_refreshed_once_without_exposing_credentials()
    {
        var authenticationCalls = 0;
        var verificationCalls = 0;
        var seenAuthorization = new ConcurrentBag<AuthenticationHeaderValue?>();
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/api/v1/auth/login",
                    StringComparison.Ordinal))
            {
                var call = Interlocked.Increment(ref authenticationCalls);
                return JsonResponse(AuthResponse($"token-{call}"));
            }

            seenAuthorization.Add(request.Headers.Authorization);
            if (Interlocked.Increment(ref verificationCalls) == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return JsonResponse(
                """
                {
                  "requestSuccessful": true,
                  "responseMessage": "success",
                  "responseCode": "0",
                  "responseBody": {
                    "transactionReference": "MNFY|TEST|000001",
                    "paymentReference": "PAY-001",
                    "amountPaid": 100000,
                    "paymentStatus": "PAID",
                    "paymentMethod": "CARD",
                    "currencyCode": "NGN",
                    "customerDTO": {
                      "email": "guest@example.test",
                      "name": "Guest Tester"
                    },
                    "metaData": {
                      "bookingCode": "MHS00001"
                    }
                  }
                }
                """);
        });
        var service = CreateService(handler);

        var result = await service.VerifyTransactionAsync("PAY-001");

        Assert.NotNull(result);
        Assert.Equal(2, authenticationCalls);
        Assert.Equal(2, verificationCalls);
        Assert.DoesNotContain(
            seenAuthorization,
            value => value?.Scheme == "Basic");
    }

    private static MonnifyService CreateService(
        HttpMessageHandler handler)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.monnify.com/")
        };
        return new MonnifyService(
            Options.Create(new MonnifySettings
            {
                ApiKey = "MK_TEST_SAFE",
                SecretKey = "SK_TEST_DO_NOT_LOG",
                ContractCode = "1234567890",
                BaseUrl = "https://api.monnify.com"
            }),
            new SingleHttpClientFactory(client),
            NullLogger<MonnifyService>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };

    private static string AuthResponse(string token) =>
        $$"""
        {
          "requestSuccessful": true,
          "responseMessage": "success",
          "responseCode": "0",
          "responseBody": {
            "accessToken": "{{token}}",
            "expiresIn": 3600
          }
        }
        """;

    private sealed class SingleHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
