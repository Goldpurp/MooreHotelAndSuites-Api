using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.Infrastructure.Services;

public sealed class ReservationAmendmentService : IReservationAmendmentService
{
    private readonly MooreHotelsDbContext _db;
    private readonly IHotelTimeService _hotelTime;
    private readonly IEmailOutbox _emailOutbox;
    private readonly IConfiguration _config;

    public ReservationAmendmentService(
        MooreHotelsDbContext db,
        IHotelTimeService hotelTime,
        IEmailOutbox emailOutbox,
        IConfiguration config)
    {
        _db = db;
        _hotelTime = hotelTime;
        _emailOutbox = emailOutbox;
        _config = config;
    }

    public async Task<ReservationAmendmentDto> AmendAsync(
        Guid bookingId,
        AmendReservationRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        if (bookingId == Guid.Empty) throw new NotFoundException("Booking not found.");
        await RequireActorAsync(actorId, cancellationToken);
        ValidateRequest(request);
        var reason = request.Reason.Trim();
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            var booking = await _db.Bookings
                .FromSqlInterpolated($"SELECT * FROM bookings WHERE \"Id\" = {bookingId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException("Booking not found.");
            if (booking.Status is not (BookingStatus.Pending or BookingStatus.Confirmed))
                throw new BadRequestException("Only pending or confirmed reservations can be amended.");
            if (DateTime.UtcNow >= booking.CheckIn)
                throw new BadRequestException("A reservation cannot be amended after its check-in time.");

            var folio = await _db.Folios.Include(item => item.Entries)
                .SingleOrDefaultAsync(item => item.BookingId == booking.Id, cancellationToken)
                ?? throw new InvalidOperationException("The booking folio is missing.");
            if (folio.Status != FolioStatus.Open)
                throw new BadRequestException("A reservation with a closed folio cannot be amended.");

            var tokenHash = BookingGuestAccess.Hash(request.QuoteToken.Trim());
            var quote = await _db.BookingQuotes
                .FromSqlInterpolated(
                    $"SELECT * FROM booking_quotes WHERE \"Id\" = {request.QuoteId} AND \"AccessTokenHash\" = {tokenHash} FOR UPDATE")
                .Include(item => item.Lines)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new BadRequestException("The amendment quote is invalid.");
            var now = DateTime.UtcNow;
            if (quote.ConsumedAtUtc.HasValue || quote.ExpiresAtUtc <= now)
                throw new BadRequestException("The amendment quote is expired or already used. Request a new quote.");
            if (quote.AmendmentBookingId != booking.Id)
                throw new BadRequestException("The amendment quote was not issued for this reservation.");
            if (quote.RoomId != request.RoomId || quote.RoomTypeId != request.RoomTypeId ||
                quote.RoomQuantity != request.RoomQuantity ||
                quote.CheckInDate != DateOnly.FromDateTime(request.CheckIn) ||
                quote.CheckOutDate != DateOnly.FromDateTime(request.CheckOut) ||
                quote.AdultCount != request.AdultCount || quote.ChildCount != request.ChildCount)
                throw new BadRequestException("The requested amendment does not match its immutable quote.");
            if (!string.Equals(quote.Currency, booking.Currency, StringComparison.Ordinal))
                throw new BadRequestException("A reservation currency cannot be changed by amendment.");
            var previousPromotionId = booking.QuoteId.HasValue
                ? await _db.BookingQuotes.AsNoTracking()
                    .Where(item => item.Id == booking.QuoteId.Value)
                    .Select(item => item.PromotionId)
                    .SingleOrDefaultAsync(cancellationToken)
                : null;

            var roomType = await _db.RoomTypes.SingleOrDefaultAsync(
                item => item.Id == quote.RoomTypeId && item.IsActive, cancellationToken)
                ?? throw new BadRequestException("The quoted room type is no longer active.");
            Room? physicalRoom = null;
            if (quote.RoomId.HasValue)
            {
                physicalRoom = await _db.Rooms.SingleOrDefaultAsync(
                    room => room.Id == quote.RoomId && room.RoomTypeId == roomType.Id,
                    cancellationToken)
                    ?? throw new BadRequestException("The quoted physical room no longer belongs to the room type.");
                if (!physicalRoom.IsOnline || physicalRoom.Status is RoomStatus.Maintenance or RoomStatus.OutOfOrder)
                    throw new BadRequestException("The quoted physical room is not sellable.");
            }
            if (checked(request.AdultCount + request.ChildCount) >
                checked(roomType.MaxOccupancy * request.RoomQuantity))
                throw new BadRequestException("Guest occupancy exceeds the selected room inventory.");

            var checkIn = _hotelTime.GetCheckInUtc(request.CheckIn);
            var checkOut = _hotelTime.GetCheckOutUtc(request.CheckOut);
            await LockAndValidateInventoryAsync(
                booking.Id, roomType.Id, physicalRoom?.Id, request.RoomQuantity,
                checkIn, checkOut, cancellationToken);

            if (quote.PromotionId != previousPromotionId)
            {
                var promotionIds = new[] { previousPromotionId, quote.PromotionId }
                    .Where(item => item.HasValue)
                    .Select(item => item!.Value)
                    .Distinct()
                    .OrderBy(item => item)
                    .ToArray();
                var promotions = await _db.Promotions
                    .FromSqlInterpolated(
                        $"SELECT * FROM promotions WHERE \"Id\" = ANY({promotionIds}) ORDER BY \"Id\" FOR UPDATE")
                    .ToListAsync(cancellationToken);
                if (quote.PromotionId.HasValue)
                {
                    var promotion = promotions.Single(item => item.Id == quote.PromotionId.Value);
                    if (promotion.RedemptionLimit.HasValue &&
                        promotion.RedemptionCount >= promotion.RedemptionLimit)
                    {
                        throw new BadRequestException(
                            "The promotion has reached its redemption limit. Request a new quote.");
                    }
                    promotion.RedemptionCount++;
                    promotion.UpdatedAtUtc = now;
                }
                if (previousPromotionId.HasValue)
                {
                    var previousPromotion = promotions.Single(item => item.Id == previousPromotionId.Value);
                    previousPromotion.RedemptionCount = Math.Max(0, previousPromotion.RedemptionCount - 1);
                    previousPromotion.UpdatedAtUtc = now;
                }
            }

            var oldBaseTotal = FolioAccounting.Money(
                booking.RoomSubtotal - booking.DiscountAmount + booking.TaxAmount + booking.FeeAmount);
            var previousAmount = booking.Amount;
            var previousState = Snapshot(booking);
            var amendment = new BookingAmendment
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                PreviousStateJson = JsonSerializer.Serialize(previousState),
                PreviousAmount = previousAmount,
                NewAmount = FolioAccounting.Money(previousAmount - oldBaseTotal + quote.TotalAmount),
                PriceDifference = FolioAccounting.Money(quote.TotalAmount - oldBaseTotal),
                RoomTypeId = roomType.Id,
                RoomQuantity = request.RoomQuantity,
                CheckIn = checkIn,
                CheckOut = checkOut,
                AdultCount = request.AdultCount,
                ChildCount = request.ChildCount,
                Reason = reason,
                AmendedAtUtc = now,
                AmendedByUserId = actorId
            };

            VoidActiveReservationPricing(folio, amendment, actorId, now);
            AddQuotedPricing(folio, quote, amendment, actorId, now);

            _db.ReservationRooms.RemoveRange(await _db.ReservationRooms
                .Where(item => item.BookingId == booking.Id).ToListAsync(cancellationToken));
            for (var sequence = 1; sequence <= request.RoomQuantity; sequence++)
            {
                _db.ReservationRooms.Add(new ReservationRoom
                {
                    Id = Guid.NewGuid(),
                    BookingId = booking.Id,
                    RoomTypeId = roomType.Id,
                    RoomTypeCode = roomType.Code,
                    RoomTypeName = roomType.Name,
                    AssignedRoomId = sequence == 1 ? physicalRoom?.Id : null,
                    AssignedAtUtc = sequence == 1 && physicalRoom is not null ? now : null,
                    AssignedByUserId = sequence == 1 && physicalRoom is not null ? actorId : null,
                    Sequence = sequence,
                    CreatedAtUtc = now
                });
            }

            booking.RoomId = physicalRoom?.Id;
            booking.RoomTypeId = roomType.Id;
            booking.RoomQuantity = request.RoomQuantity;
            booking.CheckIn = checkIn;
            booking.CheckOut = checkOut;
            booking.AdultCount = request.AdultCount;
            booking.ChildCount = request.ChildCount;
            booking.QuoteId = quote.Id;
            booking.RoomSubtotal = quote.RoomSubtotal;
            booking.DiscountAmount = quote.DiscountAmount;
            booking.IncludedTaxAmount = quote.IncludedTaxAmount;
            booking.TaxAmount = quote.TaxAmount;
            booking.FeeAmount = quote.FeeAmount;
            booking.Amount = amendment.NewAmount;
            amendment.NewStateJson = JsonSerializer.Serialize(Snapshot(booking));
            quote.ConsumedAtUtc = now;
            RecalculatePaymentStatus(booking, folio);
            _db.BookingAmendments.Add(amendment);
            _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                ProfileId = actorId,
                Action = "RESERVATION_AMENDED",
                EntityType = "Booking",
                EntityId = booking.Id.ToString(),
                OldDataJson = amendment.PreviousStateJson,
                NewDataJson = amendment.NewStateJson,
                CreatedAt = now
            });

            if (!string.IsNullOrWhiteSpace(booking.GuestId))
            {
                var guest = await _db.Guests.AsNoTracking()
                    .SingleOrDefaultAsync(g => g.Id == booking.GuestId, cancellationToken);
                if (guest is not null && !string.IsNullOrWhiteSpace(guest.Email))
                {
                    var deterministicEmailId = CreateDeterministicId($"amendment:{amendment.Id:N}");
                    if (!_db.EmailOutboxMessages.Local.Any(m => m.Id == deterministicEmailId) &&
                        !await _db.EmailOutboxMessages.AnyAsync(m => m.Id == deterministicEmailId, cancellationToken))
                    {
                        var nights = Math.Max(1, (int)Math.Ceiling((booking.CheckOut - booking.CheckIn).TotalDays));
                        var publicAppUrl = (_config["PublicAppUrl"] ??
                            throw new InvalidOperationException("PublicAppUrl is not configured.")).TrimEnd('/');
                        var manageBookingUrl = $"{publicAppUrl}/booking-status?code={Uri.EscapeDataString(booking.BookingCode)}";
                        var guestName = $"{guest.FirstName} {guest.LastName}".Trim();
                        if (string.IsNullOrWhiteSpace(guestName)) guestName = "Valued Guest";
                        var folioBalance = FolioAccounting.Calculate(folio.Entries);
                        var balanceDue = FolioAccounting.Money(Math.Max(0m, folioBalance.Balance));

                        var emailPayload = new BookingAmendmentConfirmationEmail(
                            guestName,
                            booking.BookingCode,
                            roomType.Name,
                            request.RoomQuantity,
                            booking.CheckIn,
                            booking.CheckOut,
                            nights,
                            booking.RoomSubtotal,
                            booking.TaxAmount,
                            booking.FeeAmount,
                            booking.Amount,
                            balanceDue,
                            amendment.PriceDifference,
                            manageBookingUrl,
                            amendment.Reason);

                        var emailMessage = _emailOutbox.Create(
                            TransactionalEmailTemplates.BookingAmendmentConfirmation,
                            guest.Email,
                            emailPayload,
                            booking.GuestId);
                        emailMessage.Id = deterministicEmailId;
                        _db.EmailOutboxMessages.Add(emailMessage);
                    }
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToDto(amendment, folio);
        });
    }

    public async Task<IReadOnlyList<ReservationAmendmentDto>> GetHistoryAsync(
        Guid bookingId,
        CancellationToken cancellationToken = default)
    {
        if (bookingId == Guid.Empty) throw new NotFoundException("Booking not found.");
        if (!await _db.Bookings.AsNoTracking().AnyAsync(item => item.Id == bookingId, cancellationToken))
            throw new NotFoundException("Booking not found.");
        var folio = await _db.Folios.AsNoTracking().Include(item => item.Entries)
            .SingleAsync(item => item.BookingId == bookingId, cancellationToken);
        var amendments = await _db.BookingAmendments.AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .OrderBy(item => item.AmendedAtUtc).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return amendments.Select(item => ToDto(item, folio)).ToArray();
    }

    private async Task LockAndValidateInventoryAsync(
        Guid bookingId,
        Guid roomTypeId,
        Guid? roomId,
        int units,
        DateTime checkIn,
        DateTime checkOut,
        CancellationToken cancellationToken)
    {
        var advisoryKey = BitConverter.ToInt64(roomTypeId.ToByteArray(), 0);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({advisoryKey})", cancellationToken);
        var expirationCutoff = BookingPaymentPolicy.GetExpirationCutoffUtc(DateTime.UtcNow);
        if (roomId.HasValue && await _db.Bookings.AnyAsync(existing =>
                existing.Id != bookingId &&
                (existing.ReservationRooms.Any(item => item.AssignedRoomId == roomId) ||
                 !existing.ReservationRooms.Any() && existing.RoomId == roomId) &&
                existing.Status != BookingStatus.Cancelled &&
                existing.Status != BookingStatus.CheckedOut &&
                existing.Status != BookingStatus.NoShow &&
                !(existing.Status == BookingStatus.Pending &&
                  (existing.PaymentStatus == PaymentStatus.Unpaid ||
                   existing.PaymentStatus == PaymentStatus.AwaitingVerification) &&
                  existing.CreatedAt <= expirationCutoff) &&
                existing.CheckIn < checkOut && existing.CheckOut > checkIn,
                cancellationToken))
            throw new ConflictException("The selected room is already assigned during the amended stay.");

        var physicalRoomIds = await _db.Rooms
            .Where(room => room.RoomTypeId == roomTypeId && room.IsOnline &&
                           room.Status != RoomStatus.Maintenance && room.Status != RoomStatus.OutOfOrder)
            .Select(room => room.Id).ToArrayAsync(cancellationToken);
        var reservations = await _db.ReservationRooms
            .Where(item => item.BookingId != bookingId && item.RoomTypeId == roomTypeId &&
                           item.Booking != null && item.Booking.CheckIn < checkOut &&
                           item.Booking.CheckOut > checkIn &&
                           item.Booking.Status != BookingStatus.Cancelled &&
                           item.Booking.Status != BookingStatus.CheckedOut &&
                           item.Booking.Status != BookingStatus.NoShow &&
                           !(item.Booking.Status == BookingStatus.Pending &&
                             (item.Booking.PaymentStatus == PaymentStatus.Unpaid ||
                              item.Booking.PaymentStatus == PaymentStatus.AwaitingVerification) &&
                             item.Booking.CreatedAt <= expirationCutoff))
            .Select(item => new { item.Booking!.CheckIn, item.Booking.CheckOut })
            .ToListAsync(cancellationToken);
        var startDate = DateOnly.FromDateTime(checkIn);
        var endDate = DateOnly.FromDateTime(checkOut);
        var closures = await _db.RoomInventoryClosures
            .Where(item => item.RoomTypeId == roomTypeId && item.IsActive &&
                           item.StartDate < endDate && item.EndDate > startDate)
            .Select(item => new { item.RoomId, item.StartDate, item.EndDate, item.Units })
            .ToListAsync(cancellationToken);
        for (var date = startDate; date < endDate; date = date.AddDays(1))
        {
            var dayStart = _hotelTime.GetCheckInUtc(date.ToDateTime(TimeOnly.MinValue));
            var dayEnd = _hotelTime.GetCheckOutUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue));
            var reserved = reservations.Count(stay => stay.CheckIn < dayEnd && stay.CheckOut > dayStart);
            var active = closures.Where(item => item.StartDate <= date && item.EndDate > date).ToArray();
            var closedRooms = active.Where(item => item.RoomId.HasValue && physicalRoomIds.Contains(item.RoomId.Value))
                .Select(item => item.RoomId!.Value).Distinct().Count();
            var closedUnits = active.Where(item => !item.RoomId.HasValue).Sum(item => item.Units);
            if (Math.Max(0, physicalRoomIds.Length - Math.Min(physicalRoomIds.Length, closedRooms + closedUnits) - reserved) < units)
                throw new ConflictException("The amended room type is sold out for part of the stay.");
        }
    }

    private void VoidActiveReservationPricing(
        Folio folio, BookingAmendment amendment, Guid actorId, DateTime now)
    {
        var originals = folio.Entries
            .Where(entry => (entry.SourceType is "Booking" or "BookingAmendment") &&
                            (entry.Type is FolioEntryType.RoomCharge or FolioEntryType.Discount or FolioEntryType.Tax or FolioEntryType.Fee) &&
                            !folio.Entries.Any(reversal => reversal.ReversesEntryId == entry.Id))
            .ToArray();
        foreach (var original in originals)
        {
            _db.FolioEntries.Add(FolioAccounting.NewEntry(
                folio,
                FolioEntryType.Void,
                original.Direction == FolioEntryDirection.Debit ? FolioEntryDirection.Credit : FolioEntryDirection.Debit,
                original.Amount,
                $"Amendment reversal: {original.Description}",
                "BookingAmendment",
                amendment.Id.ToString(),
                $"amendment:{amendment.Id:N}:void:{original.Id:N}",
                now,
                actorId,
                reversesEntryId: original.Id,
                notes: amendment.Reason));
        }
    }

    private void AddQuotedPricing(
        Folio folio, BookingQuote quote, BookingAmendment amendment, Guid actorId, DateTime now)
    {
        Add(FolioEntryType.RoomCharge, FolioEntryDirection.Debit, quote.RoomSubtotal, "Amended room accommodation", "room");
        Add(FolioEntryType.Discount, FolioEntryDirection.Credit, quote.DiscountAmount, "Amended reservation discount", "discount");
        Add(FolioEntryType.Tax, FolioEntryDirection.Debit, quote.TaxAmount, "Amended exclusive taxes", "tax");
        Add(FolioEntryType.Fee, FolioEntryDirection.Debit, quote.FeeAmount, "Amended reservation fees", "fee");
        return;

        void Add(FolioEntryType type, FolioEntryDirection direction, decimal amount, string description, string key)
        {
            if (amount <= 0) return;
            _db.FolioEntries.Add(FolioAccounting.NewEntry(
                folio, type, direction, amount, description, "BookingAmendment", amendment.Id.ToString(),
                $"amendment:{amendment.Id:N}:{key}", now, actorId));
        }
    }

    private static object Snapshot(Booking booking) => new
    {
        booking.RoomId,
        booking.RoomTypeId,
        booking.RoomQuantity,
        booking.CheckIn,
        booking.CheckOut,
        booking.AdultCount,
        booking.ChildCount,
        booking.QuoteId,
        booking.Currency,
        booking.RoomSubtotal,
        booking.DiscountAmount,
        booking.IncludedTaxAmount,
        booking.TaxAmount,
        booking.FeeAmount,
        booking.Amount,
        booking.PaymentStatus
    };

    private static void RecalculatePaymentStatus(Booking booking, Folio folio)
    {
        var balance = FolioAccounting.Calculate(folio.Entries);
        if (balance.GuestCredit > 0) booking.PaymentStatus = PaymentStatus.RefundPending;
        else if (balance.Payments - balance.Refunds <= 0)
            booking.PaymentStatus = booking.PaymentMethod == PaymentMethod.DirectTransfer
                ? PaymentStatus.AwaitingVerification : PaymentStatus.Unpaid;
        else booking.PaymentStatus = balance.AmountDue == 0 ? PaymentStatus.Paid : PaymentStatus.PartiallyPaid;
    }

    private void ValidateRequest(AmendReservationRequest request)
    {
        if (request.QuoteId == Guid.Empty || string.IsNullOrWhiteSpace(request.QuoteToken) ||
            request.QuoteToken.Length is < 40 or > 200 || request.QuoteToken.Any(char.IsControl))
            throw new BadRequestException("A current amendment quote is required.");
        if (request.RoomTypeId == Guid.Empty || request.RoomId == Guid.Empty)
            throw new BadRequestException("Room or room-type identifier is invalid.");
        if (request.RoomQuantity is < 1 or > 10 || request.AdultCount is < 1 or > 20 ||
            request.ChildCount is < 0 or > 20)
            throw new BadRequestException("Room quantity and occupancy are invalid.");
        if (DateOnly.FromDateTime(request.CheckIn) < _hotelTime.Today ||
            DateOnly.FromDateTime(request.CheckOut) <= DateOnly.FromDateTime(request.CheckIn) ||
            DateOnly.FromDateTime(request.CheckOut).DayNumber - DateOnly.FromDateTime(request.CheckIn).DayNumber > 90 ||
            DateOnly.FromDateTime(request.CheckIn) > _hotelTime.Today.AddYears(2))
            throw new BadRequestException("Amended stay dates are invalid.");
        if (string.IsNullOrWhiteSpace(request.Reason) ||
            request.Reason.Trim().Length is < 10 or > 500 || request.Reason.Any(char.IsControl))
            throw new BadRequestException("An amendment reason of 10 to 500 characters is required.");
    }

    private async Task RequireActorAsync(Guid actorId, CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty)
            throw new UnauthorizedAccessException("The reservation actor is invalid.");
        var actor = await _db.Users.AsNoTracking().SingleOrDefaultAsync(user =>
            user.Id == actorId && user.Status == ProfileStatus.Active,
            cancellationToken);
        if (actor is null || actor.Role is UserRole.Client ||
            actor.Role == UserRole.Staff && actor.Department is not ("Reception" or "FrontDesk"))
        {
            throw new UnauthorizedAccessException("The actor cannot amend reservations.");
        }
    }

    private static ReservationAmendmentDto ToDto(BookingAmendment amendment, Folio folio) => new(
        amendment.Id, amendment.BookingId, amendment.PreviousAmount, amendment.NewAmount,
        amendment.PriceDifference, amendment.Reason, amendment.AmendedAtUtc, amendment.AmendedByUserId,
        amendment.RoomTypeId, amendment.RoomQuantity, amendment.CheckIn, amendment.CheckOut,
        amendment.AdultCount, amendment.ChildCount, FolioService.ToSummary(folio));

    private static Guid CreateDeterministicId(string key)
    {
        // This preserves the established outbox idempotency key format. The hash is
        // only a stable namespace mapping for server-generated IDs, not a security primitive.
#pragma warning disable CA5351
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
#pragma warning restore CA5351
        return new Guid(hash);
    }
}
