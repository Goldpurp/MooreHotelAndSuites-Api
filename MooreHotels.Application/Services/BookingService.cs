using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Application.Exceptions;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Domain.Common;
using System.Security.Claims;
using System.Text.Json;

namespace MooreHotels.Application.Services;

public class BookingService : IBookingService
{
    private readonly IBookingRepository _bookingRepo;
    private readonly IRoomRepository _roomRepo;
    private readonly IGuestRepository _guestRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IEmailService _emailService;
    private readonly IMonnifyService _monnifyService;
    private readonly IVisitRecordService _visitService;
    private readonly INotificationService _notificationService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _config;
    private readonly ILogger<BookingService> _logger;


    private const int CHECK_IN_HOUR = 14; // 2:00 PM
    private const int CHECK_OUT_HOUR = 12; // 12:00 PM

    public BookingService(
        IBookingRepository bookingRepo, 
        IRoomRepository roomRepo, 
        IGuestRepository guestRepo, 
        IAuditLogRepository auditRepo,
        IEmailService emailService,
        IMonnifyService monnifyService,
        IVisitRecordService visitService,
        INotificationService notificationService,
        UserManager<ApplicationUser> userManager,
        IConfiguration config,
        ILogger<BookingService> logger)

    {
        _bookingRepo = bookingRepo;
        _roomRepo = roomRepo;
        _guestRepo = guestRepo;
        _auditRepo = auditRepo;
        _emailService = emailService;
        _monnifyService = monnifyService;
        _visitService = visitService;
        _notificationService = notificationService;
        _userManager = userManager;
        _config = config;
        _logger = logger;
    }

