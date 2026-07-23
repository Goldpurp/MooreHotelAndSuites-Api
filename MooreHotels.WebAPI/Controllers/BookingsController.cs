using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;
using MooreHotels.WebAPI.Extensions;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MooreHotels.Domain.Common;
using System.Security.Claims;

namespace MooreHotels.WebAPI.Controllers;

[ApiController]
[Route("api/bookings")]
public class BookingsController : ControllerBase
{
    private readonly IBookingService _bookingService;
    private readonly IMonnifyService _monnifyService;
    private readonly IMonnifyPaymentProcessor _monnifyPaymentProcessor;
    private readonly IBookingRepository _bookingRepo;
    private readonly MooreHotelsDbContext _dbContext;
    private readonly MonnifySettings _monnifySettings;

    public BookingsController(
        IBookingService bookingService,
        IMonnifyService monnifyService,
        IMonnifyPaymentProcessor monnifyPaymentProcessor,
        IBookingRepository bookingRepo,
        MooreHotelsDbContext dbContext,
        IOptions<MonnifySettings> monnifySettings)
    {
        _bookingService = bookingService;
        _monnifyService = monnifyService;
        _monnifyPaymentProcessor = monnifyPaymentProcessor;
        _bookingRepo = bookingRepo;
        _dbContext = dbContext;
        _monnifySettings = monnifySettings.Value;
    }

    [HttpGet]
    [Authorize(Roles = "Admin,Manager,Staff")]
    public async Task<IActionResult> GetAllBookings() 
        => Ok(await _bookingService.GetAllBookingsAsync());

    [HttpGet("{code}")]
    [Authorize(Roles = "Admin,Manager,Staff")]
    public async Task<IActionResult> GetBookingByCode(string code)
    {
        var dto = await _bookingService.GetBookingByCodeAsync(code);
        return dto == null ? NotFound() : Ok(dto);
    }

    [HttpGet("lookup")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.LookupRateLimitPolicy)]
    public async Task<IActionResult> LookupBooking([FromQuery] string code, [FromQuery] string email)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(email))
            return BadRequest(new { Message = "Booking code and associated email are required for lookup." });

        var dto = await _bookingService.GetBookingByCodeAndEmailAsync(code, email);
        return dto == null
            ? NotFound(new { Message = "No booking found with the provided credentials." })
            : Ok(ToPublicBooking(dto));
    }

    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.PublicWriteRateLimitPolicy)]
    public async Task<IActionResult> CreateBooking([FromBody] CreateBookingRequest request)
    {
        Guid? accountUserId = null;
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(userId, out var parsedUserId)) accountUserId = parsedUserId;

        var dto = await _bookingService.CreateBookingAsync(request, accountUserId);
        return Ok(ToPublicBooking(dto));
    }

    [HttpPost("{code}/verify-monnify")]
    [Authorize(Roles = "Admin,Manager")]
    [EnableRateLimiting(ServiceCollectionExtensions.PublicWriteRateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> VerifyMonnify(
        string code,
        [FromQuery] string? paymentReference,
        [FromQuery] string? transactionReference,
        CancellationToken cancellationToken)
    {
        if (!_monnifySettings.Enabled)
        {
            throw new ServiceUnavailableException(
                "Monnify payment verification is not enabled.");
        }

        // Legacy query parameters are accepted only so older dashboards keep
        // working. They are intentionally ignored: the server-owned reference
        // persisted during initialization is the sole verification input.
        _ = paymentReference;
        _ = transactionReference;
        var booking = await _bookingRepo.GetByCodeAsync(code.Trim().ToUpperInvariant());
        if (booking is null || booking.PaymentMethod != PaymentMethod.Monnify)
        {
            throw new BadRequestException("This is not a valid Monnify booking.");
        }

        if (booking.PaymentStatus == PaymentStatus.Paid)
        {
            return Ok(new { Message = "Payment was already verified.", Data = await _bookingService.GetBookingByCodeAsync(booking.BookingCode) });
        }

        if (string.IsNullOrWhiteSpace(booking.TransactionReference))
        {
            throw new ConflictException(
                "This booking does not have a server-owned Monnify payment reference.");
        }

        var verification = await _monnifyService.VerifyTransactionAsync(
            booking.TransactionReference,
            cancellationToken)
            ?? throw new BadRequestException(
                "Monnify has not confirmed this payment.");
        var actorValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(actorValue, out var actorId)) return Unauthorized();

        var outcome = await _monnifyPaymentProcessor.ApplyVerifiedPaymentAsync(
            booking.Id,
            verification,
            "AdminVerification",
            actorId,
            HttpContext.TraceIdentifier,
            cancellationToken);
        if (outcome.Kind == MonnifyPaymentOutcomeKind.PaidAfterExpiry)
        {
            throw new ConflictException(
                "Monnify received this payment after the booking expired. The room remains released and staff reconciliation or a refund is required.");
        }

        if (outcome.Kind == MonnifyPaymentOutcomeKind.Confirmed)
        {
            await _bookingService.SendPaymentConfirmationAsync(
                outcome.BookingCode,
                outcome.PaymentReference);
        }

        var dto = await _bookingService.GetBookingByCodeAsync(outcome.BookingCode);
        return Ok(new
        {
            Message = outcome.Kind == MonnifyPaymentOutcomeKind.AlreadyProcessed
                ? "Payment was already verified."
                : "Monnify payment verified successfully.",
            Data = dto!
        });
    }

    /// <summary>
    /// </summary>
    /// <remarks>
    /// The comparison is exact and case-sensitive. The optional transactionReference
    /// request property is ignored; the server creates the persisted reference.
    /// </remarks>
    [HttpPost("{bookingCode}/confirm-transfer")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType<ManualTransferConfirmationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ConfirmTransfer(
        string bookingCode,
        [FromBody] ConfirmTransferRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                request.ConfirmationText,
                ManualTransferConfirmation.RequiredText,
                StringComparison.Ordinal))
        {
            return BadRequest(new
            {
                Message = "confirmationText must be exactly 'ACCEPT' (case-sensitive, with no leading or trailing whitespace)."
            });
        }

        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

        var data = await _bookingService.ConfirmManualTransferAsync(
            bookingCode,
            request,
            userId,
            HttpContext.TraceIdentifier,
            cancellationToken);
        await _bookingService.SendPaymentConfirmationAsync(
            data.BookingCode,
            data.TransactionReference);
        return Ok(new ManualTransferConfirmationResponse(
            "Bank transfer payment confirmed manually.",
            data));
    }

    [HttpPut("{id}/status")]
    [Authorize(Roles = "Admin,Manager,Staff")]
    public async Task<IActionResult> UpdateBookingStatus(Guid id, [FromQuery] BookingStatus status)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();
        var dto = await ExecuteBookingMutationAsync(
            id,
            () => _bookingService.UpdateStatusAsync(id, status, userId));
        return Ok(dto);
    }
    
    [HttpPost("{id}/cancel")]
