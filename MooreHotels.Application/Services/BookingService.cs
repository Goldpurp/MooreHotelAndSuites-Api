using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MooreHotels.Application.Common;
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
    private readonly IEmailOutbox _emailOutbox;
    private readonly IMonnifyService _monnifyService;
    private readonly IVisitRecordService _visitService;
    private readonly INotificationService _notificationService;
    private readonly IHotelTimeService _hotelTime;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _config;
    private readonly ILogger<BookingService> _logger;


    public BookingService(
        IBookingRepository bookingRepo,
        IRoomRepository roomRepo,
        IGuestRepository guestRepo,
        IAuditLogRepository auditRepo,
        IEmailOutbox emailOutbox,
        IMonnifyService monnifyService,
        IVisitRecordService visitService,
        INotificationService notificationService,
        IHotelTimeService hotelTime,
        UserManager<ApplicationUser> userManager,
        IConfiguration config,
        ILogger<BookingService> logger)

    {
        _bookingRepo = bookingRepo;
        _roomRepo = roomRepo;
        _guestRepo = guestRepo;
        _auditRepo = auditRepo;
        _emailOutbox = emailOutbox;
        _monnifyService = monnifyService;
        _visitService = visitService;
        _notificationService = notificationService;
        _hotelTime = hotelTime;
        _userManager = userManager;
        _config = config;
        _logger = logger;
    }

    public async Task RequestBookingEmailVerificationAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new BadRequestException("A valid guest email is required.");

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var token = BookingGuestAccess.GenerateToken();
        var now = DateTime.UtcNow;
        var publicAppUrl = _config["PublicAppUrl"]
            ?? throw new InvalidOperationException("PublicAppUrl is not configured.");
        var verificationLink = FrontendLinkBuilder.WithFragment(
            publicAppUrl,
            "/book",
            [
                new("email", normalizedEmail),
                new("bookingVerificationToken", token)
            ]);
        var verification = new BookingEmailVerification
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            TokenHash = BookingGuestAccess.Hash(token),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(BookingEmailVerificationPolicy.TokenLifetime)
        };
        var emailMessage = _emailOutbox.Create(
            TransactionalEmailTemplates.BookingEmailVerification,
            normalizedEmail,
            new BookingEmailVerificationEmail(verificationLink));

        await _bookingRepo.QueueBookingEmailVerificationAsync(
            verification,
            emailMessage,
            now.Subtract(BookingEmailVerificationPolicy.RequestCooldown),
            cancellationToken);
    }

    public async Task<BookingDto> CreateBookingAsync(CreateBookingRequest request, Guid? accountUserId = null)
    {
        // 1. Validation Logic
        if (string.IsNullOrWhiteSpace(request.GuestEmail)) throw new BadRequestException("Guest email is required.");
        if (string.IsNullOrWhiteSpace(request.GuestFirstName)) throw new BadRequestException("Guest first name is required.");
        if (string.IsNullOrWhiteSpace(request.GuestLastName)) throw new BadRequestException("Guest last name is required.");
        if (string.IsNullOrWhiteSpace(request.GuestPhone)) throw new BadRequestException("Guest phone number is required.");
        if (request.AdultCount < 1 || request.ChildCount < 0)
            throw new BadRequestException("A booking requires at least one adult and cannot contain a negative guest count.");
        if (!request.PaymentMethod.HasValue) throw new BadRequestException("A payment method is required.");
        if (request.PaymentMethod == PaymentMethod.Paystack)
        {
            throw new BadRequestException(
                "Paystack is not a supported payment method. Please choose Monnify or direct bank transfer.");
        }
        if (request.PaymentMethod == PaymentMethod.Monnify &&
            !_config.GetValue<bool>("MonnifySettings:Enabled"))
        {
            throw new BadRequestException(
                "Online payment is temporarily unavailable. Please choose direct bank transfer.");
        }

        var privacy = _config.GetSection("Privacy").Get<PrivacySettings>() ?? new PrivacySettings();
        var submittedPolicyAcceptance = request.AcceptPrivacyPolicy ||
                                        request.AcceptBookingTerms ||
                                        !string.IsNullOrWhiteSpace(request.PrivacyPolicyVersion) ||
                                        !string.IsNullOrWhiteSpace(request.BookingTermsVersion);
        if ((privacy.RequirePolicyAcceptance || submittedPolicyAcceptance) &&
            (!request.AcceptPrivacyPolicy ||
             !request.AcceptBookingTerms ||
             !string.Equals(
                 request.PrivacyPolicyVersion,
                 privacy.CurrentPrivacyPolicyVersion,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 request.BookingTermsVersion,
                 privacy.CurrentBookingTermsVersion,
                 StringComparison.Ordinal)))
        {
            throw new BadRequestException(
                "Accept the current privacy policy and booking terms before creating a reservation.");
        }

        var normalizedEmail = request.GuestEmail.Trim().ToLowerInvariant();

        // Authenticated client reservations use the guest identity linked to
        // the signed-in account and do not require a second email challenge.
        Guest? guest = null;
        var linkedClientBooking = false;
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
                linkedClientBooking = true;
            }
        }

        BookingEmailVerificationProof? emailVerification = null;
        if (!linkedClientBooking &&
            _config.GetValue("Runtime:RequirePublicBookingEmailVerification", true))
        {
            var token = request.EmailVerificationToken?.Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new BadRequestException(
                    "Verify the guest email before creating this booking.");
            }

            var now = DateTime.UtcNow;
            var tokenHash = BookingGuestAccess.Hash(token);
            if (!await _bookingRepo.IsBookingEmailVerificationValidAsync(
                    normalizedEmail,
                    tokenHash,
                    now))
            {
                throw new BadRequestException(
                    "Verify the guest email again before creating this booking.");
            }

            emailVerification = new BookingEmailVerificationProof(
                normalizedEmail,
                tokenHash,
                now);
        }

        var room = await _roomRepo.GetByIdAsync(request.RoomId);
        if (room == null) throw new NotFoundException("Room not found.");

        var checkIn = _hotelTime.GetCheckInUtc(request.CheckIn);
        var checkOut = _hotelTime.GetCheckOutUtc(request.CheckOut);

        var checkInDate = DateOnly.FromDateTime(request.CheckIn);
        var checkOutDate = DateOnly.FromDateTime(request.CheckOut);
        if (checkInDate < _hotelTime.Today)
            throw new BadRequestException("Check-in cannot be in the past.");
        if (checkOut <= checkIn) throw new BadRequestException("Check-out must be after check-in date.");
        if (checkOutDate.DayNumber - checkInDate.DayNumber > 90)
            throw new BadRequestException("A single reservation cannot exceed 90 nights.");
        if (checkInDate > _hotelTime.Today.AddYears(2))
            throw new BadRequestException("Reservations cannot be created more than two years in advance.");
        if (!room.IsOnline) throw new BadRequestException("This room is currently unavailable.");
        var requestedOccupancy = checked(request.AdultCount + request.ChildCount);
        if (requestedOccupancy > room.Capacity)
        {
            throw new BadRequestException(
                $"This room permits a maximum of {room.Capacity} guests. Select another room or reduce the occupancy.");
        }

        // 2. Conflict Check
        if (await _bookingRepo.IsRoomBookedAsync(room.Id, checkIn, checkOut))
            throw new BadRequestException("This room is already reserved for the selected dates.");

        // Anonymous reservations retain the public booking flow and identify an
        // existing guest only by an exact normalized e-mail and name match.
        guest ??= await _guestRepo.GetByEmailAndNameAsync(
            normalizedEmail,
            request.GuestFirstName.Trim(),
            request.GuestLastName.Trim());

        var newGuest = guest is null;
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
        }

        var nights = Math.Max(1, checkOutDate.DayNumber - checkInDate.DayNumber);
        var totalAmount = room.PricePerNight * nights;
        var bookingCode = await _bookingRepo.GenerateBookingCodeAsync();

        // 4. Build the booking. For Monnify, initialize against a reference
        // generated and owned by this server before persisting the booking.
        var guestAccessToken = BookingGuestAccess.GenerateToken();
        var guestAccessIssuedAtUtc = DateTime.UtcNow;
        var policiesAcceptedAtUtc = request.AcceptPrivacyPolicy && request.AcceptBookingTerms
            ? guestAccessIssuedAtUtc
            : (DateTime?)null;
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            BookingCode = bookingCode,
            RoomId = room.Id,
            GuestId = guest.Id,
            CheckIn = checkIn,
            CheckOut = checkOut,
            AdultCount = request.AdultCount,
            ChildCount = request.ChildCount,
            Status = BookingStatus.Pending,
            Amount = totalAmount,
            PaymentStatus = request.PaymentMethod == PaymentMethod.DirectTransfer ? PaymentStatus.AwaitingVerification : PaymentStatus.Unpaid,
            PaymentMethod = request.PaymentMethod.Value,
            Notes = request.Notes,
            StatusHistoryJson = "[]", // FIX: Initialised as empty JSON array to prevent Deserialization errors
            GuestAccessTokenHash = BookingGuestAccess.Hash(guestAccessToken),
            GuestAccessTokenIssuedAtUtc = guestAccessIssuedAtUtc,
            GuestAccessTokenExpiresAtUtc = guestAccessIssuedAtUtc.Add(
                BookingGuestAccessPolicy.InitialLinkLifetime),
            PrivacyPolicyVersion = policiesAcceptedAtUtc.HasValue
                ? request.PrivacyPolicyVersion
                : null,
            BookingTermsVersion = policiesAcceptedAtUtc.HasValue
                ? request.BookingTermsVersion
                : null,
            PoliciesAcceptedAtUtc = policiesAcceptedAtUtc,
            CreatedAt = guestAccessIssuedAtUtc
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

        var manageBookingUrl = BuildManageBookingUrl(booking.BookingCode, guestAccessToken);
        var emailMessages = new List<EmailOutboxMessage>
        {
            _emailOutbox.Create(
                TransactionalEmailTemplates.BookingConfirmation,
                guest.Email,
                new BookingConfirmationEmail(
                    $"{guest.FirstName} {guest.LastName}",
                    booking.BookingCode,
                    room.Name,
                    room.Category.ToString(),
                    room.Capacity,
                    booking.AdultCount,
                    booking.ChildCount,
                    booking.CheckIn,
                    booking.CheckOut,
                    nights,
                    totalAmount,
                    manageBookingUrl))
        };

        var adminEmail = _config["EmailSettings:AdminNotificationEmail"] ?? _config["EmailSettings:SenderEmail"];
        if (!string.IsNullOrWhiteSpace(adminEmail))
        {
            emailMessages.Add(_emailOutbox.Create(
                TransactionalEmailTemplates.AdminNewBooking,
                adminEmail,
                new AdminNewBookingEmail(
                    $"{guest.FirstName} {guest.LastName}",
                    booking.BookingCode,
                    room.Name,
                    room.Category.ToString(),
                    room.Capacity,
                    booking.AdultCount,
                    booking.ChildCount,
                    booking.CheckIn,
                    booking.CheckOut,
                    nights,
                    totalAmount,
                    guest.Email,
                    guest.Phone)));
        }

        // Guest PII, the room reservation, and its required emails commit as
        // one unit. A room conflict or database failure leaves no orphan guest.
        await _bookingRepo.AddAsync(
            booking,
            newGuest ? guest : null,
            emailMessages,
            emailVerification);

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

        string? paymentInstruction = (booking.PaymentMethod == PaymentMethod.DirectTransfer) ? GetTransferInstructions() : null;

        return MapToDto(booking, guest) with
        {
            PaymentUrl = paymentUrl,
            PaymentInstruction = paymentInstruction,
            GuestAccessToken = guestAccessToken
        };
    }

    public async Task<BookingDto?> GetBookingByCodeAsync(string code)
    {
        var b = await _bookingRepo.GetByCodeAsync(code);
        return b != null ? MapToDto(b) : null;
    }

    public async Task<BookingDto?> GetBookingByCodeAndEmailAsync(
        string code,
        string? email,
        string? guestAccessToken = null,
        Guid? accountUserId = null)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var b = await _bookingRepo.GetByCodeAsync(code.Trim().ToUpperInvariant());
        if (b is null) return null;

        if (await IsAccountOwnerAsync(b, accountUserId) ||
            BookingGuestAccessPolicy.IsValid(b, guestAccessToken, DateTime.UtcNow))
        {
            return MapToDto(b);
        }

        return null;
    }

    public async Task RequestBookingAccessLinkAsync(
        string code,
        string email,
        string requestId)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(email)) return;
        var booking = await _bookingRepo.GetByCodeAsync(code.Trim().ToUpperInvariant());
        if (booking?.Guest is null ||
            !booking.Guest.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase) ||
            booking.Status == BookingStatus.Cancelled)
        {
            return;
        }

        var issuedAtUtc = DateTime.UtcNow;
        if (booking.GuestAccessLinkLastRequestedAtUtc >
            issuedAtUtc.Subtract(BookingGuestAccessPolicy.ReplacementRequestCooldown))
        {
            return;
        }

        var token = BookingGuestAccess.GenerateToken();
        booking.GuestAccessTokenHash = BookingGuestAccess.Hash(token);
        booking.GuestAccessTokenIssuedAtUtc = issuedAtUtc;
        booking.GuestAccessTokenExpiresAtUtc = issuedAtUtc.Add(
            BookingGuestAccessPolicy.ReplacementLinkLifetime);
        booking.GuestAccessTokenRevokedAtUtc = null;
        booking.GuestAccessLinkLastRequestedAtUtc = issuedAtUtc;

        await _bookingRepo.UpdateAsync(booking);
        await _auditRepo.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ProfileId = BookingPaymentPolicy.SystemActorId,
            Action = "BOOKING_ACCESS_LINK_REQUESTED",
            EntityType = "Booking",
            EntityId = booking.Id.ToString(),
            NewDataJson = JsonSerializer.Serialize(new
            {
                booking.BookingCode,
                booking.GuestId,
                RequestId = requestId,
                RequestedAtUtc = issuedAtUtc,
                ExpiresAtUtc = booking.GuestAccessTokenExpiresAtUtc,
                ReplacedPreviousLink = true
            }),
            CreatedAt = issuedAtUtc
        });
        await _emailOutbox.EnqueueAsync(
            TransactionalEmailTemplates.BookingAccessLink,
            booking.Guest.Email,
            new BookingAccessLinkEmail(
                $"{booking.Guest.FirstName} {booking.Guest.LastName}",
                booking.BookingCode,
                BuildManageBookingUrl(booking.BookingCode, token)));
    }

    public async Task<IEnumerable<BookingDto>> GetAllBookingsAsync()
    {
        var bookings = await _bookingRepo.GetAllAsync();
        return bookings.Select(booking => MapToDto(booking));
    }

    public async Task<PagedResult<BookingDto>> GetPagedBookingsAsync(
        int pageNumber = 1,
        int pageSize = 20,
        BookingStatus? status = null,
        PaymentStatus? paymentStatus = null,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        var paged = await _bookingRepo.GetPagedBookingsAsync(
            pageNumber,
            pageSize,
            status,
            paymentStatus,
            search,
            cancellationToken);

        var mapped = paged.Items.Select(b => MapToDto(b)).ToList();
        return PagedResult<BookingDto>.Create(mapped, paged.TotalCount, paged.PageNumber, paged.PageSize);
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

        await _auditRepo.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ProfileId = userId,
            Action = "LIFECYCLE_TRANSITION",
            EntityType = "Booking",
            EntityId = booking.Id.ToString(),
            OldDataJson = JsonSerializer.Serialize(new { Status = oldStatus }),
            NewDataJson = JsonSerializer.Serialize(new { Status = status })
        });

        if (status == BookingStatus.CheckedOut)
        {
            await _emailOutbox.EnqueueAsync(
                TransactionalEmailTemplates.CheckOutThankYou,
                booking.Guest.Email,
                new CheckOutThankYouEmail(
                    booking.Guest.FirstName,
                    booking.BookingCode,
                    room?.Name ?? "Reserved Room"));
        }
        else if (status == BookingStatus.NoShow)
        {
            await _emailOutbox.EnqueueAsync(
                TransactionalEmailTemplates.Cancellation,
                booking.Guest.Email,
                new CancellationEmail(
                    $"{booking.Guest.FirstName} {booking.Guest.LastName}",
                    booking.BookingCode,
                    room?.Name ?? "Reserved Room",
                    room?.Category.ToString() ?? "Standard",
                    booking.CheckIn,
                    "The reservation was marked as a no-show after the scheduled check-in time."));
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
        booking.CancelledAtUtc = DateTime.UtcNow;
        booking.GuestAccessTokenRevokedAtUtc = booking.CancelledAtUtc;
        booking.PaymentCheckoutUrl = null;

        // --- TRIGGER REFUND LOGIC ---
        if (booking.PaymentStatus == PaymentStatus.Paid)
        {
            booking.PaymentStatus = PaymentStatus.RefundPending;
        }

        var history = JsonSerializer.Deserialize<List<object>>(booking.StatusHistoryJson ?? "[]") ?? new();
        history.Add(new
        {
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

        await _emailOutbox.EnqueueAsync(
            TransactionalEmailTemplates.Cancellation,
            booking.Guest!.Email,
            new CancellationEmail(
                $"{booking.Guest.FirstName} {booking.Guest.LastName}",
                booking.BookingCode,
                room?.Name ?? "Reserved Room",
                room?.Category.ToString() ?? "Standard",
                booking.CheckIn,
                reason));

        if (booking.PaymentStatus == PaymentStatus.RefundPending)
        {
            var adminEmail =
                _config["EmailSettings:AdminNotificationEmail"];
            if (!string.IsNullOrEmpty(adminEmail))
            {
                await _emailOutbox.EnqueueAsync(
                    TransactionalEmailTemplates.AdminRefund,
                    adminEmail,
                    new AdminRefundEmail(
                        $"{booking.Guest?.FirstName} {booking.Guest?.LastName}",
                        booking.BookingCode,
                        room?.Name ?? "Reserved Room",
                        booking.Amount));
            }
        }
        return MapToDto(booking);
    }

    public async Task<BookingDto> CancelBookingByGuestAsync(
        string bookingCode,
        string? email,
        string? guestAccessToken,
        Guid? accountUserId,
        string requestId,
        string? reason = null)
    {
        var booking = await _bookingRepo.GetByCodeAsync(bookingCode.Trim().ToUpperInvariant());
        if (booking?.Guest is null)
            throw new UnauthorizedAccessException("Verification failed.");

        var isOwner = await IsAccountOwnerAsync(booking, accountUserId);
        var validToken = BookingGuestAccessPolicy.IsValid(
            booking,
            guestAccessToken,
            DateTime.UtcNow);
        if (!isOwner && !validToken)
            throw new UnauthorizedAccessException("Verification failed.");

        if (booking.Status == BookingStatus.Cancelled) return MapToDto(booking);
        if (booking.Status is BookingStatus.CheckedIn or BookingStatus.CheckedOut or BookingStatus.NoShow ||
            DateTime.UtcNow >= booking.CheckIn)
        {
            throw new BadRequestException("A stay in progress or a completed stay cannot be cancelled online.");
        }

        var room = await _roomRepo.GetByIdAsync(booking.RoomId);
        var previousStatus = booking.Status;
        var previousPaymentStatus = booking.PaymentStatus;
        booking.Status = BookingStatus.Cancelled;
        booking.CancelledAtUtc = DateTime.UtcNow;
        booking.GuestAccessTokenRevokedAtUtc = booking.CancelledAtUtc;
        booking.PaymentCheckoutUrl = null;
        if (booking.PaymentStatus == PaymentStatus.Paid)
            booking.PaymentStatus = PaymentStatus.RefundPending;

        var history = JsonSerializer.Deserialize<List<object>>(booking.StatusHistoryJson ?? "[]") ?? new();
        history.Add(new
        {
            Status = BookingStatus.Cancelled,
            Timestamp = DateTime.UtcNow,
            Actor = isOwner ? "AuthenticatedGuest" : "SecureGuestLink",
            Reason = reason ?? "Self-service cancellation",
            PaymentShift = booking.PaymentStatus.ToString()
        });
        booking.StatusHistoryJson = JsonSerializer.Serialize(history);

        await _bookingRepo.UpdateAsync(booking);
        await _auditRepo.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ProfileId = accountUserId ?? BookingPaymentPolicy.SystemActorId,
            Action = "GUEST_BOOKING_CANCELLED",
            EntityType = "Booking",
            EntityId = booking.Id.ToString(),
            OldDataJson = JsonSerializer.Serialize(new
            {
                booking.BookingCode,
                Status = previousStatus.ToString(),
                PaymentStatus = previousPaymentStatus.ToString()
            }),
            NewDataJson = JsonSerializer.Serialize(new
            {
                booking.BookingCode,
                booking.GuestId,
                Status = booking.Status.ToString(),
                PaymentStatus = booking.PaymentStatus.ToString(),
                Reason = reason,
                RequestId = requestId,
                TimestampUtc = DateTime.UtcNow
            }),
            CreatedAt = DateTime.UtcNow
        });
        await _emailOutbox.EnqueueAsync(
            TransactionalEmailTemplates.Cancellation,
            booking.Guest.Email,
            new CancellationEmail(
                $"{booking.Guest.FirstName} {booking.Guest.LastName}",
                booking.BookingCode,
                room?.Name ?? "Reserved Room",
                room?.Category.ToString() ?? "Standard",
                booking.CheckIn,
                reason));

        if (booking.PaymentStatus == PaymentStatus.RefundPending)
        {
            var adminEmail = _config["EmailSettings:AdminNotificationEmail"];
            if (!string.IsNullOrWhiteSpace(adminEmail))
            {
                await _emailOutbox.EnqueueAsync(
                    TransactionalEmailTemplates.AdminRefund,
                    adminEmail,
                    new AdminRefundEmail(
                        $"{booking.Guest.FirstName} {booking.Guest.LastName}",
                        booking.BookingCode,
                        room?.Name ?? "Reserved Room",
                        booking.Amount));
            }
        }

        return MapToDto(booking);
    }

    public async Task<BookingDto> ApproveRefundAsync(
        Guid bookingId,
        ApproveRefundRequest request,
        Guid approvingUserId)
    {
        var booking = await _bookingRepo.GetByIdAsync(bookingId);
        if (booking == null) throw new NotFoundException("Booking not found.");
        if (booking.PaymentStatus != PaymentStatus.RefundPending)
            throw new BadRequestException("This booking is not flagged for a manual refund.");

        var threshold = _config.GetValue(
            "FinancialControls:HighValueRefundThreshold",
            500000m);
        if (booking.Amount < threshold)
            throw new BadRequestException("This refund is below the dual-approval threshold.");

        var approvingUser = await GetActiveRefundOperatorAsync(approvingUserId);
        if (booking.RefundApprovedByUserId.HasValue)
        {
            if (booking.RefundApprovedByUserId == approvingUserId) return MapToDto(booking);
            throw new BadRequestException("This refund already has its first approval.");
        }

        var approvedAtUtc = DateTime.UtcNow;
        booking.RefundApprovedByUserId = approvingUserId;
        booking.RefundApprovedAtUtc = approvedAtUtc;
        var history = JsonSerializer.Deserialize<List<object>>(booking.StatusHistoryJson ?? "[]") ?? [];
        history.Add(new
        {
            Action = "HIGH_VALUE_REFUND_APPROVED",
            Timestamp = approvedAtUtc,
            ApproverId = approvingUserId,
            ApproverRole = approvingUser.Role.ToString()
        });
        booking.StatusHistoryJson = JsonSerializer.Serialize(history);

        await _bookingRepo.UpdateAsync(booking);
        await _auditRepo.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ProfileId = approvingUserId,
            Action = "HIGH_VALUE_REFUND_APPROVED",
            EntityType = "Booking",
            EntityId = booking.Id.ToString(),
            NewDataJson = JsonSerializer.Serialize(new
            {
                booking.BookingCode,
                booking.Amount,
                Threshold = threshold,
                Reason = request.Reason.Trim(),
                ApprovedAtUtc = approvedAtUtc
            }),
            CreatedAt = approvedAtUtc
        });
        return MapToDto(booking);
    }

    public async Task<BookingDto> CompleteRefundAsync(
        Guid bookingId,
        CompleteRefundRequest request,
        Guid processingUserId)
    {
        var transactionRef = request.TransactionReference.Trim().ToUpperInvariant();
        if (transactionRef.Length is < 4 or > 160)
            throw new BadRequestException("A valid external refund reference is required.");
        var booking = await _bookingRepo.GetByIdAsync(bookingId);
        if (booking == null) throw new NotFoundException("Booking not found.");
        if (booking.PaymentStatus == PaymentStatus.Refunded &&
            string.Equals(booking.RefundReference, transactionRef, StringComparison.Ordinal) &&
            booking.RefundAmount == request.Amount &&
            string.Equals(booking.RefundChannel, request.Channel, StringComparison.Ordinal) &&
            string.Equals(booking.RefundEvidenceType, request.EvidenceType, StringComparison.Ordinal))
        {
            return MapToDto(booking);
        }
        if (booking.PaymentStatus != PaymentStatus.RefundPending)
            throw new BadRequestException("This booking is not flagged for a manual refund.");
        if (request.Amount != booking.Amount)
            throw new BadRequestException("The recorded refund amount must equal the paid booking amount.");
        var validEvidenceForChannel = request.Channel switch
        {
            "BankTransfer" => request.EvidenceType == "BankStatement",
            "Monnify" => request.EvidenceType == "ProviderReceipt",
            "Cash" => request.EvidenceType == "CashVoucher",
            _ => false
        };
        if (!validEvidenceForChannel)
            throw new BadRequestException("The evidence type does not match the refund channel.");

        var processingUser = await GetActiveRefundOperatorAsync(processingUserId);
        var threshold = _config.GetValue(
            "FinancialControls:HighValueRefundThreshold",
            500000m);
        if (booking.Amount >= threshold)
        {
            if (!booking.RefundApprovedByUserId.HasValue ||
                !booking.RefundApprovedAtUtc.HasValue)
            {
                throw new BadRequestException(
                    "This high-value refund requires approval from a different administrator or manager.");
            }
            if (booking.RefundApprovedByUserId == processingUserId)
            {
                throw new BadRequestException(
                    "The person who approved a high-value refund cannot also complete it.");
            }
        }

        var processedAtUtc = DateTime.UtcNow;
        booking.PaymentStatus = PaymentStatus.Refunded;
        booking.RefundReference = transactionRef;
        booking.RefundAmount = request.Amount;
        booking.RefundChannel = request.Channel.Trim();
        booking.RefundEvidenceType = request.EvidenceType.Trim();
        booking.RefundNotes = request.Notes?.Trim();
        booking.RefundProcessedByUserId = processingUserId;
        booking.RefundProcessedAtUtc = processedAtUtc;

        var history = JsonSerializer.Deserialize<List<object>>(booking.StatusHistoryJson ?? "[]") ?? [];
        history.Add(new
        {
            Action = "MANUAL_REFUND_COMPLETED",
            Timestamp = processedAtUtc,
            Reference = transactionRef,
            Amount = request.Amount,
            Channel = booking.RefundChannel,
            EvidenceType = booking.RefundEvidenceType,
            ProcessorId = processingUserId,
            ProcessorRole = processingUser.Role.ToString()
        });
        booking.StatusHistoryJson = JsonSerializer.Serialize(history);

        await _bookingRepo.UpdateAsync(booking);
        await _auditRepo.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ProfileId = processingUserId,
            Action = "REFUND_COMPLETED",
            EntityType = "Booking",
            EntityId = booking.Id.ToString(),
            NewDataJson = JsonSerializer.Serialize(new
            {
                booking.BookingCode,
                PaymentStatus = booking.PaymentStatus.ToString(),
                RefundReference = booking.RefundReference,
                RefundAmount = booking.RefundAmount,
                RefundChannel = booking.RefundChannel,
                RefundEvidenceType = booking.RefundEvidenceType,
                RefundApprovedByUserId = booking.RefundApprovedByUserId,
                RefundProcessedByUserId = processingUserId,
                RefundProcessedAtUtc = processedAtUtc
            }),
            CreatedAt = processedAtUtc
        });
        var room = await _roomRepo.GetByIdAsync(booking.RoomId);
        await _emailOutbox.EnqueueAsync(
            TransactionalEmailTemplates.RefundCompleted,
            booking.Guest!.Email,
            new RefundCompletedEmail(
                booking.Guest.FirstName,
                booking.BookingCode,
                room?.Name ?? "Reserved Room",
                booking.Amount,
                transactionRef));

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
            PaymentExpiresAtUtc: paymentExpiresAtUtc,
            RefundAmount: b.RefundAmount,
            RefundChannel: b.RefundChannel,
            RefundEvidenceType: b.RefundEvidenceType,
            RefundApprovedByUserId: b.RefundApprovedByUserId,
            RefundApprovedAtUtc: b.RefundApprovedAtUtc,
            RefundProcessedByUserId: b.RefundProcessedByUserId,
            RefundProcessedAtUtc: b.RefundProcessedAtUtc,
            GuestAccessExpiresAtUtc: b.GuestAccessTokenExpiresAtUtc,
            AdultCount: b.AdultCount,
            ChildCount: b.ChildCount,
            PrivacyPolicyVersion: b.PrivacyPolicyVersion,
            BookingTermsVersion: b.BookingTermsVersion,
            PoliciesAcceptedAtUtc: b.PoliciesAcceptedAtUtc);
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

    private async Task<bool> IsAccountOwnerAsync(Booking booking, Guid? accountUserId)
    {
        if (!accountUserId.HasValue) return false;
        var account = await _userManager.FindByIdAsync(accountUserId.Value.ToString());
        return account?.Role == UserRole.Client &&
               !string.IsNullOrWhiteSpace(account.GuestId) &&
               string.Equals(account.GuestId, booking.GuestId, StringComparison.Ordinal);
    }

    private async Task<ApplicationUser> GetActiveRefundOperatorAsync(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null ||
            user.Status != ProfileStatus.Active ||
            user.Role is not (UserRole.Admin or UserRole.Manager))
        {
            throw new UnauthorizedAccessException(
                "Only an active administrator or manager can process refunds.");
        }

        return user;
    }

    private string BuildManageBookingUrl(string bookingCode, string guestAccessToken)
    {
        var publicAppUrl = _config["PublicAppUrl"]
            ?? throw new InvalidOperationException("PublicAppUrl is not configured.");
        var bookingUrl = QueryHelpers.AddQueryString(
            $"{publicAppUrl.TrimEnd('/')}/booking-status",
            "code",
            bookingCode);
        // URL fragments are never sent to the web host, CDN, reverse proxy or
        // API access logs. The guest site reads the token in-browser and sends
        // it to the API through a dedicated header.
        return $"{bookingUrl}#accessToken={Uri.EscapeDataString(guestAccessToken)}";
    }
}