    public async Task<BookingDto> CreateBookingAsync(CreateBookingRequest request, Guid? accountUserId = null)
    {
        // 1. Validation Logic
        if (string.IsNullOrWhiteSpace(request.GuestEmail)) throw new BadRequestException("Guest email is required.");
        if (string.IsNullOrWhiteSpace(request.GuestFirstName)) throw new BadRequestException("Guest first name is required.");
        if (string.IsNullOrWhiteSpace(request.GuestLastName)) throw new BadRequestException("Guest last name is required.");
        if (string.IsNullOrWhiteSpace(request.GuestPhone)) throw new BadRequestException("Guest phone number is required.");
        if (!request.PaymentMethod.HasValue) throw new BadRequestException("A payment method is required.");
        if (request.PaymentMethod == PaymentMethod.Monnify &&
            !_config.GetValue<bool>("MonnifySettings:Enabled"))
        {
            throw new BadRequestException(
                "Online payment is temporarily unavailable. Please choose direct bank transfer.");
        }
        
        var room = await _roomRepo.GetByIdAsync(request.RoomId);
        if (room == null) throw new NotFoundException("Room not found.");
        
        var checkIn = DateTime.SpecifyKind(request.CheckIn.Date.AddHours(CHECK_IN_HOUR), DateTimeKind.Utc);
        var checkOut = DateTime.SpecifyKind(request.CheckOut.Date.AddHours(CHECK_OUT_HOUR), DateTimeKind.Utc);

        if (request.CheckIn.Date < DateTime.UtcNow.Date)
            throw new BadRequestException("Check-in cannot be in the past.");
        if (checkOut <= checkIn) throw new BadRequestException("Check-out must be after check-in date.");
        if ((checkOut.Date - checkIn.Date).Days > 90)
            throw new BadRequestException("A single reservation cannot exceed 90 nights.");
        if (checkIn.Date > DateTime.UtcNow.Date.AddYears(2))
            throw new BadRequestException("Reservations cannot be created more than two years in advance.");
        if (!room.IsOnline) throw new BadRequestException("This room is currently unavailable.");
        
        // 2. Conflict Check
        if (await _bookingRepo.IsRoomBookedAsync(room.Id, checkIn, checkOut)) 
            throw new BadRequestException("This room is already reserved for the selected dates.");

        // Authenticated client reservations always use the guest identity linked
        // to the signed-in account. This prevents name/email variations from
        // creating an unlinked profile or exposing another guest's history.
        Guest? guest = null;
        if (accountUserId.HasValue)
        {
            var account = await _userManager.FindByIdAsync(accountUserId.Value.ToString());
            if (account?.Role == UserRole.Client)
            {
                if (string.IsNullOrWhiteSpace(account.GuestId))
                    throw new BadRequestException("This client account requires guest-profile reconciliation before booking.");

                guest = await _guestRepo.GetByIdAsync(account.GuestId);
                if (guest is null)
                    throw new BadRequestException("This client account requires guest-profile reconciliation before booking.");
            }
        }

        // Anonymous reservations retain the public booking flow and identify an
        // existing guest only by an exact normalized e-mail and name match.
        guest ??= await _guestRepo.GetByEmailAndNameAsync(
            request.GuestEmail.Trim().ToLowerInvariant(),
            request.GuestFirstName.Trim(),
            request.GuestLastName.Trim());

        if (guest == null)
        {
            guest = new Guest
            {
                Id = $"GS-{Guid.NewGuid():N}"[..19].ToUpperInvariant(),
                Email = request.GuestEmail.Trim().ToLowerInvariant(),
                FirstName = request.GuestFirstName.Trim(),
                LastName = request.GuestLastName.Trim(),
                Phone = request.GuestPhone.Trim()
            };
            await _guestRepo.AddAsync(guest);
        }

        var nights = Math.Max(1, (checkOut.Date - checkIn.Date).Days);
        var totalAmount = room.PricePerNight * nights;
        var bookingCode = await _bookingRepo.GenerateBookingCodeAsync();

        // 4. Build the booking. For Monnify, initialize against a reference
        // generated and owned by this server before persisting the booking.
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            BookingCode = bookingCode,
            RoomId = room.Id,
            GuestId = guest.Id,
            CheckIn = checkIn,
            CheckOut = checkOut,
            Status = BookingStatus.Pending,
            Amount = totalAmount,
            PaymentStatus = request.PaymentMethod == PaymentMethod.DirectTransfer ? PaymentStatus.AwaitingVerification : PaymentStatus.Unpaid,
            PaymentMethod = request.PaymentMethod.Value,
            Notes = request.Notes,
            StatusHistoryJson = "[]", // FIX: Initialised as empty JSON array to prevent Deserialization errors
            CreatedAt = DateTime.UtcNow
        };

        string? paymentUrl = null;
        if (booking.PaymentMethod == PaymentMethod.Monnify)
        {
            var paymentReference = $"{booking.BookingCode}-{Guid.NewGuid():N}";
            booking.TransactionReference = paymentReference;
            var publicAppUrl = _config["PublicAppUrl"]
                ?? throw new InvalidOperationException("PublicAppUrl is not configured.");
            var callbackUrl = QueryHelpers.AddQueryString(
                $"{publicAppUrl.TrimEnd('/')}/booking-status",
                "code",
                booking.BookingCode);

            var initialization = await _monnifyService.InitializeMonnifyPaymentAsync(
                guest.Email,
                $"{guest.FirstName} {guest.LastName}",
                totalAmount,
                booking.Id,
                booking.BookingCode,
                paymentReference,
                callbackUrl);
            booking.PaymentProviderReference =
                initialization.TransactionReference;
            booking.PaymentCheckoutUrl = initialization.CheckoutUrl;
            booking.PaymentCheckoutExpiresAtUtc =
                booking.CreatedAt.Add(MonnifyPaymentPolicy.HostedCheckoutWindow);
            paymentUrl = initialization.CheckoutUrl;
        }

        await _bookingRepo.AddAsync(booking);
        
        // 5. Critical Communication (Awaited for reliability)
        try 
        {
            // FIX: Awaited this call so the user knows immediately if the confirmation failed to dispatch
            await _emailService.SendBookingConfirmationAsync(
                guest.Email, 
                $"{guest.FirstName} {guest.LastName}", 
                booking.BookingCode, 
                room.Name, 
                room.Category.ToString(),
                room.Capacity,
                booking.CheckIn, 
                booking.CheckOut,
                nights,
                totalAmount);
        }
        catch (Exception ex) 
        {
            _logger.LogError(ex, "Initial booking confirmation failed for {Code}", booking.BookingCode);
        }