[Authorize(Roles = "Admin,Manager,Staff")]
public async Task<IActionResult> CancelBookingAdmin(Guid id, [FromQuery] string? reason = null)
{
    // 1. Robust User ID Extraction
    var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (!Guid.TryParse(userIdStr, out var userId))
    {
        return Unauthorized(new { Message = "User identity is invalid or expired." });
    }

    var dto = await ExecuteBookingMutationAsync(
        id,
        () => _bookingService.CancelBookingAsync(id, userId, reason));
    return Ok(new { Message = "Booking cancelled.", Data = dto });
}

[HttpPost("guest/cancel")]
[AllowAnonymous]
[EnableRateLimiting(ServiceCollectionExtensions.PublicWriteRateLimitPolicy)]
public async Task<IActionResult> CancelBookingGuest([FromBody] CancelBookingRequest request)
{
    // 3. Model State Check (Ensures BookingCode and Email aren't null)
    if (!ModelState.IsValid)
    {
        return BadRequest(ModelState);
    }

    var dto = await _bookingService.CancelBookingByGuestAsync(
        request.BookingCode,
        request.Email,
        request.Reason);
    return Ok(new { Message = "Your reservation has been cancelled successfully.", Data = ToPublicBooking(dto) });
}

[HttpPost("{id}/complete-refund")]
[Authorize(Roles = "Admin,Manager")]
public async Task<IActionResult> CompleteRefund(Guid id, [FromQuery] string transactionRef)
{
    var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (!Guid.TryParse(userIdStr, out var adminId)) return Unauthorized();

    var dto = await ExecuteBookingMutationAsync(
        id,
        () => _bookingService.CompleteRefundAsync(id, transactionRef, adminId));
    return Ok(new { Message = "Refund marked as completed in system.", Data = dto });
}

[HttpGet("pending-refunds")]
[Authorize(Roles = "Admin,Manager")]
public async Task<IActionResult> GetPendingRefunds() =>
    Ok(await _bookingService.GetPendingRefundsAsync());

private async Task<T> ExecuteBookingMutationAsync<T>(Guid bookingId, Func<Task<T>> mutation)
{
    var strategy = _dbContext.Database.CreateExecutionStrategy();
    return await strategy.ExecuteAsync(async () =>
    {
        _dbContext.ChangeTracker.Clear();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync();
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM bookings WHERE \"Id\" = {bookingId} FOR UPDATE");
        var result = await mutation();
        await transaction.CommitAsync();
        return result;
    });
}

private static PublicBookingDto ToPublicBooking(BookingDto booking) => new(
    booking.Id,
    booking.BookingCode,
    booking.RoomId,
    booking.GuestFirstName,
    booking.GuestLastName,
    booking.GuestEmail,
    booking.CheckIn,
    booking.CheckOut,
    booking.Status,
    booking.Amount,
    booking.PaymentStatus,
    booking.PaymentMethod,
    booking.CreatedAt,
    booking.PaymentUrl,
    booking.PaymentInstruction,
    booking.NotificationMessage,
    booking.PaymentExpiresAtUtc);



}
