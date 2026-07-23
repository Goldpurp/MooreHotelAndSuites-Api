using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Enums;
using MooreHotels.WebAPI.Extensions;
using MooreHotels.WebAPI.Services;
using System.Text;
using System.Text.Json;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/payments")]
public sealed class MonnifyWebhookController : ControllerBase
{
    private const int MaximumWebhookBytes = 256 * 1024;

    private readonly IMonnifyService _monnifyService;
    private readonly IMonnifyPaymentProcessor _paymentProcessor;
    private readonly IBookingService _bookingService;
    private readonly IBookingRepository _bookingRepository;
    private readonly MonnifySettings _settings;
    private readonly ILogger<MonnifyWebhookController> _logger;

    public MonnifyWebhookController(
        IMonnifyService monnifyService,
        IMonnifyPaymentProcessor paymentProcessor,
        IBookingService bookingService,
        IBookingRepository bookingRepository,
        IOptions<MonnifySettings> settings,
        ILogger<MonnifyWebhookController> logger)
    {
        _monnifyService = monnifyService;
        _paymentProcessor = paymentProcessor;
        _bookingService = bookingService;
        _bookingRepository = bookingRepository;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Receives Monnify transaction-completion notifications.
    /// </summary>
    /// <remarks>
    /// The raw request is authenticated with Monnify's HMAC-SHA512 signature.
    /// Payment state is changed only after an independent server-to-server
    /// verification and an atomic, idempotent database transaction.
    /// </remarks>
    [HttpPost("monnify-webhook")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.WebhookRateLimitPolicy)]
    [RequestSizeLimit(MaximumWebhookBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> HandleWebhook(
        CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            return NotFound();
        }

        if (!MonnifyWebhookSecurity.IsAllowedSource(
                HttpContext.Connection.RemoteIpAddress,
                _settings))
        {
            _logger.LogWarning(
                "Rejected a Monnify webhook from a non-allowlisted source.");
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var requestBody = await ReadRequestBodyAsync(cancellationToken);
        if (requestBody is null)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        StringValues signatureValues = Request.Headers["monnify-signature"];
        if (signatureValues.Count != 1 ||
            !MonnifyWebhookSecurity.VerifySignature(
                requestBody,
                signatureValues[0],
                _settings.SecretKey))
        {
            _logger.LogWarning(
                "Rejected a Monnify webhook with an invalid signature.");
            return Unauthorized();
        }

        try
        {
            using var document = JsonDocument.Parse(
                requestBody,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            var root = document.RootElement;
            var eventType = root.TryGetProperty(
                    "eventType",
                    out var eventTypeValue)
                ? eventTypeValue.GetString()
                : null;
            if (!string.Equals(
                    eventType,
                    "SUCCESSFUL_TRANSACTION",
                    StringComparison.Ordinal))
            {
                // Other signed Monnify event types are outside this collection
                // endpoint and are acknowledged without changing booking state.
                return Ok(new { received = true });
            }

            if (!root.TryGetProperty("eventData", out var eventData) ||
                eventData.ValueKind != JsonValueKind.Object)
            {
                return BadRequest();
            }

            var paymentReference = eventData.TryGetProperty(
                    "paymentReference",
                    out var referenceValue) &&
                referenceValue.ValueKind == JsonValueKind.String
                    ? referenceValue.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(paymentReference) ||
                paymentReference.Length > 160)
            {
                return BadRequest();
            }

            var booking = await _bookingRepository.GetByPaymentReferenceAsync(
                paymentReference);
            if (booking is null ||
                booking.PaymentMethod != PaymentMethod.Monnify ||
                !string.Equals(
                    booking.TransactionReference,
                    paymentReference,
                    StringComparison.Ordinal))
            {
                // The request is authentically from Monnify but is not a
                // transaction owned by this application. Acknowledge it so the
                // provider does not retry forever; do not disclose any lookup.
                _logger.LogWarning(
                    "Acknowledged a signed Monnify webhook with no matching booking.");
                return Ok(new { received = true });
            }

            var verification = await _monnifyService.VerifyTransactionAsync(
                booking.TransactionReference!,
                cancellationToken);
            if (verification is null)
            {
                return StatusCode(
                    StatusCodes.Status409Conflict,
                    new
                    {
                        message =
                            "The transaction is not yet verifiable with Monnify."
                    });
            }

            var outcome = await _paymentProcessor.ApplyVerifiedPaymentAsync(
                booking.Id,
                verification,
                "Webhook",
                actingUserId: null,
                HttpContext.TraceIdentifier,
                cancellationToken);

            if (outcome.Kind == MonnifyPaymentOutcomeKind.Confirmed)
            {
                await _bookingService.SendPaymentConfirmationAsync(
                    outcome.BookingCode,
                    outcome.PaymentReference);
            }
            else if (outcome.Kind ==
                     MonnifyPaymentOutcomeKind.PaidAfterExpiry)
            {
                _logger.LogWarning(
                    "Monnify reported a paid transaction after booking {BookingCode} expired; the room remains released.",
                    outcome.BookingCode);
            }

            return Ok(new { received = true });
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Rejected malformed signed Monnify webhook JSON.");
            return BadRequest();
        }
        catch (ServiceUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception) when (
            exception is BadRequestException or ConflictException)
        {
            _logger.LogWarning(
                "Rejected a signed Monnify payment because verification did not bind to the booking: {Reason}",
                exception.Message);
            return StatusCode(StatusCodes.Status409Conflict);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Monnify webhook processing failed.");
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    private async Task<string?> ReadRequestBodyAsync(
        CancellationToken cancellationToken)
    {
        if (Request.ContentLength > MaximumWebhookBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream(
            Request.ContentLength is > 0 and <= MaximumWebhookBytes
                ? (int)Request.ContentLength.Value
                : 4096);
        var rented = new byte[8192];
        int read;
        while ((read = await Request.Body.ReadAsync(
                   rented,
                   cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaximumWebhookBytes)
            {
                return null;
            }

            await buffer.WriteAsync(
                rented.AsMemory(0, read),
                cancellationToken);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
