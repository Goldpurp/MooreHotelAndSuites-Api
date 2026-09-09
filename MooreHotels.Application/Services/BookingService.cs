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
using MooreHotels.Application.DTOs.Pricing;

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
    private readonly IPricingService _pricingService;
    private readonly IFolioService _folioService;
    private readonly IHousekeepingService? _housekeepingService;


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
        ILogger<BookingService> logger,
        IPricingService pricingService,
        IFolioService folioService,
        IHousekeepingService? housekeepingService = null)

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
        _pricingService = pricingService;
        _folioService = folioService;
        _housekeepingService = housekeepingService;
    }

    public async Task RequestBookingEmailVerificationAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = RequireEmail(email);
        var token = BookingGuestAccess.GenerateToken();
        var now = DateTime.UtcNow;
        var publicAppUrl = _config["PublicAppUrl"]
            ?? throw new InvalidOperationException("PublicAppUrl is not configured.");
        var verificationLink = FrontendLinkBuilder.WithFragment(
            publicAppUrl,
            "/book",
            [
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
        var normalizedEmail = RequireEmail(request.GuestEmail);
        var firstName = RequireText(request.GuestFirstName, "Guest first name", 80);
        var lastName = RequireText(request.GuestLastName, "Guest last name", 80);
        var phone = RequirePhone(request.GuestPhone);
        var notes = NormalizeOptionalText(request.Notes, "Booking notes", 1000);
        ValidateOptionalToken(request.EmailVerificationToken, "Email verification token");
        ValidateOptionalToken(request.QuoteToken, "Quote token");
        ValidateOptionalText(request.PrivacyPolicyVersion, "Privacy policy version", 80);
        ValidateOptionalText(request.BookingTermsVersion, "Booking terms version", 80);
        if (request.AdultCount is < 1 or > 20 || request.ChildCount is < 0 or > 20)
            throw new BadRequestException("A booking requires 1-20 adults and 0-20 children.");
        if (!request.PaymentMethod.HasValue) throw new BadRequestException("A payment method is required.");
        if (!Enum.IsDefined(request.PaymentMethod.Value))
            throw new BadRequestException("The payment method is invalid.");
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

        // Authenticated client reservations use the guest identity linked to
        // the signed-in account and do not require a second email challenge.
        Guest? guest = null;
        var linkedClientBooking = false;
        if (accountUserId.HasValue)
        {
            var account = await _userManager.FindByIdAsync(accountUserId.Value.ToString());
            if (account?.Role == UserRole.Client)
            {
                if (account.Status != ProfileStatus.Active || !account.EmailConfirmed)
                    throw new UnauthorizedAccessException("The client account is not active and verified.");
                if (string.IsNullOrWhiteSpace(account.GuestId))
                    throw new BadRequestException("This client account requires guest-profile reconciliation before booking.");

                guest = await _guestRepo.GetByIdAsync(account.GuestId);
                if (guest is null)
                    throw new BadRequestException("This client account requires guest-profile reconciliation before booking.");
                if (!string.Equals(
                        normalizedEmail,
                        guest.NormalizedEmail,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new BadRequestException(
                        "The booking email must match the verified email on the client account.");
                }
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

        if (request.RoomId.HasValue == request.RoomTypeId.HasValue)
            throw new BadRequestException(
                "Select exactly one inventory scope: a room type or a legacy physical room.");
        if (request.RoomQuantity is < 1 or > 10)
            throw new BadRequestException("Room quantity must be between 1 and 10.");
        if (request.RoomId == Guid.Empty || request.RoomTypeId == Guid.Empty || request.QuoteId == Guid.Empty)
            throw new BadRequestException("A supplied booking identifier is invalid.");

        Room? room = null;
        RoomType? roomType;
        if (request.RoomId.HasValue)
        {
            if (request.RoomQuantity != 1)
                throw new BadRequestException("A legacy physical-room booking can contain only one room.");
            room = await _roomRepo.GetByIdAsync(request.RoomId.Value)
                   ?? throw new NotFoundException("Room not found.");
            roomType = room.RoomType ?? await _roomRepo.GetRoomTypeByIdAsync(room.RoomTypeId);
        }
        else
        {
            roomType = await _roomRepo.GetRoomTypeByIdAsync(request.RoomTypeId!.Value);
        }
        if (roomType is null || !roomType.IsActive)
            throw new NotFoundException("Room type not found or inactive.");

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
        if (room is not null && (!room.IsOnline || room.Status is RoomStatus.Maintenance or RoomStatus.OutOfOrder))
            throw new BadRequestException("This room is currently unavailable.");
        var requestedOccupancy = checked(request.AdultCount + request.ChildCount);
        var maximumOccupancy = checked(roomType.MaxOccupancy * request.RoomQuantity);
        if (requestedOccupancy > maximumOccupancy)
        {
            throw new BadRequestException(
                $"The selected inventory permits a maximum of {maximumOccupancy} guests. Select more rooms or reduce the occupancy.");
        }

        // 2. Conflict Check
        if (room is not null && await _bookingRepo.IsRoomBookedAsync(room.Id, checkIn, checkOut))
            throw new BadRequestException("This room is already reserved for the selected dates.");

        ValidatedBookingQuote? quote = null;
        var quoteWasSupplied = request.QuoteId.HasValue ||
                               !string.IsNullOrWhiteSpace(request.QuoteToken);
        if (quoteWasSupplied &&
            (!request.QuoteId.HasValue || string.IsNullOrWhiteSpace(request.QuoteToken)))
            throw new BadRequestException("quoteId and quoteToken must be supplied together.");
        if (quoteWasSupplied)
        {
            quote = await _pricingService.ValidateBookingQuoteAsync(
                request.QuoteId!.Value,
                request.QuoteToken!,
                request);
        }
        else if (_config.GetValue<bool>("Pricing:RequireQuoteForBooking"))
        {
            throw new BadRequestException(
                "Request a current pricing quote before creating this booking.");
        }
        if (request.PaymentMethod == PaymentMethod.Monnify && quote is not null &&
            !string.Equals(quote.Currency, "NGN", StringComparison.Ordinal))
        {
            throw new BadRequestException(
                "Monnify checkout is available only for NGN pricing quotes.");
        }

        // Anonymous reservations retain the public booking flow and identify an
        // existing guest only by an exact normalized e-mail and name match.
        guest ??= await _guestRepo.GetByEmailAndNameAsync(
            normalizedEmail,
            firstName,
            lastName);

        var newGuest = guest is null;
        if (guest == null)
        {
            guest = new Guest
            {
                Id = $"GS-{Guid.NewGuid():N}"[..19].ToUpperInvariant(),
                Email = normalizedEmail,
                NormalizedEmail = normalizedEmail,
                FirstName = firstName,
                LastName = lastName,
                Phone = phone,
                NormalizedPhone = NormalizePhone(phone),
                EmailVerifiedAtUtc = emailVerification?.VerifiedAtUtc
            };
        }
        else if (emailVerification is not null && !guest.EmailVerifiedAtUtc.HasValue)
        {
            guest.EmailVerifiedAtUtc = emailVerification.VerifiedAtUtc;
            await _guestRepo.UpdateAsync(guest);
        }

        var nights = Math.Max(1, checkOutDate.DayNumber - checkInDate.DayNumber);
        var legacyRoomSubtotal = (room?.PricePerNight ?? roomType.BasePricePerNight) *
                                 nights * request.RoomQuantity;
        var totalAmount = quote?.TotalAmount ?? legacyRoomSubtotal;
        if (totalAmount <= 0 || totalAmount > 9999999999999999m)
            throw new BadRequestException("The booking total is outside the supported payment range.");
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
            RoomId = room?.Id,
            RoomTypeId = roomType.Id,
            RoomQuantity = request.RoomQuantity,
            GuestId = guest.Id,
            CheckIn = checkIn,
            CheckOut = checkOut,
            AdultCount = request.AdultCount,
            ChildCount = request.ChildCount,
            Status = BookingStatus.Pending,
            QuoteId = quote?.QuoteId,
            Currency = quote?.Currency ??
                       _config["Pricing:DefaultCurrency"] ??
                       "NGN",
            RoomSubtotal = quote?.RoomSubtotal ?? legacyRoomSubtotal,
            DiscountAmount = quote?.DiscountAmount ?? 0m,
            IncludedTaxAmount = quote?.IncludedTaxAmount ?? 0m,
            TaxAmount = quote?.TaxAmount ?? 0m,
            FeeAmount = quote?.FeeAmount ?? 0m,
            Amount = totalAmount,
            PaymentStatus = request.PaymentMethod == PaymentMethod.DirectTransfer ? PaymentStatus.AwaitingVerification : PaymentStatus.Unpaid,
            PaymentMethod = request.PaymentMethod.Value,
            Notes = notes,
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
            ReservationPolicyVersion = _config["ReservationPolicies:Version"] ?? "2026-09",
            FreeCancellationHours = Math.Clamp(
                _config.GetValue("ReservationPolicies:FreeCancellationHours", 24), 0, 720),
            CancellationPenaltyPercent = Math.Clamp(
                _config.GetValue("ReservationPolicies:CancellationPenaltyPercent", 50m), 0m, 100m),
            DepositPercent = Math.Clamp(
                _config.GetValue("ReservationPolicies:DepositPercent", 30m), 0m, 100m),
            NoShowPenaltyPercent = Math.Clamp(
                _config.GetValue("ReservationPolicies:NoShowPenaltyPercent", 100m), 0m, 100m),
            CreatedAt = guestAccessIssuedAtUtc
        };
        for (var sequence = 1; sequence <= booking.RoomQuantity; sequence++)
        {
            booking.ReservationRooms.Add(new ReservationRoom
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                RoomTypeId = roomType.Id,
                RoomTypeCode = roomType.Code,
                RoomTypeName = roomType.Name,
                AssignedRoomId = sequence == 1 ? room?.Id : null,
                Sequence = sequence,
                AssignedAtUtc = sequence == 1 && room is not null ? guestAccessIssuedAtUtc : null,
                CreatedAtUtc = guestAccessIssuedAtUtc
            });
        }
        booking.Folio = FolioAccounting.CreateInitial(booking, guestAccessIssuedAtUtc);

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
                    roomType.Name,
                    roomType.Category.ToString(),
                    maximumOccupancy,
                    booking.AdultCount,
                    booking.ChildCount,
                    booking.CheckIn,
                    booking.CheckOut,
                    nights,
                    totalAmount,
                    manageBookingUrl),
                dataSubjectGuestId: guest.Id)
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
                    roomType.Name,
                    roomType.Category.ToString(),
                    maximumOccupancy,
                    booking.AdultCount,
                    booking.ChildCount,
                    booking.CheckIn,
                    booking.CheckOut,
                    nights,
                    totalAmount,
                    guest.Email,
                    guest.Phone),
                dataSubjectGuestId: guest.Id));
        }

        // Guest PII, the room reservation, and its required emails commit as
        // one unit. A room conflict or database failure leaves no orphan guest.
        await _bookingRepo.AddAsync(
            booking,
            newGuest ? guest : null,
            emailMessages,
            emailVerification,
            quote);

        // 6. Notifications & Admin Alerts. Scoped services are awaited so work
        // cannot be lost when the request scope is disposed.
        try
        {
            await _notificationService.NotifyNewBookingAsync(
                booking,
                $"{guest.FirstName} {guest.LastName}",
                roomType.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "New-booking notification failed for {Code} with {ExceptionType}.",
                booking.BookingCode,
                ex.GetType().Name);
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
        var normalizedCode = NormalizeBookingCode(code);
        var b = await _bookingRepo.GetByCodeAsync(normalizedCode);
        return b != null ? MapToDto(b) : null;
    }

    public async Task<BookingDto?> GetBookingWithAccessAsync(
        string code,
        string? guestAccessToken = null,
        Guid? accountUserId = null)
    {
        if (!TryNormalizeBookingCode(code, out var normalizedCode)) return null;
        var b = await _bookingRepo.GetByCodeAsync(normalizedCode);
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
        if (!TryNormalizeBookingCode(code, out var normalizedCode) ||
            !TryNormalizeEmail(email, out var normalizedEmail) ||
            IsInvalidAuditValue(requestId, 160)) return;
        var booking = await _bookingRepo.GetByCodeAsync(normalizedCode);
        if (booking?.Guest is null ||
            !booking.Guest.Email.Equals(normalizedEmail, StringComparison.OrdinalIgnoreCase) ||
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
                BuildManageBookingUrl(booking.BookingCode, token)),
            dataSubjectGuestId: booking.GuestId);
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
        if (status.HasValue && !Enum.IsDefined(status.Value) ||
            paymentStatus.HasValue && !Enum.IsDefined(paymentStatus.Value) ||
            IsInvalidAuditValue(search, 120))
        {
            throw new BadRequestException("Booking filters are invalid or too long.");
        }
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
        if (bookingId == Guid.Empty) throw new NotFoundException("Booking not found.");
        if (!Enum.IsDefined(status)) throw new BadRequestException("Booking status is invalid.");
        var actingUser = await GetActiveReservationOperatorAsync(userId);
        var booking = await _bookingRepo.GetByIdAsync(bookingId);
        if (booking == null) throw new NotFoundException("Booking not found.");

        // FIX: Ensure Guest navigation property is loaded to prevent NullRef in Emails
        if (booking.Guest == null) throw new InvalidOperationException("Booking guest data could not be loaded.");

        var assignedRooms = booking.ReservationRooms
            .Where(item => item.AssignedRoom is not null)
            .OrderBy(item => item.Sequence)
            .Select(item => item.AssignedRoom!)
            .ToArray();
        var room = assignedRooms.FirstOrDefault() ?? booking.Room;
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
            if (assignedRooms.Length != booking.RoomQuantity)
                throw new BadRequestException("Assign every reserved room before check-in.");
            if (assignedRooms.Any(assignedRoom =>
                    !assignedRoom.IsOnline || assignedRoom.Status != RoomStatus.Available))
                throw new BadRequestException(
                    "Every assigned room must be online, clean, and available before check-in.");
            foreach (var assignedRoom in assignedRooms)
            {
                assignedRoom.Status = RoomStatus.Occupied;
                await _roomRepo.UpdateAsync(assignedRoom);
            }
            await _visitService.CreateRecordAsync(booking.BookingCode, "CHECK_IN", actingUser?.Name ?? "Admin");
        }
        else if (status == BookingStatus.CheckedOut)
        {
            if (booking.Status != BookingStatus.CheckedIn)
                throw new BadRequestException("Only a checked-in booking can be checked out.");
            if (booking.Folio is null)
                throw new InvalidOperationException("The booking folio is missing.");
            var balance = FolioAccounting.Calculate(booking.Folio.Entries);
            if (balance.Balance != 0)
                throw new BadRequestException(
                    "Settle the outstanding folio balance or guest credit before checkout.");
            foreach (var assignedRoom in assignedRooms)
            {
                assignedRoom.Status = RoomStatus.Dirty;
                await _roomRepo.UpdateAsync(assignedRoom);
            }
            if (_housekeepingService is not null)
                await _housekeepingService.CreateCheckoutTasksAsync(booking, userId);
            booking.Folio.Status = FolioStatus.Closed;
            booking.Folio.ClosedAtUtc = DateTime.UtcNow;
            booking.Folio.ClosedByUserId = userId;
            await _visitService.CreateRecordAsync(booking.BookingCode, "CHECK_OUT", actingUser?.Name ?? "Admin");
        }
        else if (status == BookingStatus.NoShow)
        {
            if (booking.Status == BookingStatus.CheckedIn || booking.Status == BookingStatus.CheckedOut)
                throw new BadRequestException("An active or completed stay cannot be marked as a no-show.");
            if (DateTime.UtcNow < booking.CheckIn)
                throw new BadRequestException("A booking cannot be marked as a no-show before check-in time.");

            foreach (var assignedRoom in assignedRooms)
            {
                assignedRoom.Status = RoomStatus.Available;
                await _roomRepo.UpdateAsync(assignedRoom);
            }
            await _folioService.ApplyNoShowPolicyAsync(
                booking, "Reservation marked as a no-show.", userId);
        }


        // FIX: Robust JSON handling logic
        var history = ReadStatusHistory(booking.StatusHistoryJson);

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
                    room?.Name ?? "Reserved Room"),
                dataSubjectGuestId: booking.GuestId);
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
                    "The reservation was marked as a no-show after the scheduled check-in time."),
                dataSubjectGuestId: booking.GuestId);
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
        var normalizedCode = NormalizeBookingCode(bookingCode);
        if (IsInvalidAuditValue(requestId, 160))
            throw new BadRequestException("The request identifier is invalid.");
        if (!string.Equals(
                request.ConfirmationText,
                ManualTransferConfirmation.RequiredText,
                StringComparison.Ordinal))
        {
            throw new BadRequestException(
                "confirmationText must be exactly 'ACCEPT' (case-sensitive, with no leading or trailing whitespace).");
        }

        var actingUser = await _userManager.FindByIdAsync(actingUserId.ToString());
        if (actingUser is null || actingUser.Status != ProfileStatus.Active ||
            actingUser.Role is not (UserRole.Admin or UserRole.Manager))
        {
            throw new UnauthorizedAccessException(
                "Only an authenticated Admin or Manager can confirm a bank transfer.");
        }

        return await _bookingRepo.ConfirmManualTransferAsync(
            normalizedCode,
            new ManualTransferConfirmationActor(
                actingUser.Id,
                actingUser.Name,
                actingUser.Role,
                requestId),
            cancellationToken);
    }

    public async Task<BookingDto> CancelBookingAsync(Guid bookingId, Guid userId, string? reason = null)
    {
        if (bookingId == Guid.Empty) throw new NotFoundException("Booking record not found.");
        var normalizedReason = NormalizeOptionalText(reason, "Cancellation reason", 500);
        var actingUser = await GetActiveReservationOperatorAsync(userId);
        var booking = await _bookingRepo.GetByIdAsync(bookingId);
        if (booking == null) throw new NotFoundException("Booking record not found.");
        if (booking.Guest is null)
            throw new InvalidOperationException("Booking guest data could not be loaded.");

        if (booking.Status == BookingStatus.Cancelled) return MapToDto(booking);

        if (booking.Status is BookingStatus.CheckedIn or BookingStatus.CheckedOut or BookingStatus.NoShow)
            throw new BadRequestException("Active or completed stays cannot be cancelled.");

        var oldStatus = booking.Status;
        var room = booking.Room;

        booking.Status = BookingStatus.Cancelled;
        booking.CancelledAtUtc = DateTime.UtcNow;
        booking.GuestAccessTokenRevokedAtUtc = booking.CancelledAtUtc;
        booking.PaymentCheckoutUrl = null;

        await _folioService.ApplyCancellationCreditAsync(
            booking, normalizedReason ?? "Cancelled by staff", actingUser.Id);

        var history = ReadStatusHistory(booking.StatusHistoryJson);
        history.Add(new
        {
            Status = BookingStatus.Cancelled,
            Timestamp = DateTime.UtcNow,
            Actor = actingUser?.UserName ?? "Staff",
            Reason = normalizedReason ?? "Cancelled by staff",
            PaymentShift = booking.PaymentStatus.ToString() // Will show 'RefundPending' if it was 'Paid'
        });
        booking.StatusHistoryJson = JsonSerializer.Serialize(history);

        var refundableAmount = booking.Folio is null
            ? 0m
            : FolioAccounting.Calculate(booking.Folio.Entries).GuestCredit;
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
                Reason = normalizedReason
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
                normalizedReason),
            dataSubjectGuestId: booking.GuestId);

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
                        refundableAmount),
                    dataSubjectGuestId: booking.GuestId);
            }
        }
        return MapToDto(booking);
    }

    public async Task<BookingDto> CancelBookingByGuestAsync(
        string bookingCode,
        string? guestAccessToken,
        Guid? accountUserId,
        string requestId,
        string? reason = null)
    {
        var normalizedCode = NormalizeBookingCode(bookingCode);
        var normalizedReason = NormalizeOptionalText(reason, "Cancellation reason", 500);
        if (IsInvalidAuditValue(requestId, 160))
            throw new BadRequestException("The request identifier is invalid.");
        ValidateOptionalToken(guestAccessToken, "Guest access token");
        if (accountUserId == Guid.Empty)
            throw new UnauthorizedAccessException("Verification failed.");
        var booking = await _bookingRepo.GetByCodeAsync(normalizedCode);
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

        var room = booking.Room;
        var previousStatus = booking.Status;
        var previousPaymentStatus = booking.PaymentStatus;
        booking.Status = BookingStatus.Cancelled;
        booking.CancelledAtUtc = DateTime.UtcNow;
        booking.GuestAccessTokenRevokedAtUtc = booking.CancelledAtUtc;
        booking.PaymentCheckoutUrl = null;
        var cancellationActor = accountUserId ?? BookingPaymentPolicy.SystemActorId;
        await _folioService.ApplyCancellationCreditAsync(
            booking, normalizedReason ?? "Self-service cancellation", cancellationActor);
        var history = ReadStatusHistory(booking.StatusHistoryJson);
        history.Add(new
        {
            Status = BookingStatus.Cancelled,
            Timestamp = DateTime.UtcNow,
            Actor = isOwner ? "AuthenticatedGuest" : "SecureGuestLink",
            Reason = normalizedReason ?? "Self-service cancellation",
            PaymentShift = booking.PaymentStatus.ToString()
        });
        booking.StatusHistoryJson = JsonSerializer.Serialize(history);

        var refundableAmount = booking.Folio is null
            ? 0m
            : FolioAccounting.Calculate(booking.Folio.Entries).GuestCredit;
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
                Reason = normalizedReason,
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
                normalizedReason),
            dataSubjectGuestId: booking.GuestId);

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
                        refundableAmount),
                    dataSubjectGuestId: booking.GuestId);
            }
        }

        return MapToDto(booking);
    }

    public async Task<BookingDto> ApproveRefundAsync(
        Guid bookingId,
        ApproveRefundRequest request,
        Guid approvingUserId)
    {
        if (bookingId == Guid.Empty) throw new NotFoundException("Booking not found.");
        var reason = RequireText(request.Reason, "Refund approval reason", 500, minimumLength: 10);
        if (request.Amount is <= 0 or > 9999999999999999m)
            throw new BadRequestException("The refund approval amount is invalid.");
        var approvingUser = await GetActiveRefundOperatorAsync(approvingUserId);
        var booking = await _bookingRepo.GetByIdAsync(bookingId);
        if (booking == null) throw new NotFoundException("Booking not found.");
        if (booking.PaymentStatus != PaymentStatus.RefundPending)
            throw new BadRequestException("This booking is not flagged for a manual refund.");

        var guestCredit = booking.Folio is null
            ? 0m
            : FolioAccounting.Calculate(booking.Folio.Entries).GuestCredit;
        var approvedAmount = request.Amount ?? guestCredit;
        if (approvedAmount <= 0 || approvedAmount > guestCredit)
            throw new BadRequestException("The approved amount exceeds the unsettled guest credit.");

        var threshold = _config.GetValue(
            "FinancialControls:HighValueRefundThreshold",
            500000m);
        if (approvedAmount < threshold)
            throw new BadRequestException("This refund is below the dual-approval threshold.");

        if (booking.RefundApprovedByUserId.HasValue)
        {
            var priorApprovalWasConsumed = booking.RefundProcessedAtUtc.HasValue &&
                                           booking.RefundApprovedAtUtc.HasValue &&
                                           booking.RefundProcessedAtUtc >= booking.RefundApprovedAtUtc;
            if (!priorApprovalWasConsumed)
            {
                if (booking.RefundApprovedByUserId == approvingUserId &&
                    booking.RefundApprovedAmount == approvedAmount) return MapToDto(booking);
                throw new BadRequestException("This refund already has its first approval.");
            }
        }

        var approvedAtUtc = DateTime.UtcNow;
        booking.RefundApprovedByUserId = approvingUserId;
        booking.RefundApprovedAtUtc = approvedAtUtc;
        booking.RefundApprovedAmount = approvedAmount;
        var history = ReadStatusHistory(booking.StatusHistoryJson);
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
                ApprovedAmount = approvedAmount,
                Threshold = threshold,
                Reason = reason,
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
        if (bookingId == Guid.Empty) throw new NotFoundException("Booking not found.");
        var transactionRef = request.TransactionReference?.Trim().ToUpperInvariant() ?? string.Empty;
        if (transactionRef.Length is < 4 or > 160 || transactionRef.Any(char.IsControl))
            throw new BadRequestException("A valid external refund reference is required.");
        if (request.Amount is <= 0 or > 9999999999999999m)
            throw new BadRequestException("The refund amount is invalid.");
        if (request.Channel is not ("BankTransfer" or "Cash" or "Monnify") ||
            request.EvidenceType is not ("BankStatement" or "ProviderReceipt" or "CashVoucher"))
            throw new BadRequestException("The refund channel or evidence type is invalid.");
        var notes = NormalizeOptionalText(request.Notes, "Refund notes", 500);
        var processingUser = await GetActiveRefundOperatorAsync(processingUserId);
        var booking = await _bookingRepo.GetByIdAsync(bookingId);
        if (booking == null) throw new NotFoundException("Booking not found.");
        if (booking.Folio?.Entries.Any(entry =>
                entry.Type == FolioEntryType.Refund &&
                entry.ExternalReference == transactionRef &&
                entry.Amount == FolioAccounting.Money(request.Amount)) == true)
        {
            return MapToDto(booking);
        }
        if (booking.PaymentStatus != PaymentStatus.RefundPending)
            throw new BadRequestException("This booking is not flagged for a manual refund.");
        var guestCredit = booking.Folio is null
            ? 0m
            : FolioAccounting.Calculate(booking.Folio.Entries).GuestCredit;
        if (request.Amount <= 0 || request.Amount > guestCredit)
            throw new BadRequestException("The refund amount exceeds the unsettled guest credit.");
        var validEvidenceForChannel = request.Channel switch
        {
            "BankTransfer" => request.EvidenceType == "BankStatement",
            "Monnify" => request.EvidenceType == "ProviderReceipt",
            "Cash" => request.EvidenceType == "CashVoucher",
            _ => false
        };
        if (!validEvidenceForChannel)
            throw new BadRequestException("The evidence type does not match the refund channel.");

        var threshold = _config.GetValue(
            "FinancialControls:HighValueRefundThreshold",
            500000m);
        if (request.Amount >= threshold)
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
            if (booking.RefundApprovedAmount != request.Amount)
                throw new BadRequestException(
                    "The completed amount must exactly match the independently approved amount.");
        }

        var processedAtUtc = DateTime.UtcNow;
        var approvingUserId = booking.RefundApprovedByUserId;
        await _folioService.ApplyRefundAsync(
            booking,
            request.Amount,
            transactionRef,
            request.Channel.Trim(),
            notes,
            processingUserId);
        booking.RefundReference = transactionRef;
        booking.RefundAmount = FolioAccounting.Money((booking.RefundAmount ?? 0m) + request.Amount);
        booking.RefundChannel = request.Channel.Trim();
        booking.RefundEvidenceType = request.EvidenceType.Trim();
        booking.RefundNotes = notes;
        booking.RefundProcessedByUserId = processingUserId;
        booking.RefundProcessedAtUtc = processedAtUtc;

        var history = ReadStatusHistory(booking.StatusHistoryJson);
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
                RefundApprovedByUserId = approvingUserId,
                RefundProcessedByUserId = processingUserId,
                RefundProcessedAtUtc = processedAtUtc
            }),
            CreatedAt = processedAtUtc
        });
        var room = booking.Room;
        await _emailOutbox.EnqueueAsync(
            TransactionalEmailTemplates.RefundCompleted,
            booking.Guest!.Email,
            new RefundCompletedEmail(
                booking.Guest.FirstName,
                booking.BookingCode,
                room?.Name ?? "Reserved Room",
                request.Amount,
                transactionRef),
            dataSubjectGuestId: booking.GuestId);

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
            PoliciesAcceptedAtUtc: b.PoliciesAcceptedAtUtc,
            QuoteId: b.QuoteId,
            Currency: b.Currency,
            RoomSubtotal: b.RoomSubtotal,
            DiscountAmount: b.DiscountAmount,
            IncludedTaxAmount: b.IncludedTaxAmount,
            TaxAmount: b.TaxAmount,
            FeeAmount: b.FeeAmount,
            PriceBreakdown: b.Quote?.Lines
                .OrderBy(line => line.SortOrder)
                .ThenBy(line => line.Id)
                .Select(line => new PricingQuoteLineDto(
                    line.Type,
                    line.Code,
                    line.Description,
                    line.StayDate,
                    line.Quantity,
                    line.UnitAmount,
                    line.Amount,
                    line.IsInclusive))
                .ToArray(),
            RoomTypeId: b.RoomTypeId,
            RoomTypeCode: b.RoomType?.Code,
            RoomTypeName: b.RoomType?.Name,
            RoomQuantity: b.RoomQuantity,
            Rooms: b.ReservationRooms.OrderBy(item => item.Sequence)
                .Select(item => new ReservationRoomDto(
                    item.Id,
                    item.Sequence,
                    item.RoomTypeId,
                    item.RoomTypeCode,
                    item.RoomTypeName,
                    item.AssignedRoomId,
                    item.AssignedRoom?.RoomNumber,
                    item.AssignedAtUtc,
                    item.AssignedByUserId))
                .ToArray(),
            Folio: b.Folio is null ? null : MapFolioSummary(b.Folio),
            RefundApprovedAmount: b.RefundApprovedAmount,
            ReservationPolicy: MapReservationPolicy(b));
    }

    private static string NormalizePhone(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return value.TrimStart().StartsWith('+') && digits.Length > 0 ? $"+{digits}" : digits;
    }

    private static ReservationPolicySnapshotDto MapReservationPolicy(Booking booking) => new(
        booking.ReservationPolicyVersion,
        booking.FreeCancellationHours,
        booking.CancellationPenaltyPercent,
        booking.DepositPercent,
        booking.NoShowPenaltyPercent,
        FolioAccounting.Money(
            (booking.RoomSubtotal - booking.DiscountAmount + booking.TaxAmount + booking.FeeAmount) *
            booking.DepositPercent / 100m),
        booking.CancellationPenaltyAmount,
        booking.NoShowPenaltyAmount);

    private static FolioSummaryDto MapFolioSummary(Folio folio)
    {
        var balance = FolioAccounting.Calculate(folio.Entries);
        return new FolioSummaryDto(
            folio.Id,
            folio.Currency,
            folio.Status,
            balance.TotalDebits,
            balance.TotalCredits,
            balance.Balance,
            balance.AmountDue,
            balance.GuestCredit,
            balance.Payments,
            balance.Refunds,
            folio.OpenedAtUtc,
            folio.ClosedAtUtc);
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
               account.Status == ProfileStatus.Active &&
               account.EmailConfirmed &&
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

    private async Task<ApplicationUser> GetActiveReservationOperatorAsync(Guid userId)
    {
        if (userId == Guid.Empty)
            throw new UnauthorizedAccessException("The authenticated operator is invalid.");
        var user = await _userManager.FindByIdAsync(userId.ToString());
        var mayManageReservations = user?.Role is UserRole.Admin or UserRole.Manager ||
                                    user?.Role == UserRole.Staff &&
                                    user.Department is "Reception" or "FrontDesk";
        if (user is null || user.Status != ProfileStatus.Active || !mayManageReservations)
        {
            throw new UnauthorizedAccessException(
                "Only an active reservations operator can change a booking.");
        }
        return user;
    }

    private List<object> ReadStatusHistory(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var history = JsonSerializer.Deserialize<List<object>>(json) ?? [];
            return history.Count <= 199 ? history : history.TakeLast(199).ToList();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                "Invalid booking status history was reset during a lifecycle transition after {ExceptionType}.",
                ex.GetType().Name);
            return [];
        }
    }

    private static string NormalizeBookingCode(string? value)
    {
        if (!TryNormalizeBookingCode(value, out var normalized))
            throw new BadRequestException("Booking code is invalid.");
        return normalized;
    }

    private static bool TryNormalizeBookingCode(string? value, out string normalized)
    {
        normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized.Length is >= 4 and <= 30 &&
               !normalized.Any(char.IsControl);
    }

    private static string RequireEmail(string? value)
    {
        if (!TryNormalizeEmail(value, out var email))
            throw new BadRequestException("A valid guest email is required.");
        return email;
    }

    private static bool TryNormalizeEmail(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Length is > 0 and <= 254 &&
               !normalized.Any(char.IsControl) &&
               new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(normalized);
    }

    private static string RequirePhone(string? value)
    {
        var phone = value?.Trim() ?? string.Empty;
        var normalized = NormalizePhone(phone);
        if (phone.Length is < 7 or > 30 || phone.Any(char.IsControl) ||
            normalized.TrimStart('+').Length is < 7 or > 20 ||
            !new System.ComponentModel.DataAnnotations.PhoneAttribute().IsValid(phone))
        {
            throw new BadRequestException("A valid guest phone number is required.");
        }
        return phone;
    }

    private static string RequireText(
        string? value,
        string field,
        int maximumLength,
        int minimumLength = 1)
    {
        var cleaned = value?.Trim() ?? string.Empty;
        if (cleaned.Length < minimumLength || cleaned.Length > maximumLength || cleaned.Any(char.IsControl))
            throw new BadRequestException($"{field} is invalid or too long.");
        return cleaned;
    }

    private static string? NormalizeOptionalText(string? value, string field, int maximumLength)
    {
        if (value is null) return null;
        var cleaned = value.Trim();
        if (cleaned.Length > maximumLength || cleaned.Any(char.IsControl))
            throw new BadRequestException($"{field} is invalid or too long.");
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static void ValidateOptionalText(string? value, string field, int maximumLength)
    {
        if (value is not null && (value.Length > maximumLength || value.Any(char.IsControl)))
            throw new BadRequestException($"{field} is invalid or too long.");
    }

    private static void ValidateOptionalToken(string? value, string field)
    {
        if (value is not null &&
            (value.Length is < 40 or > 128 || value.Any(char.IsControl)))
            throw new BadRequestException($"{field} is invalid.");
    }

    private static bool IsInvalidAuditValue(string? value, int maximumLength) =>
        value is not null && (value.Length > maximumLength || value.Any(char.IsControl));

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
