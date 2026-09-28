using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.Infrastructure.Services;

// Uses existing audit/folio tables: no destructive data migration or balance rewrites.
public sealed class PaymentReviewService(
    MooreHotelsDbContext db, IInventoryService inventory, IHotelTimeService hotelTime, IEmailOutbox outbox)
{
    public async Task<IReadOnlyList<PaymentReviewItem>> GetQueueAsync(Guid actorId, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == actorId && u.Status == ProfileStatus.Active &&
            (u.Role == UserRole.Admin || u.Role == UserRole.Manager), ct))
            throw new UnauthorizedAccessException("An active manager or administrator is required.");
        var bookings = await db.Bookings.AsNoTracking().Include(b => b.Guest)
            .Where(b => b.PaymentStatus == PaymentStatus.PaymentReported)
            .OrderBy(b => b.CreatedAt).Take(500).ToListAsync(ct);
        var ids = bookings.Select(b => b.Id.ToString()).ToArray();
        var reports = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "PAYMENT_REPORTED" && ids.Contains(a.EntityId))
            .Select(a => new { a.EntityId, a.CreatedAt }).ToListAsync(ct);
        var now = DateTime.UtcNow;
        return bookings.Select(b =>
        {
            var reported = reports.Where(a => a.EntityId == b.Id.ToString())
                .Select(a => (DateTime?)a.CreatedAt).Min() ?? b.CreatedAt;
            return new PaymentReviewItem(b.BookingCode,
                $"{b.Guest?.FirstName} {b.Guest?.LastName}", b.Status.ToString(), b.Amount, b.Currency,
                reported, now >= reported.AddHours(1), b.Status == BookingStatus.Pending);
        }).ToArray();
    }

    public Task<PaymentReviewResult> ReportAsync(string code, string? token, Guid? userId, CancellationToken ct) =>
        UnderLockAsync(code, async b =>
        {
            var owner = userId.HasValue && await db.Users.AnyAsync(u => u.Id == userId &&
                u.GuestId == b.GuestId && u.Status == ProfileStatus.Active, ct);
            if (!owner && !BookingGuestAccessPolicy.IsValid(b, token, DateTime.UtcNow))
                throw new UnauthorizedAccessException("Use your secure booking link or sign in to report payment.");
            if (b.PaymentMethod != PaymentMethod.DirectTransfer)
                throw new BadRequestException("Only direct bank transfers can be reported here.");
            if (b.PaymentStatus == PaymentStatus.PaymentReported)
                return Result(b, "Your payment report is already awaiting staff review.");
            if (b.PaymentStatus == PaymentStatus.Paid)
                return Result(b, "Payment has already been verified.");
            if (b.Status is not (BookingStatus.Pending or BookingStatus.Cancelled) ||
                b.PaymentStatus is not (PaymentStatus.Unpaid or PaymentStatus.AwaitingVerification))
                throw new BadRequestException("Contact the hotel to reconcile this payment.");
            if (await db.AuditLogs.AnyAsync(a => a.EntityId == b.Id.ToString() &&
                a.Action == "TRANSFER_REVIEW_REJECT", ct))
                throw new BadRequestException("This report has been reviewed. Contact the hotel with new evidence.");

            var inventoryKey = BitConverter.ToInt64(b.RoomTypeId.ToByteArray(), 0);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({inventoryKey})", ct);

            // Availability already treats expired holds as released. Never revive
            // such a hold just because the guest reports a payment late.
            if (BookingPaymentPolicy.IsExpiredUnconfirmed(b.Status, b.PaymentStatus, b.CreatedAt, DateTime.UtcNow))
            {
                b.Status = BookingStatus.Cancelled;
                b.CancelledAtUtc = DateTime.UtcNow;
                Audit(b, userId ?? Guid.Empty, "PAYMENT_REPORT_AFTER_EXPIRY", new { b.BookingCode });
            }
            b.PaymentStatus = PaymentStatus.PaymentReported;
            b.PaymentCheckoutUrl = null;
            Audit(b, userId ?? Guid.Empty, "PAYMENT_REPORTED", new { b.BookingCode, RoomHeld = b.Status == BookingStatus.Pending });
            db.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                BookingCode = b.BookingCode,
                Title = "Bank transfer requires verification",
                Message = $"{b.BookingCode}: payment reported, not verified. " +
                    (b.Status == BookingStatus.Pending ? "Room held pending review." : "Hold released; reconcile before promising a room."),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
            return Result(b, b.Status == BookingStatus.Pending
                ? "Payment reported. Your room is held during staff verification."
                : "Payment reported after the hold ended. Contact the hotel; a room is not guaranteed yet.");
        }, ct);

    public Task<PaymentReviewResult> ResolveAsync(string code, ResolveTransferRequest request, Guid actorId, CancellationToken ct) =>
        UnderLockAsync(code, async b =>
        {
            var actor = await db.Users.SingleOrDefaultAsync(u => u.Id == actorId, ct);
            if (actor is null || actor.Status != ProfileStatus.Active ||
                actor.Role is not (UserRole.Admin or UserRole.Manager))
                throw new UnauthorizedAccessException("An active manager or administrator must verify the bank credit.");
            if (request.ConfirmationText != "VERIFY" || request.Reason.Trim().Length < 10 || request.Reason.Length > 500)
                throw new BadRequestException("Enter VERIFY and a review reason of 10–500 characters.");
            var decision = request.Decision;
            if (decision is not ("Confirm" or "Refund" or "Reject"))
                throw new BadRequestException("Choose Confirm, Refund, or Reject.");
            if (b.PaymentMethod != PaymentMethod.DirectTransfer ||
                b.Status is not (BookingStatus.Pending or BookingStatus.Cancelled) ||
                b.PaymentStatus is not (PaymentStatus.PaymentReported or PaymentStatus.AwaitingVerification or PaymentStatus.Unpaid))
                throw new BadRequestException("This reservation is not eligible for payment review or was already resolved.");
            if (decision == "Reject" && await db.AuditLogs.AnyAsync(a => a.EntityId == b.Id.ToString() && a.Action == "TRANSFER_REVIEW_REJECT", ct))
                throw new BadRequestException("This report was already rejected. Verify any new bank credit before arranging a refund.");
            var folio = await db.Folios.Include(f => f.Entries).SingleAsync(f => f.BookingId == b.Id, ct);
            if (folio.Status != FolioStatus.Open || FolioAccounting.Calculate(folio.Entries).Payments != 0)
                throw new BadRequestException("This folio already has payments or is closed. Reconcile it with the accounts team.");
            var now = DateTime.UtcNow;
            if (decision == "Reject")
            {
                if (b.PaymentStatus != PaymentStatus.PaymentReported)
                    throw new BadRequestException("Only an outstanding payment report can be rejected.");
                ReleaseBalance(b, folio, actorId, now);
                b.Status = BookingStatus.Cancelled;
                b.CancelledAtUtc = now;
                b.PaymentStatus = PaymentStatus.Unpaid;
                Audit(b, actorId, "TRANSFER_REVIEW_REJECT", new { request.Reason, NoBankCreditVerified = true });
                await AddNoticeAsync(b, "Payment report reviewed: no bank credit verified. Contact the hotel if you have new evidence.", ct);
                await db.SaveChangesAsync(ct);
                return Result(b, "Report rejected and room released. No payment or refund was recorded.");
            }

            var bankReference = request.BankReference?.Trim().ToUpperInvariant();
            if (bankReference is null || bankReference.Length < 6 || bankReference.Length > 120 ||
                bankReference.Any(char.IsControl) ||
                request.Amount is not > 0 || request.Amount > 99999999999999m ||
                decimal.Round(request.Amount.Value, 2) != request.Amount)
                throw new BadRequestException("Enter the unique bank statement reference and exact verified credit amount (two decimal places maximum).");
            if (b.Currency != "NGN") throw new BadRequestException("Only verified NGN bank credits can be reconciled here.");
            var creditKey = "bank-credit:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bankReference)));
            // Global lock + unique ledger key prevent reuse across different bookings.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({creditKey}, 0))", ct);
            if (await db.FolioEntries.AnyAsync(e => e.IdempotencyKey == creditKey, ct))
                throw new BadRequestException("This bank credit has already been recorded. Do not apply it twice.");

            if (decision == "Confirm")
            {
                if (request.Amount != b.Amount)
                    throw new BadRequestException("Confirmation requires the full reservation amount. Reconcile partial or excess payments separately; do not invent a matching amount.");
                if (b.CheckIn < hotelTime.GetLocalDayStartUtc(hotelTime.Today) || b.CheckOut <= now)
                    throw new BadRequestException("Past stays cannot be restored. Arrange a refund or a new reservation.");
                await VerifyInventoryAsync(b, ct);
                // Only automatically-expired reservations may be restored. Other
                // cancellations may carry penalties or separate commitments.
                if (b.Status == BookingStatus.Cancelled && !await db.AuditLogs.AnyAsync(a =>
                    a.EntityId == b.Id.ToString() &&
                    (a.Action == "UNCONFIRMED_BOOKING_EXPIRED" || a.Action == "PAYMENT_REPORT_AFTER_EXPIRY"), ct))
                    throw new BadRequestException("Only an automatically expired reservation can be restored. Arrange a refund or a new booking.");
                var release = folio.Entries.Where(e => e.SourceType == "Expiration" && e.Direction == FolioEntryDirection.Credit).ToArray();
                foreach (var entry in release)
                    AddEntry(folio, FolioAccounting.NewEntry(folio, FolioEntryType.Void, FolioEntryDirection.Debit,
                        entry.Amount, "Restore expired reservation charge", "PaymentReview", b.Id.ToString(),
                        $"restore:{entry.Id:N}", now, actorId, reversesEntryId: entry.Id));
                if (FolioAccounting.Calculate(folio.Entries).AmountDue != request.Amount)
                    throw new BadRequestException("The folio does not match this credit. Accounts review is required.");
                b.Status = BookingStatus.Confirmed;
                b.CancelledAtUtc = null;
                // Do not reactivate any revoked guest token. Guest can request a fresh link.
                b.PaymentStatus = PaymentStatus.Paid;
            }
            else
            {
                ReleaseBalance(b, folio, actorId, now);
                b.Status = BookingStatus.Cancelled;
                b.CancelledAtUtc ??= now;
                b.PaymentStatus = PaymentStatus.RefundPending;
            }
            AddEntry(folio, FolioAccounting.NewEntry(folio, FolioEntryType.Payment, FolioEntryDirection.Credit,
                request.Amount.Value, "Bank credit verified during payment review", "PaymentReview", b.Id.ToString(),
                creditKey, now, actorId, bankReference));
            b.TransactionReference = bankReference;
            b.PaymentConfirmationMethod = "BankStatementReview";
            b.PaymentConfirmedByUserId = actorId;
            b.PaymentConfirmedAtUtc = now;
            Audit(b, actorId, "TRANSFER_REVIEW_" + decision.ToUpperInvariant(), new
            { request.Amount, request.Reason, BankReference = bankReference, b.Status, b.PaymentStatus });
            if (decision == "Confirm")
            {
                var guest = await db.Guests.SingleAsync(g => g.Id == b.GuestId, ct);
                var roomName = await db.Rooms.Where(r => r.Id == b.RoomId).Select(r => r.Name).SingleOrDefaultAsync(ct);
                db.EmailOutboxMessages.Add(outbox.Create(TransactionalEmailTemplates.PaymentSuccess, guest.Email,
                    new PaymentSuccessEmail(guest.FirstName, b.BookingCode, roomName ?? "Room assignment pending", request.Amount.Value, bankReference), b.GuestId));
            }
            else await AddNoticeAsync(b, "Bank credit verified. Your reservation is cancelled and a refund is pending staff processing; the refund has not yet been sent.", ct);
            await db.SaveChangesAsync(ct);
            return Result(b, decision == "Confirm" ? "Bank credit verified and reservation confirmed."
                : "Verified credit recorded. Complete the existing refund approval and payment workflow; no money has been sent automatically.");
        }, ct);

    private async Task VerifyInventoryAsync(Booking b, CancellationToken ct)
    {
        var key = BitConverter.ToInt64(b.RoomTypeId.ToByteArray(), 0);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
        if (await db.ReservationRooms.CountAsync(r => r.BookingId == b.Id && r.RoomTypeId == b.RoomTypeId, ct) != b.RoomQuantity)
            throw new BadRequestException("Reservation inventory is incomplete. Reconcile the booking before restoring it.");
        var start = DateOnly.FromDateTime(hotelTime.ToHotelLocalTime(b.CheckIn));
        var end = DateOnly.FromDateTime(hotelTime.ToHotelLocalTime(b.CheckOut));
        var availability = await inventory.GetAvailabilityExcludingBookingAsync(b.RoomTypeId, start, end, b.RoomQuantity, b.Id, ct);
        if (!availability.Available) throw new BadRequestException("Room inventory is no longer available. Arrange an alternative with the guest or select Refund.");
        var assigned = await db.ReservationRooms.Where(r => r.BookingId == b.Id && r.AssignedRoomId != null)
            .Select(r => r.AssignedRoomId!.Value).ToListAsync(ct);
        if (b.RoomId.HasValue && !assigned.Contains(b.RoomId.Value)) assigned.Add(b.RoomId.Value);
        var cutoff = BookingPaymentPolicy.GetExpirationCutoffUtc(DateTime.UtcNow);
        if (await db.Rooms.AnyAsync(r => assigned.Contains(r.Id) &&
                (!r.IsOnline || r.Status == RoomStatus.Maintenance || r.Status == RoomStatus.OutOfOrder), ct) ||
            await db.RoomInventoryClosures.AnyAsync(c => c.IsActive && c.RoomId.HasValue && assigned.Contains(c.RoomId.Value) && c.StartDate < end && c.EndDate > start, ct) ||
            await db.Bookings.AnyAsync(other => other.Id != b.Id &&
                ((other.RoomId.HasValue && assigned.Contains(other.RoomId.Value)) ||
                 other.ReservationRooms.Any(r => r.AssignedRoomId.HasValue && assigned.Contains(r.AssignedRoomId.Value))) &&
                other.Status != BookingStatus.Cancelled && other.Status != BookingStatus.NoShow && other.Status != BookingStatus.CheckedOut &&
                !(other.Status == BookingStatus.Pending && (other.PaymentStatus == PaymentStatus.Unpaid || other.PaymentStatus == PaymentStatus.AwaitingVerification) && other.CreatedAt <= cutoff) &&
                other.CheckIn < b.CheckOut && other.CheckOut > b.CheckIn, ct))
            throw new BadRequestException("The assigned room is no longer available. Arrange an alternative with the guest or select Refund.");
    }

    private async Task<PaymentReviewResult> UnderLockAsync(string code, Func<Booking, Task<PaymentReviewResult>> action, CancellationToken ct)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var b = await db.Bookings.FromSqlInterpolated($"SELECT * FROM bookings WHERE \"BookingCode\" = {normalized} FOR UPDATE")
                .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Booking not found.");
            var result = await action(b);
            await tx.CommitAsync(ct);
            return result;
        });
    }

    private void ReleaseBalance(Booking b, Folio folio, Guid actor, DateTime now)
    {
        var due = FolioAccounting.Calculate(folio.Entries).AmountDue;
        if (due > 0) AddEntry(folio, FolioAccounting.NewEntry(folio, FolioEntryType.Credit, FolioEntryDirection.Credit,
            due, "Release reservation after transfer review", "PaymentReview", b.Id.ToString(), $"review-release:{b.Id:N}", now, actor));
    }

    private void AddEntry(Folio folio, FolioEntry entry)
    {
        folio.Entries.Add(entry);
        db.FolioEntries.Add(entry);
    }

    private void Audit(Booking b, Guid actor, string action, object data) => db.AuditLogs.Add(new AuditLog
    {
        Id = Guid.NewGuid(),
        ProfileId = actor,
        Action = action,
        EntityType = "Booking",
        EntityId = b.Id.ToString(),
        NewDataJson = JsonSerializer.Serialize(data),
        CreatedAt = DateTime.UtcNow
    });

    private async Task AddNoticeAsync(Booking b, string reason, CancellationToken ct)
    {
        var guest = await db.Guests.SingleAsync(g => g.Id == b.GuestId, ct);
        var room = await db.Rooms.SingleOrDefaultAsync(r => r.Id == b.RoomId, ct);
        var roomType = await db.RoomTypes.SingleAsync(t => t.Id == b.RoomTypeId, ct);
        db.EmailOutboxMessages.Add(outbox.Create(TransactionalEmailTemplates.Cancellation, guest.Email,
            new CancellationEmail(guest.FirstName, b.BookingCode, room?.Name ?? $"{roomType.Name} (room assignment pending)",
                roomType.Category.ToString(), b.CheckIn, reason), b.GuestId));
    }

    private static PaymentReviewResult Result(Booking b, string message) => new(b.BookingCode, b.Status.ToString(), b.PaymentStatus.ToString(), message);
}
