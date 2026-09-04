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
    private readonly IPdfInvoiceGenerator _pdfGenerator;
    private readonly IAddOnService _addOnService;

    public BookingsController(
        IBookingService bookingService,
        IMonnifyService monnifyService,
        IMonnifyPaymentProcessor monnifyPaymentProcessor,
        IBookingRepository bookingRepo,
        MooreHotelsDbContext dbContext,
        IOptions<MonnifySettings> monnifySettings,
        IPdfInvoiceGenerator pdfGenerator,
        IAddOnService addOnService)
    {
        _bookingService = bookingService;
        _monnifyService = monnifyService;
        _monnifyPaymentProcessor = monnifyPaymentProcessor;
        _bookingRepo = bookingRepo;
        _dbContext = dbContext;
        _monnifySettings = monnifySettings.Value;
        _pdfGenerator = pdfGenerator;
        _addOnService = addOnService;
    }

    [HttpGet]
    [Authorize(Roles = "Admin,Manager,Staff")]
    public async Task<IActionResult> GetAllBookings(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] BookingStatus? status = null,
        [FromQuery] PaymentStatus? paymentStatus = null,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
        => Ok(await _bookingService.GetPagedBookingsAsync(page, pageSize, status, paymentStatus, search, cancellationToken));

    [HttpGet("{code}")]
    [Authorize(Roles = "Admin,Manager,Staff")]
    public async Task<IActionResult> GetBookingByCode(string code)
    {
        var dto = await _bookingService.GetBookingByCodeAsync(code);
        return dto == null ? NotFound() : Ok(dto);
    }

    [HttpGet("{code}/invoice.pdf")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.LookupRateLimitPolicy)]
    public async Task<IActionResult> DownloadInvoicePdf(string code, CancellationToken cancellationToken = default)
    {
        BookingDto? booking;
        if (User.IsInRole(nameof(UserRole.Admin)) ||
            User.IsInRole(nameof(UserRole.Manager)) ||
            User.IsInRole(nameof(UserRole.Staff)))
        {
            booking = await _bookingService.GetBookingByCodeAsync(code);
        }
        else
        {
            Guid? accountUserId = null;
            if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId))
                accountUserId = parsedUserId;

            var accessToken = Request.Headers["X-Booking-Access-Token"].ToString();
            booking = await _bookingService.GetBookingByCodeAndEmailAsync(
                code,
                email: null,
                accessToken,
                accountUserId);
        }

        // Use the same response for a missing reservation and failed access so
        // the invoice route cannot be used to enumerate booking codes.
        if (booking == null) return NotFound(new { Message = "Invoice not found." });

        var addOns = await _addOnService.GetBookingAddOnsAsync(code, cancellationToken);
        var pdfBytes = _pdfGenerator.GenerateInvoicePdf(booking, addOns);

        return File(pdfBytes, "application/pdf", $"Invoice-{code}.pdf");
    }

    [HttpGet("lookup")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.LookupRateLimitPolicy)]
    public async Task<IActionResult> LookupBooking(
        [FromQuery] string code,
        [FromQuery] string? email,
        [FromHeader(Name = "X-Booking-Access-Token")] string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(code))
            return BadRequest(new { Message = "Booking code is required for lookup." });

        Guid? accountUserId = null;
        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId))
            accountUserId = parsedUserId;
        if (string.IsNullOrWhiteSpace(accessToken) &&
            string.IsNullOrWhiteSpace(email) &&
            !accountUserId.HasValue)
        {
            return BadRequest(new { Message = "Use a secure booking link or sign in to view this reservation." });
        }

        var dto = await _bookingService.GetBookingByCodeAndEmailAsync(
            code,
            email,
            accessToken,
            accountUserId);
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

    [HttpPost("verification/request")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.PublicWriteRateLimitPolicy)]
    public async Task<IActionResult> RequestBookingEmailVerification(
        [FromBody] RequestBookingEmailVerificationRequest request,
        CancellationToken cancellationToken)
    {
        await _bookingService.RequestBookingEmailVerificationAsync(
            request.Email,
            cancellationToken);
        return Accepted(new
        {
            Message = "If the address can receive booking verification, a secure link will be sent."
        });
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

        Guid? accountUserId = null;
        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId))
            accountUserId = parsedUserId;

        var dto = await ExecuteBookingCodeMutationAsync(
            request.BookingCode,
            () => _bookingService.CancelBookingByGuestAsync(
                request.BookingCode,
                request.Email,
                request.GuestAccessToken,
                accountUserId,
                HttpContext.TraceIdentifier,
                request.Reason));
        return Ok(new { Message = "Your reservation has been cancelled successfully.", Data = ToPublicBooking(dto) });
    }

    [HttpPost("access-link")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceCollectionExtensions.LookupRateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> RequestAccessLink(
        [FromBody] RequestBookingAccessLinkRequest request)
    {
        if (ModelState.IsValid)
        {
            await ExecuteBookingCodeMutationAsync(
                request.BookingCode,
                () => _bookingService.RequestBookingAccessLinkAsync(
                    request.BookingCode,
                    request.Email,
                    HttpContext.TraceIdentifier));
        }

        // Deliberately identical for existing and unknown reservations to prevent
        // booking-code or guest-email discovery.
        return Accepted(new
        {
            Message = "If the booking details match, a secure access link will be sent to the booking email."
        });
    }

    [HttpPost("{id}/approve-refund")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> ApproveRefund(
        Guid id,
        [FromBody] ApproveRefundRequest request)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var approvingUserId)) return Unauthorized();

        var dto = await ExecuteBookingMutationAsync(
            id,
            () => _bookingService.ApproveRefundAsync(id, request, approvingUserId));
        return Ok(new
        {
            Message = "High-value refund approved for independent completion.",
            Data = dto
        });
    }

    [HttpPost("{id}/complete-refund")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> CompleteRefund(
        Guid id,
        [FromBody] CompleteRefundRequest request)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var adminId)) return Unauthorized();

        var dto = await ExecuteBookingMutationAsync(
            id,
            () => _bookingService.CompleteRefundAsync(id, request, adminId));
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

    private async Task<T> ExecuteBookingCodeMutationAsync<T>(
        string bookingCode,
        Func<Task<T>> mutation)
    {
        var normalizedCode = bookingCode.Trim().ToUpperInvariant();
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _dbContext.ChangeTracker.Clear();
            await using var transaction = await _dbContext.Database.BeginTransactionAsync();
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM bookings WHERE \"BookingCode\" = {normalizedCode} FOR UPDATE");
            var result = await mutation();
            await transaction.CommitAsync();
            return result;
        });
    }

    private async Task ExecuteBookingCodeMutationAsync(
        string bookingCode,
        Func<Task> mutation)
    {
        await ExecuteBookingCodeMutationAsync(
            bookingCode,
            async () =>
            {
                await mutation();
                return true;
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
        booking.PaymentExpiresAtUtc,
        booking.GuestAccessToken,
        booking.GuestAccessExpiresAtUtc);



}
