using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace MooreHotels.Infrastructure.Services;

public sealed class MonnifyService : IMonnifyService
{
    public const string HttpClientName = "Monnify";

    private const int MaximumProviderResponseBytes = 512 * 1024;
    private static readonly string[] AllowedPaymentMethods =
        ["CARD", "ACCOUNT_TRANSFER", "USSD", "PHONE_NUMBER"];

    private readonly MonnifySettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MonnifyService> _logger;
    private readonly SemaphoreSlim _tokenRefreshLock = new(1, 1);
    private readonly object _tokenStateLock = new();
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiryUtc;

    public MonnifyService(
        IOptions<MonnifySettings> settings,
        IHttpClientFactory httpClientFactory,
        ILogger<MonnifyService> logger)
    {
        _settings = settings.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(
        CancellationToken cancellationToken = default)
    {
        var cached = ReadUsableToken();
        if (cached is not null) return cached;

        await _tokenRefreshLock.WaitAsync(cancellationToken);
        try
        {
            cached = ReadUsableToken();
            if (cached is not null) return cached;

            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login");
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_settings.ApiKey}:{_settings.SecretKey}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            using var response = await SendProviderRequestAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Monnify authentication failed with status {StatusCode}.",
                    response.StatusCode);
                throw ProviderUnavailable();
            }

            using var document = await ReadProviderJsonAsync(response, cancellationToken);
            var body = GetSuccessfulResponseBody(document.RootElement);
            var token = GetRequiredString(body, "accessToken", 4096);
            var expiresIn = GetInt32(body, "expiresIn");
            if (expiresIn is < 30 or > 86400)
            {
                throw ProviderUnavailable();
            }

            lock (_tokenStateLock)
            {
                _cachedToken = token;
                _tokenExpiryUtc = DateTimeOffset.UtcNow.AddSeconds(
                    Math.Max(15, expiresIn - 120));
            }

