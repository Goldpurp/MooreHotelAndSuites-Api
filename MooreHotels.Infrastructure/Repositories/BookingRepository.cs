using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Exceptions;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Domain.Common;
using MooreHotels.Infrastructure.Persistence;
using MooreHotels.Application.Interfaces;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MooreHotels.Application.DTOs.Pricing;

namespace MooreHotels.Infrastructure.Repositories;

public class BookingRepository : IBookingRepository
{
    private const int BookingCodeUpperBound = 1_000_000;
    private const int BookingCodeAllocationAttempts = 64;

    private readonly MooreHotelsDbContext _db;
    private readonly IEmailOutbox _emailOutbox;
    public BookingRepository(MooreHotelsDbContext db, IEmailOutbox emailOutbox)
    {
        _db = db;
        _emailOutbox = emailOutbox;
    }

    public async Task<Booking?> GetByIdAsync(Guid id) =>
        await _db.Bookings.Include(b => b.Room).Include(b => b.Guest)
            .Include(b => b.Quote).ThenInclude(quote => quote!.Lines)
            .FirstOrDefaultAsync(b => b.Id == id);

    public async Task<Booking?> GetByCodeAsync(string code) =>
        await _db.Bookings.Include(b => b.Room).Include(b => b.Guest)
            .Include(b => b.Quote).ThenInclude(quote => quote!.Lines)
            .FirstOrDefaultAsync(b => b.BookingCode == code);

    public async Task<Booking?> GetByPaymentReferenceAsync(string paymentReference) =>
        await _db.Bookings
            .Include(booking => booking.Room)
            .Include(booking => booking.Guest)
            .Include(booking => booking.Quote).ThenInclude(quote => quote!.Lines)
            .FirstOrDefaultAsync(booking => booking.TransactionReference == paymentReference);

    public async Task<IEnumerable<Booking>> GetAllAsync() =>
        await _db.Bookings
            .AsNoTracking()
            .Include(b => b.Room)
            .Include(b => b.Guest)
            .Include(b => b.Quote).ThenInclude(quote => quote!.Lines)
            .OrderByDescending(b => b.CreatedAt)
            .Take(2000)
            .ToListAsync();

    public async Task<IEnumerable<Booking>> GetByGuestIdAsync(string guestId) =>
        await _db.Bookings
            .AsNoTracking()
            .Include(booking => booking.Room)
            .Include(booking => booking.Guest)
            .Include(booking => booking.Quote).ThenInclude(quote => quote!.Lines)
            .Where(booking => booking.GuestId == guestId)
            .OrderByDescending(booking => booking.CheckIn)
            .Take(500)
            .ToListAsync();

