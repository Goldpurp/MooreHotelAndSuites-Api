using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Exceptions;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Domain.Common;
using MooreHotels.Infrastructure.Persistence;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace MooreHotels.Infrastructure.Repositories;

public class BookingRepository : IBookingRepository
{
    private const int BookingCodeUpperBound = 1_000_000;
    private const int BookingCodeAllocationAttempts = 64;

    private readonly MooreHotelsDbContext _db;
    public BookingRepository(MooreHotelsDbContext db) => _db = db;

    public async Task<Booking?> GetByIdAsync(Guid id) => 
        await _db.Bookings.Include(b => b.Room).Include(b => b.Guest).FirstOrDefaultAsync(b => b.Id == id);

    public async Task<Booking?> GetByCodeAsync(string code) => 
        await _db.Bookings.Include(b => b.Room).Include(b => b.Guest).FirstOrDefaultAsync(b => b.BookingCode == code);

    public async Task<Booking?> GetByPaymentReferenceAsync(string paymentReference) =>
        await _db.Bookings
            .Include(booking => booking.Room)
            .Include(booking => booking.Guest)
            .FirstOrDefaultAsync(booking => booking.TransactionReference == paymentReference);

    public async Task<IEnumerable<Booking>> GetAllAsync() => 
        await _db.Bookings
            .AsNoTracking()
            .Include(b => b.Room)
            .Include(b => b.Guest)
            .OrderByDescending(b => b.CreatedAt)
            .Take(2000)
            .ToListAsync();

    public async Task<IEnumerable<Booking>> GetByGuestIdAsync(string guestId) =>
        await _db.Bookings
            .AsNoTracking()
            .Include(booking => booking.Room)
            .Include(booking => booking.Guest)
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

    public async Task AddAsync(Booking booking)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            // Serialize booking creation per room across every API instance.
            // This closes the race between the availability check and INSERT
            // without locking unrelated rooms or holding a long transaction.
            var advisoryKey = BitConverter.ToInt64(booking.RoomId.ToByteArray(), 0);
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({advisoryKey})");

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
                existing.CheckOut > booking.CheckIn);

            if (conflict)
            {
                throw new BadRequestException("This room was just reserved for the selected dates. Please choose another room or date.");
            }

            await _db.Bookings.AddAsync(booking);
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
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
        CancellationToken cancellationToken = default) =>
        (await CancelExpiredUnconfirmedWithNotificationsAsync(
            utcNow,
            batchSize,
            cancellationToken)).Count;

    public async Task<IReadOnlyList<ExpiredBookingNotification>>
        CancelExpiredUnconfirmedWithNotificationsAsync(
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
            var notifications =
                new List<ExpiredBookingNotification>(expired.Count);

            foreach (var booking in expired)
            {
                if (guests.TryGetValue(booking.GuestId, out var guest) &&
                    rooms.TryGetValue(booking.RoomId, out var room))
                {
                    notifications.Add(new ExpiredBookingNotification(
                        guest.Email,
                        $"{guest.FirstName} {guest.LastName}",
                        booking.BookingCode,
                        room.Name,
                        room.Category.ToString(),
                        booking.CheckIn));
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
            return (IReadOnlyList<ExpiredBookingNotification>)notifications;
        });
    }

    public async Task<IEnumerable<Booking>> GetPendingRefundsAsync()
{
    return await _db.Bookings
        .Include(b => b.Guest)
        .Include(b => b.Room)
        .Where(b => b.Status == BookingStatus.Cancelled && 
                    b.PaymentStatus == PaymentStatus.RefundPending)
        .OrderByDescending(b => b.CreatedAt)
        .ToListAsync();
}

}