            return token;
        }
        finally
        {
            _tokenRefreshLock.Release();
        }
    }

    public async Task<MonnifyInitializationResult> InitializeMonnifyPaymentAsync(
        string email,
        string name,
        decimal amount,
        Guid bookingId,
        string bookingCode,
        string paymentReference,
        string callbackUrl,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0 ||
            bookingId == Guid.Empty ||
            string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(bookingCode) ||
            string.IsNullOrWhiteSpace(paymentReference) ||
            paymentReference.Length > 160 ||
            !IsSafeRedirectUrl(callbackUrl))
        {
            throw new InvalidOperationException(
                "The payment request could not be initialized safely.");
        }

        using var response = await SendAuthorizedAsync(
            token =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    "api/v1/merchant/transactions/init-transaction");
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
                request.Content = JsonContent.Create(new
                {
                    amount,
                    customerName = name,
                    customerEmail = email,
                    paymentReference,
                    paymentDescription = $"Booking {bookingCode}",
                    currencyCode = "NGN",
                    contractCode = _settings.ContractCode,
                    redirectUrl = callbackUrl,
                    paymentMethods = AllowedPaymentMethods,
                    metadata = new
                    {
                        bookingId,
                        bookingCode
                    }
                });
                return request;
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Monnify initialization failed with status {StatusCode}.",
                response.StatusCode);
            throw ProviderUnavailable(
                "Online payment could not be initialized. Please try again.");
        }

        using var document = await ReadProviderJsonAsync(response, cancellationToken);
        var body = GetSuccessfulResponseBody(document.RootElement);
        var returnedPaymentReference =
            GetRequiredString(body, "paymentReference", 160);
        var transactionReference =
            GetRequiredString(body, "transactionReference", 160);
        var checkoutUrl = GetRequiredString(body, "checkoutUrl", 2048);
        var returnedApiKey = GetOptionalString(body, "apiKey", 200);

        if (!string.Equals(
                returnedPaymentReference,
                paymentReference,
                StringComparison.Ordinal) ||
            returnedApiKey is not null &&
            !string.Equals(returnedApiKey, _settings.ApiKey, StringComparison.Ordinal) ||
            !IsTrustedCheckoutUrl(checkoutUrl))
        {
            _logger.LogError(
                "Monnify initialization returned values that did not match the server-owned request.");
            throw ProviderUnavailable(
                "The payment provider returned an invalid checkout response.");
        }

        return new MonnifyInitializationResult(
            checkoutUrl,
            returnedPaymentReference,
            transactionReference);
    }

    public async Task<MonnifyVerificationResult?> VerifyTransactionAsync(
        string paymentReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(paymentReference) ||
            paymentReference.Length > 160)
        {
            return null;
        }

        var encodedReference = Uri.EscapeDataString(paymentReference);
        using var response = await SendAuthorizedAsync(
            token =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"api/v2/merchant/transactions/query?paymentReference={encodedReference}");
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
                return request;
            },
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Monnify verification failed with status {StatusCode}.",
                response.StatusCode);
            throw ProviderUnavailable();
        }

        using var document = await ReadProviderJsonAsync(response, cancellationToken);
        var body = GetSuccessfulResponseBody(document.RootElement);
        var returnedPaymentReference =
            GetOptionalString(body, "paymentReference", 160);
        var status = GetOptionalString(body, "paymentStatus", 40);
        if (string.IsNullOrWhiteSpace(returnedPaymentReference) ||
            string.IsNullOrWhiteSpace(status))
        {
            throw ProviderUnavailable();
        }

        var metadata = body.TryGetProperty("metaData", out var metaData)
            ? metaData
            : body.TryGetProperty("metadata", out var metadataValue)
                ? metadataValue
                : default;
        var customer = body.TryGetProperty("customerDTO", out var customerDto)
            ? customerDto
            : body.TryGetProperty("customer", out var customerValue)
                ? customerValue
                : default;

        Guid? bookingId = null;
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            var bookingIdValue = GetOptionalString(metadata, "bookingId", 60);
            if (Guid.TryParse(bookingIdValue, out var parsedBookingId))
            {
                bookingId = parsedBookingId;
            }
        }

        return new MonnifyVerificationResult(
            returnedPaymentReference,
            GetOptionalString(body, "transactionReference", 160),
            status,
            GetDecimal(body, "amountPaid"),
            GetOptionalString(body, "currencyCode", 10)
                ?? GetOptionalString(body, "currency", 10)
                ?? string.Empty,
            metadata.ValueKind == JsonValueKind.Object
                ? GetOptionalString(metadata, "bookingCode", 30)
                : null,
            customer.ValueKind == JsonValueKind.Object
                ? GetOptionalString(customer, "email", 254)
                : null,
            customer.ValueKind == JsonValueKind.Object
                ? GetOptionalString(customer, "name", 160)
                : null,
            GetNullableDecimal(body, "fee"),
            GetNullableDecimal(body, "settlementAmount")
                ?? GetNullableDecimal(body, "settleAmount"),
            GetOptionalString(body, "paymentMethod", 50),
            GetNullableDecimal(body, "totalPayable"),
            bookingId,
            GetUtcDateTime(body, "paidOn"));
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        Func<string, HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        var request = requestFactory(token);
        var response = await SendProviderRequestAsync(request, cancellationToken);
        request.Dispose();

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        InvalidateToken(token);
        token = await GetAccessTokenAsync(cancellationToken);
        request = requestFactory(token);
        response = await SendProviderRequestAsync(request, cancellationToken);
        request.Dispose();
        return response;
    }

    private async Task<HttpResponseMessage> SendProviderRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClientFactory
                .CreateClient(HttpClientName)
                .SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw ProviderUnavailable();
        }
        catch (HttpRequestException exception)
        {
            throw ProviderUnavailable(innerException: exception);
        }
    }

    private static async Task<JsonDocument> ReadProviderJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength >
            MaximumProviderResponseBytes)
        {
            throw ProviderUnavailable();
        }

        try
        {
            await using var stream =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(
                       chunk.AsMemory(),
                       cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaximumProviderResponseBytes)
                {
                    throw ProviderUnavailable();
                }

                await buffer.WriteAsync(
                    chunk.AsMemory(0, read),
                    cancellationToken);
            }

            buffer.Position = 0;
            return await JsonDocument.ParseAsync(
                buffer,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                },
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is JsonException or HttpRequestException)
        {
            throw ProviderUnavailable(innerException: exception);
        }
    }

    private static JsonElement GetSuccessfulResponseBody(JsonElement root)
    {
        if (!root.TryGetProperty("requestSuccessful", out var success) ||
            success.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("responseCode", out var responseCode) ||
            !string.Equals(responseCode.ToString(), "0", StringComparison.Ordinal) ||
            !root.TryGetProperty("responseBody", out var body) ||
            body.ValueKind != JsonValueKind.Object)
        {
            throw ProviderUnavailable();
        }

        return body;
    }

    private static string GetRequiredString(
        JsonElement element,
        string propertyName,
        int maximumLength) =>
        GetOptionalString(element, propertyName, maximumLength)
        ?? throw ProviderUnavailable();

    private static string? GetOptionalString(
        JsonElement element,
        string propertyName,
        int maximumLength)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var result = value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
        if (string.IsNullOrWhiteSpace(result) || result.Length > maximumLength)
        {
            return null;
        }

        return result;
    }

    private static int GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            !value.TryGetInt32(out var result))
        {
            throw ProviderUnavailable();
        }

        return result;
    }

    private static decimal GetDecimal(JsonElement element, string propertyName) =>
        GetNullableDecimal(element, propertyName) ?? 0m;

    private static decimal? GetNullableDecimal(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetDecimal(out var number))
        {
            return number;
        }

        return decimal.TryParse(
                value.ToString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out number)
            ? number
            : null;
    }

    private static DateTime? GetUtcDateTime(
        JsonElement element,
        string propertyName)
    {
        var value = GetOptionalString(element, propertyName, 100);
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return null;
        }

        return parsed.UtcDateTime;
    }

    private string? ReadUsableToken()
    {
        lock (_tokenStateLock)
        {
            return !string.IsNullOrWhiteSpace(_cachedToken) &&
                   DateTimeOffset.UtcNow < _tokenExpiryUtc
                ? _cachedToken
                : null;
        }
    }

    private void InvalidateToken(string token)
    {
        lock (_tokenStateLock)
        {
            if (string.Equals(_cachedToken, token, StringComparison.Ordinal))
            {
                _cachedToken = null;
                _tokenExpiryUtc = default;
            }
        }
    }

    private static bool IsSafeRedirectUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps ||
               uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }

    private static bool IsTrustedCheckoutUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.IsDefaultPort)
        {
            return false;
        }

        return uri.Host.Equals("monnify.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".monnify.com", StringComparison.OrdinalIgnoreCase);
    }

    private static ServiceUnavailableException ProviderUnavailable(
        string message = "The payment provider is temporarily unavailable.",
        Exception? innerException = null) =>
        innerException is null
            ? new ServiceUnavailableException(message)
            : new ServiceUnavailableException(message, innerException);
}