    public async Task<string> GenerateBookingCodeAsync(
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < BookingCodeAllocationAttempts; attempt++)
        {
            var randomNumber = RandomNumberGenerator.GetInt32(BookingCodeUpperBound);
            var bookingCode =
                $"MHS{randomNumber.ToString("D6", CultureInfo.InvariantCulture)}";
            var allocatedAtUtc = DateTime.UtcNow;

            // INSERT ... ON CONFLICT is the atomic collision check. It protects
            // allocations made concurrently by this process or any other API
            // instance without exposing booking volume through a sequence.
            var reserved = await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO booking_code_allocations ("Code", "AllocatedAtUtc")
                 VALUES ({bookingCode}, {allocatedAtUtc})
                 ON CONFLICT ("Code") DO NOTHING
                 """,
                cancellationToken);

            if (reserved == 1)
                return bookingCode;
        }

        throw new InvalidOperationException(
            "A unique booking reference could not be allocated. Please retry the booking.");
    }

    public async Task<bool> IsRoomBookedAsync(Guid roomId, DateTime checkIn, DateTime checkOut)
    {
        var expirationCutoffUtc = BookingPaymentPolicy.GetExpirationCutoffUtc(DateTime.UtcNow);
        return await _db.Bookings.AnyAsync(booking =>
            booking.RoomId == roomId &&
            booking.Status != BookingStatus.Cancelled &&
            booking.Status != BookingStatus.CheckedOut &&
            booking.Status != BookingStatus.NoShow &&
            !(booking.Status == BookingStatus.Pending &&
              (booking.PaymentStatus == PaymentStatus.Unpaid ||
               booking.PaymentStatus == PaymentStatus.AwaitingVerification) &&
              booking.CreatedAt <= expirationCutoffUtc) &&
            booking.CheckIn < checkOut &&
            booking.CheckOut > checkIn);
    }

    public async Task<bool> QueueBookingEmailVerificationAsync(
        BookingEmailVerification verification,
        EmailOutboxMessage emailMessage,
        DateTime throttleCutoffUtc,
        CancellationToken cancellationToken = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            // Serialize requests for the normalized email across API instances.
            // This makes the cooldown an enforcement rule rather than a best-effort check.
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({verification.Email}, 0))",
                cancellationToken);

            var recentlyQueued = await _db.BookingEmailVerifications.AnyAsync(
                existing =>
                    existing.Email == verification.Email &&
                    existing.ConsumedAtUtc == null &&
                    existing.ExpiresAtUtc > verification.CreatedAtUtc &&
                    existing.CreatedAtUtc >= throttleCutoffUtc,
                cancellationToken);
            if (recentlyQueued)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }

            // A newly delivered link replaces older unconsumed links for the email.
            await _db.BookingEmailVerifications
                .Where(existing =>
                    existing.Email == verification.Email &&
                    existing.ConsumedAtUtc == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        existing => existing.ConsumedAtUtc,
                        verification.CreatedAtUtc),
                    cancellationToken);

            _db.BookingEmailVerifications.Add(verification);
            _db.EmailOutboxMessages.Add(emailMessage);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        });
    }

    public Task<bool> IsBookingEmailVerificationValidAsync(
        string email,
        string tokenHash,
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        _db.BookingEmailVerifications
            .AsNoTracking()
            .AnyAsync(
                verification =>
                    verification.Email == email &&
                    verification.TokenHash == tokenHash &&
                    verification.ConsumedAtUtc == null &&
                    verification.ExpiresAtUtc > utcNow,
                cancellationToken);

    public async Task AddAsync(
        Booking booking,
        Guest? newGuest = null,
        IReadOnlyCollection<EmailOutboxMessage>? emailMessages = null,
        BookingEmailVerificationProof? emailVerification = null,
        ValidatedBookingQuote? pricingQuote = null,
        CancellationToken cancellationToken = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            BookingEmailVerification? verification = null;
            if (emailVerification is not null)
            {
                verification = await _db.BookingEmailVerifications
                    .FromSqlInterpolated(
                        $"""
                         SELECT * FROM booking_email_verifications
                         WHERE "Email" = {emailVerification.Email}
                           AND "TokenHash" = {emailVerification.TokenHash}
                           AND "ConsumedAtUtc" IS NULL
                           AND "ExpiresAtUtc" > {emailVerification.VerifiedAtUtc}
                         FOR UPDATE
                         """)
                    .SingleOrDefaultAsync(cancellationToken);
                if (verification is null)
                {
                    throw new BadRequestException(
                        "Verify the guest email again before creating this booking.");
                }
            }

            BookingQuote? lockedQuote = null;
            if (pricingQuote is not null)
            {
                lockedQuote = await _db.BookingQuotes
                    .FromSqlInterpolated(
                        $"""
                         SELECT * FROM booking_quotes
                         WHERE "Id" = {pricingQuote.QuoteId}
                           AND "AccessTokenHash" = {pricingQuote.AccessTokenHash}
                         FOR UPDATE
                         """)
                    .SingleOrDefaultAsync(cancellationToken);
                if (lockedQuote is null ||
                    lockedQuote.ConsumedAtUtc.HasValue ||
                    lockedQuote.ExpiresAtUtc <= DateTime.UtcNow)
                {
                    throw new BadRequestException(
                        "The pricing quote is invalid, expired, or already used. Request a new quote.");
                }
                if (lockedQuote.RoomId != booking.RoomId ||
                    lockedQuote.CheckInDate != DateOnly.FromDateTime(booking.CheckIn) ||
                    lockedQuote.CheckOutDate != DateOnly.FromDateTime(booking.CheckOut) ||
                    lockedQuote.AdultCount != booking.AdultCount ||
                    lockedQuote.ChildCount != booking.ChildCount ||
                    lockedQuote.Currency != booking.Currency ||
                    lockedQuote.RoomSubtotal != booking.RoomSubtotal ||
                    lockedQuote.DiscountAmount != booking.DiscountAmount ||
                    lockedQuote.IncludedTaxAmount != booking.IncludedTaxAmount ||
                    lockedQuote.TaxAmount != booking.TaxAmount ||
                    lockedQuote.FeeAmount != booking.FeeAmount ||
                    lockedQuote.TotalAmount != booking.Amount)
                {
                    throw new BadRequestException(
                        "The booking does not match its immutable pricing quote.");
                }

                if (lockedQuote.PromotionId.HasValue)
                {
                    var promotion = await _db.Promotions
                        .FromSqlInterpolated(
                            $"""
                             SELECT * FROM promotions
                             WHERE "Id" = {lockedQuote.PromotionId.Value}
                             FOR UPDATE
                             """)
                        .SingleAsync(cancellationToken);
                    if (promotion.RedemptionLimit.HasValue &&
                        promotion.RedemptionCount >= promotion.RedemptionLimit)
                    {
                        throw new BadRequestException(
                            "The promotion has reached its redemption limit. Request a new quote.");
                    }
                    promotion.RedemptionCount++;
                    promotion.UpdatedAtUtc = DateTime.UtcNow;
                }
            }

            // Serialize booking creation per room across every API instance.
            // This closes the race between the availability check and INSERT
            // without locking unrelated rooms or holding a long transaction.
            var advisoryKey = BitConverter.ToInt64(booking.RoomId.ToByteArray(), 0);
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({advisoryKey})",
                cancellationToken);

            var expirationCutoffUtc = BookingPaymentPolicy.GetExpirationCutoffUtc(DateTime.UtcNow);
            var conflict = await _db.Bookings.AnyAsync(existing =>
                    existing.RoomId == booking.RoomId &&
                    existing.Status != BookingStatus.Cancelled &&
                    existing.Status != BookingStatus.CheckedOut &&
                    existing.Status != BookingStatus.NoShow &&
                    !(existing.Status == BookingStatus.Pending &&
                      (existing.PaymentStatus == PaymentStatus.Unpaid ||
                       existing.PaymentStatus == PaymentStatus.AwaitingVerification) &&
                      existing.CreatedAt <= expirationCutoffUtc) &&
                    existing.CheckIn < booking.CheckOut &&
                    existing.CheckOut > booking.CheckIn,
                cancellationToken);

            if (conflict)
            {
                throw new BadRequestException("This room was just reserved for the selected dates. Please choose another room or date.");
            }

            if (newGuest is not null)
                await _db.Guests.AddAsync(newGuest);
            await _db.Bookings.AddAsync(booking);
            if (emailMessages is not null)
                await _db.EmailOutboxMessages.AddRangeAsync(emailMessages);
            if (verification is not null)
                verification.ConsumedAtUtc = emailVerification!.VerifiedAtUtc;
            if (lockedQuote is not null)
                lockedQuote.ConsumedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    public async Task UpdateAsync(Booking booking)
    {
        _db.Bookings.Update(booking);
        await _db.SaveChangesAsync();
    }

    public async Task<ManualTransferConfirmationDto> ConfirmManualTransferAsync(
        string bookingCode,
        ManualTransferConfirmationActor actor,
        CancellationToken cancellationToken = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            // A row-level lock serializes confirmation attempts for this booking
            // across all API instances. The state is re-checked after the lock.
            var booking = await _db.Bookings
                .FromSqlInterpolated(
                    $"SELECT * FROM bookings WHERE \"BookingCode\" = {bookingCode} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);

            if (booking is null)
            {
                throw new NotFoundException("Booking not found.");
            }

            if (booking.PaymentMethod != PaymentMethod.DirectTransfer)
            {
                throw new BadRequestException(
                    "Only a DirectTransfer booking can be confirmed manually.");
            }

            if (booking.Status == BookingStatus.Cancelled)
            {
                throw new BadRequestException("A cancelled booking cannot be confirmed.");
            }

            if (booking.PaymentStatus == PaymentStatus.Paid)
            {
                throw new BadRequestException("This booking has already been paid.");
            }

            if (booking.PaymentStatus is PaymentStatus.RefundPending or PaymentStatus.Refunded)
            {
                throw new BadRequestException(
                    "A refund-pending or refunded booking cannot be confirmed.");
            }

            if (BookingPaymentPolicy.IsExpiredUnconfirmed(
                    booking.Status,
                    booking.PaymentStatus,
                    booking.CreatedAt,
                    DateTime.UtcNow))
            {
                throw new BadRequestException(
                    "This booking's one-hour payment window has expired. The room has been released and the transfer cannot be confirmed against this booking.");
            }

            if (booking.PaymentStatus != PaymentStatus.AwaitingVerification ||
                booking.Status != BookingStatus.Pending)
            {
                throw new BadRequestException(
                    "This booking is not awaiting bank-transfer payment verification.");
            }

            var previousPaymentStatus = booking.PaymentStatus;
            var previousBookingStatus = booking.Status;
            var confirmedAtUtc = DateTime.UtcNow;
            var serverGeneratedId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var internalReference = $"MANUAL-{booking.BookingCode}-{serverGeneratedId}";

            booking.PaymentStatus = PaymentStatus.Paid;
            booking.Status = BookingStatus.Confirmed;
            booking.TransactionReference = internalReference;
            booking.PaymentConfirmationMethod = ManualTransferConfirmation.Method;
            booking.PaymentConfirmedByUserId = actor.UserId;
            booking.PaymentConfirmedAtUtc = confirmedAtUtc;

            _db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                ProfileId = actor.UserId,
                Action = "MANUAL_PAYMENT_CONFIRMED",
                EntityType = "Booking",
                EntityId = booking.Id.ToString(),
                OldDataJson = JsonSerializer.Serialize(new
                {
                    BookingId = booking.Id,
                    booking.BookingCode,
                    PaymentStatus = previousPaymentStatus.ToString(),
                    Status = previousBookingStatus.ToString()
                }),
                NewDataJson = JsonSerializer.Serialize(new
                {
                    BookingId = booking.Id,
                    booking.BookingCode,
                    booking.GuestId,
                    AmountConfirmed = booking.Amount,
                    PreviousPaymentStatus = previousPaymentStatus.ToString(),
                    NewPaymentStatus = booking.PaymentStatus.ToString(),
                    PreviousStatus = previousBookingStatus.ToString(),
                    NewStatus = booking.Status.ToString(),
                    InternalConfirmationReference = internalReference,
                    ConfirmingStaffId = actor.UserId,
                    ConfirmingStaffName = actor.Name,
                    ConfirmingStaffRole = actor.Role.ToString(),
                    ConfirmationTimestampUtc = confirmedAtUtc,
                    ConfirmationMethod = ManualTransferConfirmation.Method,
                    RequestId = actor.RequestId
                }),
                CreatedAt = confirmedAtUtc
            });

            var guest = await _db.Guests.SingleOrDefaultAsync(
                item => item.Id == booking.GuestId,
                cancellationToken);
            var room = await _db.Rooms.SingleOrDefaultAsync(
                item => item.Id == booking.RoomId,
                cancellationToken);
            if (guest is not null)
            {
                _db.EmailOutboxMessages.Add(_emailOutbox.Create(
                    TransactionalEmailTemplates.PaymentSuccess,
                    guest.Email,
                    new PaymentSuccessEmail(
                        guest.FirstName,
                        booking.BookingCode,
                        room?.Name ?? "Reserved Room",
                        booking.Amount,
                        internalReference)));
            }

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ManualTransferConfirmationDto(
                booking.BookingCode,
                booking.PaymentStatus.ToString(),
                booking.Status.ToString(),
                internalReference,
                ManualTransferConfirmation.Method,
                confirmedAtUtc);
        });
    }

    public async Task<int> CancelExpiredUnconfirmedAsync(
        DateTime utcNow,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The expiration clock must be UTC.", nameof(utcNow));

        batchSize = Math.Clamp(batchSize, 1, 500);
        var cutoffUtc = BookingPaymentPolicy.GetExpirationCutoffUtc(utcNow);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            // SKIP LOCKED lets multiple API instances sweep safely without
            // waiting on one another or creating duplicate audit records.
            var expired = await _db.Bookings
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM bookings
                    WHERE "Status" = 'Pending'
                      AND "PaymentStatus" IN ('Unpaid', 'AwaitingVerification')
                      AND "CreatedAt" <= {cutoffUtc}
                    ORDER BY "CreatedAt"
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(cancellationToken);

            var guestIds = expired.Select(item => item.GuestId).Distinct();
            var roomIds = expired.Select(item => item.RoomId).Distinct();
            var guests = await _db.Guests
                .Where(item => guestIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, cancellationToken);
            var rooms = await _db.Rooms
                .Where(item => roomIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, cancellationToken);
            foreach (var booking in expired)
            {
                if (guests.TryGetValue(booking.GuestId, out var guest) &&
                    rooms.TryGetValue(booking.RoomId, out var room))
                {
                    _db.EmailOutboxMessages.Add(_emailOutbox.Create(
                        TransactionalEmailTemplates.Cancellation,
                        guest.Email,
                        new CancellationEmail(
                            $"{guest.FirstName} {guest.LastName}",
                            booking.BookingCode,
                            room.Name,
                            room.Category.ToString(),
                            booking.CheckIn,
                            "Payment was not confirmed within one hour, so the room hold was released.")));
                }

                var previousStatus = booking.Status;
                var history = JsonSerializer.Deserialize<List<object>>(
                                  string.IsNullOrWhiteSpace(booking.StatusHistoryJson)
                                      ? "[]"
                                      : booking.StatusHistoryJson)
                              ?? [];
                history.Add(new
                {
                    Status = BookingStatus.Cancelled.ToString(),
                    Timestamp = utcNow,
                    Actor = "System",
                    Reason = "Payment was not confirmed within one hour."
                });

                booking.Status = BookingStatus.Cancelled;
                booking.CancelledAtUtc = utcNow;
                booking.GuestAccessTokenRevokedAtUtc = utcNow;
                booking.PaymentCheckoutUrl = null;
                booking.StatusHistoryJson = JsonSerializer.Serialize(history);

                _db.AuditLogs.Add(new AuditLog
                {
                    Id = Guid.NewGuid(),
                    ProfileId = BookingPaymentPolicy.SystemActorId,
                    Action = "UNCONFIRMED_BOOKING_EXPIRED",
                    EntityType = "Booking",
                    EntityId = booking.Id.ToString(),
                    OldDataJson = JsonSerializer.Serialize(new
                    {
                        booking.BookingCode,
                        Status = previousStatus.ToString(),
                        PaymentStatus = booking.PaymentStatus.ToString(),
                        booking.CreatedAt,
                        ConfirmationDeadlineUtc = BookingPaymentPolicy.GetConfirmationDeadlineUtc(booking.CreatedAt)
                    }),
                    NewDataJson = JsonSerializer.Serialize(new
                    {
                        booking.BookingCode,
                        Status = booking.Status.ToString(),
                        Reason = "Payment was not confirmed within one hour.",
                        ExpiredAtUtc = utcNow
                    }),
                    CreatedAt = utcNow
                });
            }

            if (expired.Count > 0)
            {
                await _db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return expired.Count;
        });
    }

    public async Task<int> DeleteExpiredEmailVerificationsAsync(
        DateTime utcNow,
        int batchSize = 500,
        CancellationToken cancellationToken = default)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The cleanup clock must be UTC.", nameof(utcNow));

        batchSize = Math.Clamp(batchSize, 1, 1000);
        var deleteBeforeUtc = utcNow.Subtract(
            BookingEmailVerificationPolicy.RetentionAfterExpiry);
        return await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             DELETE FROM booking_email_verifications
             WHERE "Id" IN (
                 SELECT "Id"
                 FROM booking_email_verifications
                 WHERE "ExpiresAtUtc" <= {deleteBeforeUtc}
                 ORDER BY "ExpiresAtUtc", "Id"
                 LIMIT {batchSize}
                 FOR UPDATE SKIP LOCKED
             )
             """,
            cancellationToken);
    }

    public async Task<IEnumerable<Booking>> GetPendingRefundsAsync()
    {
        return await _db.Bookings
            .Include(b => b.Guest)
            .Include(b => b.Room)
            .Include(b => b.Quote).ThenInclude(quote => quote!.Lines)
            .Where(b => b.Status == BookingStatus.Cancelled &&
                        b.PaymentStatus == PaymentStatus.RefundPending)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();
    }

    public async Task<decimal> GetNetRevenueAsync(DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken cancellationToken = default)
    {
        var query = _db.Bookings.AsNoTracking()
            .Where(b => b.Status != BookingStatus.Cancelled && b.PaymentStatus == PaymentStatus.Paid);
        if (fromUtc.HasValue)
        {
            query = query.Where(b =>
                b.PaymentConfirmedAtUtc.HasValue &&
                b.PaymentConfirmedAtUtc.Value >= fromUtc.Value);
        }
        if (toUtc.HasValue)
        {
            query = query.Where(b =>
                b.PaymentConfirmedAtUtc.HasValue &&
                b.PaymentConfirmedAtUtc.Value < toUtc.Value);
        }
        return await query.SumAsync(b => b.Amount, cancellationToken);
    }

    public async Task<int> GetActiveGuestsCountAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Bookings.AsNoTracking()
            .CountAsync(b => b.Status == BookingStatus.CheckedIn, cancellationToken);
    }

    public async Task<int> GetOccupiedRoomNightsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        if (toUtc <= fromUtc) return 0;

        var stays = await _db.Bookings.AsNoTracking()
            .Where(booking =>
                booking.Status == BookingStatus.Confirmed ||
                booking.Status == BookingStatus.CheckedIn ||
                booking.Status == BookingStatus.CheckedOut)
            .Where(booking => booking.CheckIn < toUtc && booking.CheckOut > fromUtc)
            .Select(booking => new { booking.CheckIn, booking.CheckOut })
            .ToListAsync(cancellationToken);

        var firstDate = fromUtc.Date;
        var exclusiveLastDate = toUtc.Date;
        return stays.Sum(stay =>
        {
            var start = stay.CheckIn.Date > firstDate ? stay.CheckIn.Date : firstDate;
            var end = stay.CheckOut.Date < exclusiveLastDate
                ? stay.CheckOut.Date
                : exclusiveLastDate;
            return Math.Max(0, (end - start).Days);
        });
    }

    public async Task<decimal> GetAverageNightlyRateAsync(DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken cancellationToken = default)
    {
        var query = _db.Bookings.AsNoTracking()
            .Where(b => b.Status != BookingStatus.Cancelled &&
                        b.Status != BookingStatus.NoShow);
        if (fromUtc.HasValue) query = query.Where(b => b.CheckOut > fromUtc.Value);
        if (toUtc.HasValue) query = query.Where(b => b.CheckIn < toUtc.Value);

        var stays = await query
            .Select(booking => new
            {
                booking.Amount,
                booking.CheckIn,
                booking.CheckOut
            })
            .ToListAsync(cancellationToken);
        var totalNights = stays.Sum(stay =>
            Math.Max(1, (stay.CheckOut.Date - stay.CheckIn.Date).Days));
        return totalNights == 0
            ? 0m
            : stays.Sum(stay => stay.Amount) / totalNights;
    }

    public async Task<IReadOnlyList<RevenuePoint>> GetDailyRevenueDynamicsAsync(int days = 7, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var startDate = now.Date.AddDays(-(days - 1));
        var dailyTotals = await _db.Bookings.AsNoTracking()
            .Where(b => b.Status != BookingStatus.Cancelled &&
                        b.PaymentStatus == PaymentStatus.Paid &&
                        b.PaymentConfirmedAtUtc.HasValue &&
                        b.PaymentConfirmedAtUtc.Value >= startDate)
            .GroupBy(b => b.PaymentConfirmedAtUtc!.Value.Date)
            .Select(g => new { Date = g.Key, Total = g.Sum(b => b.Amount) })
            .ToListAsync(cancellationToken);

        var result = new List<RevenuePoint>(days);
        for (var i = days - 1; i >= 0; i--)
        {
            var date = now.AddDays(-i).Date;
            var match = dailyTotals.FirstOrDefault(d => d.Date == date);
            result.Add(new RevenuePoint(date.ToString("MMM dd", CultureInfo.InvariantCulture), match?.Total ?? 0m));
        }
        return result;
    }

    public async Task<IReadOnlyList<ActiveOperationDto>> GetActiveOperationsAsync(int limit = 5, CancellationToken cancellationToken = default)
    {
        return await _db.Bookings.AsNoTracking()
            .Include(b => b.Guest)
            .Include(b => b.Room)
            .Where(b => b.Status == BookingStatus.CheckedIn)
            .OrderByDescending(b => b.CreatedAt)
            .Take(limit)
            .Select(b => new ActiveOperationDto(
                ((b.Guest != null ? b.Guest.FirstName + " " + b.Guest.LastName : "Unknown")).Trim(),
                b.Guest != null ? (b.Guest.AvatarUrl ?? "") : "",
                b.BookingCode,
                b.Room != null ? b.Room.Category.ToString() : "Unknown",
                b.Room != null ? b.Room.RoomNumber : "N/A",
                "CHECKED IN",
                b.Amount,
                b.PaymentStatus.ToString()
            ))
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetCheckInsCountAsync(DateTime dateUtc, CancellationToken cancellationToken = default)
    {
        var day = dateUtc.Date;
        return await _db.Bookings.AsNoTracking()
            .CountAsync(b => b.CheckIn.Date == day && (b.Status == BookingStatus.CheckedIn || b.Status == BookingStatus.Confirmed), cancellationToken);
    }

    public async Task<int> GetCheckOutsCountAsync(DateTime dateUtc, CancellationToken cancellationToken = default)
    {
        var day = dateUtc.Date;
        return await _db.Bookings.AsNoTracking()
            .CountAsync(b => b.CheckOut.Date == day && (b.Status == BookingStatus.CheckedIn || b.Status == BookingStatus.CheckedOut), cancellationToken);
    }

    public async Task<int> GetTotalBookingsCountAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Bookings.AsNoTracking()
            .CountAsync(b => b.Status != BookingStatus.Cancelled, cancellationToken);
    }

    public async Task<PagedResult<Booking>> GetPagedBookingsAsync(
        int pageNumber = 1,
        int pageSize = 20,
        BookingStatus? status = null,
        PaymentStatus? paymentStatus = null,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedPage = Math.Max(1, pageNumber);
        var normalizedSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.Bookings
            .AsNoTracking()
            .Include(b => b.Room)
            .Include(b => b.Guest)
            .Include(b => b.Quote).ThenInclude(quote => quote!.Lines)
            .AsQueryable();

        if (status.HasValue)
        {
            query = query.Where(b => b.Status == status.Value);
        }

        if (paymentStatus.HasValue)
        {
            query = query.Where(b => b.PaymentStatus == paymentStatus.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(b =>
                b.BookingCode.Contains(s) ||
                (b.TransactionReference != null && b.TransactionReference.Contains(s)) ||
                (b.Guest != null && (b.Guest.FirstName.Contains(s) || b.Guest.LastName.Contains(s) || b.Guest.Email.Contains(s) || b.Guest.Phone.Contains(s))) ||
                (b.Room != null && b.Room.RoomNumber.Contains(s)));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(b => b.CreatedAt)
            .Skip((normalizedPage - 1) * normalizedSize)
            .Take(normalizedSize)
            .ToListAsync(cancellationToken);

        return PagedResult<Booking>.Create(items, totalCount, normalizedPage, normalizedSize);
    }
}