        // 6. Notifications & Admin Alerts. Scoped services are awaited so work
        // cannot be lost when the request scope is disposed.
        try
        {
            await _notificationService.NotifyNewBookingAsync(
                booking,
                $"{guest.FirstName} {guest.LastName}",
                room.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "New-booking notification failed for {Code}.", booking.BookingCode);
        }
        
        var adminEmail = _config["EmailSettings:AdminNotificationEmail"] ?? _config["EmailSettings:SenderEmail"];
        if (!string.IsNullOrEmpty(adminEmail))
        {
            try
            {
                await _emailService.SendAdminNewBookingAlertAsync(
                    adminEmail,
                    $"{guest.FirstName} {guest.LastName}",
                    booking.BookingCode,
                    room.Name,
                    room.Category.ToString(),
                    room.Capacity,
                    booking.CheckIn,
                    booking.CheckOut,
                    nights,
                    totalAmount,
                    guest.Email,
                    guest.Phone);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Admin booking alert failed for {Code}.", booking.BookingCode);
            }
        }
            
        string? paymentInstruction = (booking.PaymentMethod == PaymentMethod.DirectTransfer) ? GetTransferInstructions() : null;

        return MapToDto(booking, guest) with
        {
            PaymentUrl = paymentUrl,
            PaymentInstruction = paymentInstruction
        };
    }

    public async Task<BookingDto?> GetBookingByCodeAsync(string code)
    {
        var b = await _bookingRepo.GetByCodeAsync(code);
        return b != null ? MapToDto(b) : null;
    }

    public async Task<BookingDto?> GetBookingByCodeAndEmailAsync(string code, string email)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(email)) return null;
        var b = await _bookingRepo.GetByCodeAsync(code.Trim().ToUpper());
        if (b != null && b.Guest?.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase) == true)
            return MapToDto(b);
        return null;
    }

    public async Task<IEnumerable<BookingDto>> GetAllBookingsAsync()
    {
        var bookings = await _bookingRepo.GetAllAsync();
        return bookings.Select(booking => MapToDto(booking));
    }

    public async Task<BookingDto> UpdateStatusAsync(Guid bookingId, BookingStatus status, Guid userId)
    {
        var booking = await _bookingRepo.GetByIdAsync(bookingId);
        if (booking == null) throw new NotFoundException("Booking not found.");

        // FIX: Ensure Guest navigation property is loaded to prevent NullRef in Emails
        if (booking.Guest == null) throw new InvalidOperationException("Booking guest data could not be loaded.");

        var actingUser = await _userManager.FindByIdAsync(userId.ToString());
        var room = await _roomRepo.GetByIdAsync(booking.RoomId);
        var oldStatus = booking.Status;

        if (status is not (BookingStatus.CheckedIn or BookingStatus.CheckedOut or BookingStatus.NoShow))
            throw new BadRequestException("Use the dedicated payment or cancellation workflow for this status change.");
        
        if (status == BookingStatus.CheckedIn)
        {
            var now = DateTime.UtcNow;
            if (booking.Status != BookingStatus.Confirmed)
                throw new BadRequestException("Only a confirmed booking can be checked in.");
            if (now > booking.CheckOut) throw new BadRequestException("This booking is in the past.");
            if (now > booking.CheckOut.AddMinutes(-30)) throw new BadRequestException("This booking is too close to checkout for check-in.");
            
            if (now < booking.CheckIn) throw new BadRequestException("Arrival is too early. Official check-in starts at 2:00 PM.");
            if (booking.PaymentStatus != PaymentStatus.Paid) throw new BadRequestException("Full payment verification is required.");
            if (room != null) { room.Status = RoomStatus.Occupied; await _roomRepo.UpdateAsync(room); }
            await _visitService.CreateRecordAsync(booking.BookingCode, "CHECK_IN", actingUser?.Name ?? "Admin");
        }
        else if (status == BookingStatus.CheckedOut)
        {
            if (booking.Status != BookingStatus.CheckedIn)
                throw new BadRequestException("Only a checked-in booking can be checked out.");
            if (room != null) { room.Status = RoomStatus.Cleaning; await _roomRepo.UpdateAsync(room); }
            await _visitService.CreateRecordAsync(booking.BookingCode, "CHECK_OUT", actingUser?.Name ?? "Admin");
        }
        else if (status == BookingStatus.NoShow)
        {
            if (booking.Status == BookingStatus.CheckedIn || booking.Status == BookingStatus.CheckedOut)
                throw new BadRequestException("An active or completed stay cannot be marked as a no-show.");
            if (DateTime.UtcNow < booking.CheckIn)
                throw new BadRequestException("A booking cannot be marked as a no-show before check-in time.");

            if (room != null)
            {
                room.Status = RoomStatus.Available;
                await _roomRepo.UpdateAsync(room);
            }
        }


        // FIX: Robust JSON handling logic
        var rawHistory = string.IsNullOrWhiteSpace(booking.StatusHistoryJson) ? "[]" : booking.StatusHistoryJson;
        var history = JsonSerializer.Deserialize<List<object>>(rawHistory) ?? new List<object>();
        
        history.Add(new { Status = status, Timestamp = DateTime.UtcNow, Actor = actingUser?.Name ?? "System" });
        booking.StatusHistoryJson = JsonSerializer.Serialize(history);
        booking.Status = status;
        
        await _bookingRepo.UpdateAsync(booking);
        
        await _auditRepo.AddAsync(new AuditLog {
            Id = Guid.NewGuid(), ProfileId = userId, Action = "LIFECYCLE_TRANSITION",
            EntityType = "Booking", EntityId = booking.Id.ToString(),
            OldDataJson = JsonSerializer.Serialize(new { Status = oldStatus }),
            NewDataJson = JsonSerializer.Serialize(new { Status = status })
        });

        if (status == BookingStatus.CheckedOut)
        {
            try
            {
                await _emailService.SendCheckOutThankYouAsync(
                    booking.Guest.Email,
                    booking.Guest.FirstName,
                    booking.BookingCode,
                    room?.Name ?? "Reserved Room");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Checkout email failed for {Code}.",
                    booking.BookingCode);
            }
        }
        else if (status == BookingStatus.NoShow)
        {
            try
            {
                await _emailService.SendCancellationNoticeAsync(
                    booking.Guest.Email,
                    $"{booking.Guest.FirstName} {booking.Guest.LastName}",
                    booking.BookingCode,
                    room?.Name ?? "Reserved Room",
                    room?.Category.ToString() ?? "Standard",
                    booking.CheckIn,
                    "The reservation was marked as a no-show after the scheduled check-in time.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "No-show email failed for {Code}.",
                    booking.BookingCode);
            }
        }

        return MapToDto(booking);
    }

    public async Task<ManualTransferConfirmationDto> ConfirmManualTransferAsync(
        string bookingCode,
        ConfirmTransferRequest request,
        Guid actingUserId,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(
                request.ConfirmationText,
                ManualTransferConfirmation.RequiredText,
                StringComparison.Ordinal))
        {
            throw new BadRequestException(
                "confirmationText must be exactly 'ACCEPT' (case-sensitive, with no leading or trailing whitespace).");
        }

        var actingUser = await _userManager.FindByIdAsync(actingUserId.ToString());
        if (actingUser is null || actingUser.Role is not (UserRole.Admin or UserRole.Manager))
        {
            throw new UnauthorizedAccessException(
                "Only an authenticated Admin or Manager can confirm a bank transfer.");
        }

        return await _bookingRepo.ConfirmManualTransferAsync(
            bookingCode.Trim().ToUpperInvariant(),
            new ManualTransferConfirmationActor(
                actingUser.Id,
                actingUser.Name,
                actingUser.Role,
                requestId),
            cancellationToken);
    }

    public async Task SendPaymentConfirmationAsync(string bookingCode, string reference)
    {
        var booking = await _bookingRepo.GetByCodeAsync(bookingCode);
        if (booking?.Guest != null)
        {
            var room = await _roomRepo.GetByIdAsync(booking.RoomId);
            try
            {
                await _emailService.SendPaymentSuccessAsync(
                    booking.Guest.Email,
                    booking.Guest.FirstName,
                    booking.BookingCode,
                    room?.Name ?? "Reserved Room",
                    booking.Amount,
                    reference);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Payment confirmation email failed for {Code}.", booking.BookingCode);
            }
        }
    }
    
public async Task<BookingDto> CancelBookingAsync(Guid bookingId, Guid userId, string? reason = null)
{
    var booking = await _bookingRepo.GetByIdAsync(bookingId);
    if (booking == null) throw new KeyNotFoundException("Booking record not found.");

    if (booking.Status == BookingStatus.Cancelled) return MapToDto(booking);

    if (booking.Status is BookingStatus.CheckedIn or BookingStatus.CheckedOut or BookingStatus.NoShow)
        throw new BadRequestException("Active or completed stays cannot be cancelled.");

    var actingUser = await _userManager.FindByIdAsync(userId.ToString());
    var oldStatus = booking.Status;
    var room = await _roomRepo.GetByIdAsync(booking.RoomId);

    booking.Status = BookingStatus.Cancelled;
    booking.PaymentCheckoutUrl = null;

    // --- TRIGGER REFUND LOGIC ---
    if (booking.PaymentStatus == PaymentStatus.Paid)
    {
        booking.PaymentStatus = PaymentStatus.RefundPending;
    }

    var history = JsonSerializer.Deserialize<List<object>>(booking.StatusHistoryJson ?? "[]") ?? new();
    history.Add(new { 
        Status = BookingStatus.Cancelled, 
        Timestamp = DateTime.UtcNow, 
        Actor = actingUser?.UserName ?? "Staff", 
        Reason = reason ?? "Cancelled by Admin",
        PaymentShift = booking.PaymentStatus.ToString() // Will show 'RefundPending' if it was 'Paid'
    });
    booking.StatusHistoryJson = JsonSerializer.Serialize(history);

    await _bookingRepo.UpdateAsync(booking);

    await _auditRepo.AddAsync(new AuditLog
    {
        Id = Guid.NewGuid(),
        ProfileId = userId,
        Action = "BOOKING_CANCELLED",
        EntityType = "Booking",
        EntityId = booking.Id.ToString(),
        OldDataJson = JsonSerializer.Serialize(new { Status = oldStatus.ToString() }),
        NewDataJson = JsonSerializer.Serialize(new
        {
            Status = booking.Status.ToString(),
            PaymentStatus = booking.PaymentStatus.ToString(),
            Reason = reason
        }),
        CreatedAt = DateTime.UtcNow
    });

    try
    {
        await _emailService.SendCancellationNoticeAsync(
                booking.Guest!.Email, 
                $"{booking.Guest.FirstName} {booking.Guest.LastName}", 
                booking.BookingCode, 
                room?.Name ?? "Reserved Room", 
                room?.Category.ToString() ?? "Standard",
                booking.CheckIn, 
                reason);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Staff cancellation email failed for {Code}.", booking.BookingCode);
    }

     if (booking.PaymentStatus == PaymentStatus.RefundPending)
    {
        var adminEmail =
            _config["EmailSettings:AdminNotificationEmail"];
        if (!string.IsNullOrEmpty(adminEmail))
        {
            try
            {
                await _emailService.SendAdminRefundAlertAsync(
                adminEmail, 
                $"{booking.Guest?.FirstName} {booking.Guest?.LastName}", 
                booking.BookingCode, 
                room?.Name ?? "Reserved Room", 
                booking.Amount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Admin refund alert failed for {Code}.", booking.BookingCode);
            }
        }
    }
    return MapToDto(booking);
}

public async Task<BookingDto> CancelBookingByGuestAsync(string bookingCode, string email, string? reason = null)
{
    var booking = await _bookingRepo.GetByCodeAsync(bookingCode.Trim().ToUpper());
    
    if (booking == null || !booking.Guest!.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase))
        throw new UnauthorizedAccessException("Verification failed.");

    if (booking.Status == BookingStatus.Cancelled) return MapToDto(booking);

    if (booking.Status is BookingStatus.CheckedIn or BookingStatus.CheckedOut or BookingStatus.NoShow ||
        DateTime.UtcNow >= booking.CheckIn)
        throw new BadRequestException("A stay in progress or a completed stay cannot be cancelled online.");

    var room = await _roomRepo.GetByIdAsync(booking.RoomId);
    booking.Status = BookingStatus.Cancelled;
    booking.PaymentCheckoutUrl = null;

    // --- TRIGGER REFUND LOGIC ---
    if (booking.PaymentStatus == PaymentStatus.Paid)
    {
        booking.PaymentStatus = PaymentStatus.RefundPending;
    }

    var history = JsonSerializer.Deserialize<List<object>>(booking.StatusHistoryJson ?? "[]") ?? new();
    history.Add(new { 
        Status = BookingStatus.Cancelled, 
        Timestamp = DateTime.UtcNow, 
        Actor = "Guest", 
        Reason = reason ?? "Self-service cancellation",
        PaymentShift = booking.PaymentStatus.ToString()
    });
    booking.StatusHistoryJson = JsonSerializer.Serialize(history);

    await _bookingRepo.UpdateAsync(booking);

    try
    {
        await _emailService.SendCancellationNoticeAsync(
                booking.Guest.Email, 
                $"{booking.Guest.FirstName} {booking.Guest.LastName}", 
                booking.BookingCode, 
                room?.Name ?? "Reserved Room", 
                room?.Category.ToString() ?? "Standard",
                booking.CheckIn, 
                reason);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Guest cancellation email failed for {Code}.", booking.BookingCode);
    }

      if (booking.PaymentStatus == PaymentStatus.RefundPending)
    {
        var adminEmail =
            _config["EmailSettings:AdminNotificationEmail"];
        if (!string.IsNullOrEmpty(adminEmail))
        {
            try
            {
                await _emailService.SendAdminRefundAlertAsync(
                adminEmail, 
                $"{booking.Guest!.FirstName} {booking.Guest.LastName}", 
                booking.BookingCode, 
                room?.Name ?? "Reserved Room", 
                booking.Amount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Admin refund alert failed for {Code}.", booking.BookingCode);
            }
        }
    }

    return MapToDto(booking);
}

public async Task<BookingDto> CompleteRefundAsync(Guid bookingId, string transactionRef, Guid adminId)
{
    if (string.IsNullOrWhiteSpace(transactionRef) || transactionRef.Trim().Length is < 4 or > 160)
        throw new BadRequestException("A valid external refund reference is required.");
    var booking = await _bookingRepo.GetByIdAsync(bookingId);
    if (booking == null) throw new NotFoundException("Booking not found.");
    
    if (booking.PaymentStatus != PaymentStatus.RefundPending)
        throw new BadRequestException("This booking is not flagged for a manual refund.");

    // Update the state
    booking.PaymentStatus = PaymentStatus.Refunded;
    booking.RefundReference = transactionRef.Trim();

    // Add to history for tracking
    var history = JsonSerializer.Deserialize<List<object>>(booking.StatusHistoryJson ?? "[]") ?? new();
    history.Add(new { 
        Action = "MANUAL_REFUND_COMPLETED", 
        Timestamp = DateTime.UtcNow, 
        Reference = transactionRef,
        AdminId = adminId 
    });
    booking.StatusHistoryJson = JsonSerializer.Serialize(history);

    await _bookingRepo.UpdateAsync(booking);
    await _auditRepo.AddAsync(new AuditLog
    {
        Id = Guid.NewGuid(),
        ProfileId = adminId,
        Action = "REFUND_COMPLETED",
        EntityType = "Booking",
        EntityId = booking.Id.ToString(),
        NewDataJson = JsonSerializer.Serialize(new
        {
            booking.BookingCode,
            PaymentStatus = booking.PaymentStatus.ToString(),
            RefundReference = booking.RefundReference,
            booking.Amount
        }),
        CreatedAt = DateTime.UtcNow
    });
    try
    {
        var room = await _roomRepo.GetByIdAsync(booking.RoomId);
        await _emailService.SendRefundCompletionNoticeAsync(
                booking.Guest!.Email, 
                booking.Guest.FirstName, 
                booking.BookingCode, 
                room?.Name ?? "Reserved Room",
                booking.Amount, 
                transactionRef);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Refund completion email failed for {Code}.", booking.BookingCode);
    }
    
    return MapToDto(booking);
}

public async Task<IEnumerable<BookingDto>> GetPendingRefundsAsync()
{
    var bookings = await _bookingRepo.GetPendingRefundsAsync();
    return bookings.Select(booking => MapToDto(booking));
}


    private static BookingDto MapToDto(Booking b, Guest? loadedGuest = null)
    {
        var guest = loadedGuest ?? b.Guest;
        string? msg = null;
        var now = DateTime.UtcNow;
        DateTime? paymentExpiresAtUtc = null;
        if (b.Status == BookingStatus.CheckedIn)
        {
            if (now > b.CheckOut.AddMinutes(-30) && now <= b.CheckOut)
                msg = "Guest checks out in 30mins";
        }
        else if (b.Status == BookingStatus.Pending &&
                 b.PaymentStatus is PaymentStatus.Unpaid or PaymentStatus.AwaitingVerification)
        {
            paymentExpiresAtUtc = BookingPaymentPolicy.GetConfirmationDeadlineUtc(b.CreatedAt);
            var remaining = paymentExpiresAtUtc.Value - now;
            if (remaining <= TimeSpan.Zero)
                msg = "Payment confirmation window expired";
            else if (remaining <= TimeSpan.FromMinutes(30))
                msg = $"Payment confirmation expires in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} minutes";
        }
        else if (b.Status == BookingStatus.Confirmed)
        {
            if (now > b.CheckOut.AddMinutes(-30) && now <= b.CheckOut)
                msg = "Checkout is due within 30 minutes";
            else if (now > b.CheckOut)
                msg = "Booking is in the past";
        }

        return new BookingDto(
            b.Id, b.BookingCode, b.RoomId, b.GuestId,
            guest?.FirstName ?? "", guest?.LastName ?? "", guest?.Email ?? "", guest?.Phone ?? "",
            b.CheckIn, b.CheckOut,
            b.Status, b.Amount, b.PaymentStatus, b.PaymentMethod, b.TransactionReference, b.Notes, b.CreatedAt,
            PaymentUrl:
                b.PaymentMethod == PaymentMethod.Monnify &&
                b.Status == BookingStatus.Pending &&
                b.PaymentStatus == PaymentStatus.Unpaid &&
                b.PaymentCheckoutExpiresAtUtc > now
                    ? b.PaymentCheckoutUrl
                    : null,
            NotificationMessage: msg,
            RefundReference: b.RefundReference,
            PaymentExpiresAtUtc: paymentExpiresAtUtc);
    }

    private string GetTransferInstructions()
    {
        var bankName = _config["BankTransferSettings:BankName"] ?? "Contact the hotel";
        var accountName = _config["BankTransferSettings:AccountName"] ?? "Contact the hotel";
        var accountNumber = _config["BankTransferSettings:AccountNumber"] ?? "Contact the hotel";
        return "Please transfer the total amount to:\n" +
               $"Bank: {bankName}\n" +
               $"Account Name: {accountName}\n" +
               $"Account Number: {accountNumber}\n" +
               "Ref: [Your Booking Code]";
    }
}
