using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.Infrastructure.Services;

public sealed class FolioService : IFolioService
{
    private readonly MooreHotelsDbContext _db;

    public FolioService(MooreHotelsDbContext db) => _db = db;

    public async Task<FolioDto> GetByBookingCodeAsync(
        string bookingCode,
        CancellationToken cancellationToken = default)
    {
        var folio = await FolioQuery()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Booking != null && item.Booking.BookingCode == NormalizeBookingCode(bookingCode),
                cancellationToken)
            ?? throw new NotFoundException("Booking folio not found.");
        return ToDto(folio);
    }

    public Task<FolioDto> PostChargeAsync(
        string bookingCode,
        PostFolioChargeRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        if (request.Type is not (FolioEntryType.AddOnCharge or FolioEntryType.Tax or
            FolioEntryType.Fee or FolioEntryType.Adjustment))
            throw new BadRequestException("Staff charges must be an add-on, tax, fee, or debit adjustment.");
        return MutateAsync(
            bookingCode,
            request.IdempotencyKey,
            actorId,
            async (booking, folio, key, now) =>
            {
                EnsureOpen(folio);
                if (booking.Status is BookingStatus.Cancelled or BookingStatus.CheckedOut or BookingStatus.NoShow)
                    throw new BadRequestException("Charges cannot be posted to a closed stay.");
                var amount = RequirePositiveMoney(request.Amount, "Charge amount");
                var entry = FolioAccounting.NewEntry(
                    folio,
                    request.Type,
                    FolioEntryDirection.Debit,
                    amount,
                    RequireText(request.Description, "Charge description", 200),
                    RequireText(request.SourceType, "Charge source", 80),
                    Clean(request.SourceId),
                    key,
                    now,
                    actorId,
                    notes: Clean(request.Notes));
                _db.FolioEntries.Add(entry);
                booking.Amount = FolioAccounting.Money(booking.Amount + entry.Amount);
                RecalculatePaymentStatus(booking, folio.Entries);
                await AddAuditAsync(booking, actorId, "FOLIO_CHARGE_POSTED", entry, now);
            },
            cancellationToken);
    }

    public Task<FolioDto> PostPaymentAsync(
        string bookingCode,
        PostFolioPaymentRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            bookingCode,
            request.IdempotencyKey,
            actorId,
            async (booking, folio, key, now) =>
            {
                EnsureOpen(folio);
                if (booking.Status is BookingStatus.Cancelled or BookingStatus.CheckedOut or BookingStatus.NoShow)
                    throw new BadRequestException("Payments cannot be posted to a closed reservation.");
                var current = FolioAccounting.Calculate(folio.Entries);
                if (current.AmountDue <= 0)
                    throw new BadRequestException("This folio has no outstanding balance.");
                var amount = RequirePositiveMoney(request.Amount, "Payment amount");
                if (amount > current.AmountDue)
                    throw new BadRequestException("A staff-posted payment cannot exceed the outstanding balance.");
                var reference = RequireText(request.ExternalReference, "Payment reference", 160).ToUpperInvariant();
                var method = NormalizePaymentMethod(request.Method);
                var entry = FolioAccounting.NewEntry(
                    folio,
                    FolioEntryType.Payment,
                    FolioEntryDirection.Credit,
                    amount,
                    $"{method} payment",
                    "StaffPayment",
                    booking.Id.ToString(),
                    key,
                    now,
                    actorId,
                    reference,
                    notes: Clean(request.Notes));
                _db.FolioEntries.Add(entry);
                RecalculatePaymentStatus(booking, folio.Entries);
                var updatedBalance = FolioAccounting.Calculate(folio.Entries);
                var confirmedDeposit = FolioAccounting.Money(
                    (booking.RoomSubtotal - booking.DiscountAmount + booking.TaxAmount + booking.FeeAmount) *
                    booking.DepositPercent / 100m);
                if (request.ConfirmReservation &&
                    updatedBalance.Payments - updatedBalance.Refunds >= confirmedDeposit &&
                    booking.Status == BookingStatus.Pending)
                {
                    booking.Status = BookingStatus.Confirmed;
                    booking.PaymentConfirmedAtUtc = now;
                    booking.PaymentConfirmedByUserId = actorId;
                    booking.PaymentConfirmationMethod = $"Folio{method}";
                    booking.TransactionReference ??= reference;
                    booking.PaymentCheckoutUrl = null;
                }
                await AddAuditAsync(booking, actorId, "FOLIO_PAYMENT_POSTED", entry, now);
            },
            cancellationToken);

    public Task<FolioDto> PostCreditAsync(
        string bookingCode,
        PostFolioCreditRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            bookingCode,
            request.IdempotencyKey,
            actorId,
            async (booking, folio, key, now) =>
            {
                EnsureOpen(folio);
                var current = FolioAccounting.Calculate(folio.Entries);
                var amount = RequirePositiveMoney(request.Amount, "Credit amount");
                if (amount > current.AmountDue)
                    throw new BadRequestException("A discretionary credit cannot exceed the outstanding balance.");
                var entry = FolioAccounting.NewEntry(
                    folio,
                    FolioEntryType.Credit,
                    FolioEntryDirection.Credit,
                    amount,
                    RequireText(request.Reason, "Credit reason", 200),
                    "StaffCredit",
                    booking.Id.ToString(),
                    key,
                    now,
                    actorId,
                    notes: Clean(request.Notes));
                _db.FolioEntries.Add(entry);
                booking.Amount = Math.Max(0, FolioAccounting.Money(booking.Amount - entry.Amount));
                RecalculatePaymentStatus(booking, folio.Entries);
                await AddAuditAsync(booking, actorId, "FOLIO_CREDIT_POSTED", entry, now);
            },
            cancellationToken);

    public Task<FolioDto> VoidEntryAsync(
        string bookingCode,
        Guid entryId,
        VoidFolioEntryRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            bookingCode,
            request.IdempotencyKey,
            actorId,
            async (booking, folio, key, now) =>
            {
                EnsureOpen(folio);
                var original = folio.Entries.SingleOrDefault(entry => entry.Id == entryId)
                               ?? throw new NotFoundException("Folio entry not found.");
                if (original.Type == FolioEntryType.Void ||
                    folio.Entries.Any(entry => entry.ReversesEntryId == original.Id))
                    throw new BadRequestException("This folio entry is already a void or has already been reversed.");
                var entry = FolioAccounting.NewEntry(
                    folio,
                    FolioEntryType.Void,
                    original.Direction == FolioEntryDirection.Debit
                        ? FolioEntryDirection.Credit
                        : FolioEntryDirection.Debit,
                    original.Amount,
                    $"Void: {original.Description}",
                    "FolioVoid",
                    original.Id.ToString(),
                    key,
                    now,
                    actorId,
                    reversesEntryId: original.Id,
                    notes: RequireText(request.Reason, "Void reason", 500));
                _db.FolioEntries.Add(entry);
                if (original.Type is FolioEntryType.RoomCharge or FolioEntryType.AddOnCharge or
                    FolioEntryType.Tax or FolioEntryType.Fee or FolioEntryType.Adjustment)
                {
                    booking.Amount = original.Direction == FolioEntryDirection.Debit
                        ? Math.Max(0, FolioAccounting.Money(booking.Amount - original.Amount))
                        : FolioAccounting.Money(booking.Amount + original.Amount);
                }
                else if (original.Type is FolioEntryType.Discount or FolioEntryType.Credit)
                {
                    booking.Amount = FolioAccounting.Money(booking.Amount + original.Amount);
                }
                else if (original.Type == FolioEntryType.Refund)
                {
                    booking.RefundAmount = Math.Max(
                        0,
                        FolioAccounting.Money((booking.RefundAmount ?? 0) - original.Amount));
                }
                RecalculatePaymentStatus(booking, folio.Entries);
                await AddAuditAsync(booking, actorId, "FOLIO_ENTRY_VOIDED", entry, now);
            },
            cancellationToken);

    public Task<FolioDto> CloseAsync(
        string bookingCode,
        Guid actorId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            bookingCode,
            $"close:{NormalizeBookingCode(bookingCode)}",
            actorId,
            (booking, folio, _, now) =>
            {
                if (folio.Status == FolioStatus.Closed) return Task.CompletedTask;
                if (booking.Status is not (BookingStatus.CheckedOut or BookingStatus.Cancelled or BookingStatus.NoShow))
                    throw new BadRequestException("A folio can close only after checkout, cancellation, or no-show closure.");
                var balance = FolioAccounting.Calculate(folio.Entries);
                if (balance.Balance != 0)
                    throw new BadRequestException("Settle the outstanding balance or guest credit before closing the folio.");
                folio.Status = FolioStatus.Closed;
                folio.ClosedAtUtc = now;
                folio.ClosedByUserId = actorId;
                _db.AuditLogs.Add(new AuditLog
                {
                    Id = Guid.NewGuid(),
                    ProfileId = actorId,
                    Action = "FOLIO_CLOSED",
                    EntityType = "Folio",
                    EntityId = folio.Id.ToString(),
                    NewDataJson = JsonSerializer.Serialize(new { folio.BookingId, ClosedAtUtc = now }),
                    CreatedAt = now
                });
                return Task.CompletedTask;
            },
            cancellationToken,
            idempotencyIsEntry: false);

    public async Task ApplyCancellationCreditAsync(
        Booking booking,
        string reason,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        var now = booking.CancelledAtUtc ?? DateTime.UtcNow;
        var freeCancellationDeadline = booking.CheckIn.AddHours(-booking.FreeCancellationHours);
        var penaltyPercent = now <= freeCancellationDeadline
            ? 0m
            : booking.CancellationPenaltyPercent;
        booking.CancellationPenaltyAmount = await ApplyTerminationPolicyAsync(
            booking,
            penaltyPercent,
            "cancellation",
            "Reservation cancellation credit",
            reason,
            actorId,
            now,
            cancellationToken);
    }

    public async Task ApplyNoShowPolicyAsync(
        Booking booking,
        string reason,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        booking.NoShowPenaltyAmount = await ApplyTerminationPolicyAsync(
            booking,
            booking.NoShowPenaltyPercent,
            "no-show",
            "No-show policy credit",
            reason,
            actorId,
            DateTime.UtcNow,
            cancellationToken);
    }

    private async Task<decimal> ApplyTerminationPolicyAsync(
        Booking booking,
        decimal penaltyPercent,
        string keySuffix,
        string description,
        string reason,
        Guid actorId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var folio = await _db.Folios.Include(item => item.Entries)
            .SingleAsync(item => item.BookingId == booking.Id, cancellationToken);
        if (folio.Status == FolioStatus.Closed)
            return keySuffix == "no-show" ? booking.NoShowPenaltyAmount : booking.CancellationPenaltyAmount;

        var billedBalance = FolioAccounting.Money(folio.Entries
            .Where(entry => entry.Type is not (FolioEntryType.Payment or FolioEntryType.Refund))
            .Sum(entry => entry.Direction == FolioEntryDirection.Debit ? entry.Amount : -entry.Amount));
        var penalty = Math.Min(
            Math.Max(0m, billedBalance),
            FolioAccounting.Money(booking.RoomSubtotal * Math.Clamp(penaltyPercent, 0m, 100m) / 100m));
        var creditAmount = FolioAccounting.Money(Math.Max(0m, billedBalance - penalty));
        var key = $"booking:{booking.Id:N}:{keySuffix}-credit";
        if (creditAmount > 0 && !folio.Entries.Any(entry => entry.IdempotencyKey == key))
        {
            var entry = FolioAccounting.NewEntry(
                folio,
                FolioEntryType.Credit,
                FolioEntryDirection.Credit,
                creditAmount,
                description,
                keySuffix == "no-show" ? "NoShow" : "Cancellation",
                booking.Id.ToString(),
                key,
                now,
                actorId == BookingPaymentPolicy.SystemActorId ? null : actorId,
                notes: Clean(reason));
            _db.FolioEntries.Add(entry);
        }
        RecalculatePaymentStatus(booking, folio.Entries);
        await _db.SaveChangesAsync(cancellationToken);
        return penalty;
    }

    public async Task ApplyRefundAsync(
        Booking booking,
        decimal amount,
        string reference,
        string channel,
        string? notes,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        var folio = await _db.Folios.Include(item => item.Entries)
            .SingleAsync(item => item.BookingId == booking.Id, cancellationToken);
        EnsureOpen(folio);
        var normalizedReference = RequireText(reference, "Refund reference", 160).ToUpperInvariant();
        var existing = folio.Entries.SingleOrDefault(entry => entry.ExternalReference == normalizedReference);
        if (existing is not null)
        {
            if (existing.Type != FolioEntryType.Refund || existing.Amount != FolioAccounting.Money(amount))
                throw new ConflictException("The refund reference is already used for different folio data.");
            return;
        }
        var balance = FolioAccounting.Calculate(folio.Entries);
        var refundAmount = FolioAccounting.Money(amount);
        if (refundAmount <= 0 || refundAmount > balance.GuestCredit)
            throw new BadRequestException("Refund amount exceeds the unsettled guest credit.");
        var entry = FolioAccounting.NewEntry(
            folio,
            FolioEntryType.Refund,
            FolioEntryDirection.Debit,
            refundAmount,
            $"{channel} refund",
            "Refund",
            booking.Id.ToString(),
            $"refund:{BookingGuestAccess.Hash(normalizedReference)}",
            DateTime.UtcNow,
            actorId,
            normalizedReference,
            notes: Clean(notes));
        _db.FolioEntries.Add(entry);
        RecalculatePaymentStatus(booking, folio.Entries);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<FolioDto> MutateAsync(
        string bookingCode,
        string clientIdempotencyKey,
        Guid actorId,
        Func<Booking, Folio, string, DateTime, Task> mutation,
        CancellationToken cancellationToken,
        bool idempotencyIsEntry = true)
    {
        var normalizedCode = NormalizeBookingCode(bookingCode);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
            var booking = await _db.Bookings
                .FromSqlInterpolated($"SELECT * FROM bookings WHERE \"BookingCode\" = {normalizedCode} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException("Booking not found.");
            var folio = await _db.Folios.Include(item => item.Entries)
                .SingleOrDefaultAsync(item => item.BookingId == booking.Id, cancellationToken)
                ?? throw new NotFoundException("Booking folio not found.");
            folio.Booking = booking;
            var key = BuildIdempotencyKey(folio.Id, clientIdempotencyKey);
            if (idempotencyIsEntry && folio.Entries.Any(entry => entry.IdempotencyKey == key))
            {
                await transaction.CommitAsync(cancellationToken);
                return ToDto(folio);
            }
            await mutation(booking, folio, key, DateTime.UtcNow);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToDto(folio);
        });
    }

    private IQueryable<Folio> FolioQuery() => _db.Folios
        .Include(item => item.Booking)
        .Include(item => item.Entries.OrderBy(entry => entry.PostedAtUtc).ThenBy(entry => entry.Id));

    private static void RecalculatePaymentStatus(Booking booking, IEnumerable<FolioEntry> entries)
    {
        var balance = FolioAccounting.Calculate(entries);
        var netPayments = balance.Payments - balance.Refunds;
        if (balance.GuestCredit > 0)
        {
            booking.PaymentStatus = PaymentStatus.RefundPending;
            return;
        }
        if (booking.Status == BookingStatus.Cancelled)
        {
            booking.PaymentStatus = balance.Refunds > 0
                    ? PaymentStatus.Refunded
                    : netPayments > 0
                        ? PaymentStatus.PartiallyPaid
                        : PaymentStatus.Unpaid;
            return;
        }
        booking.PaymentStatus = netPayments <= 0
            ? booking.PaymentMethod == PaymentMethod.DirectTransfer
                ? PaymentStatus.AwaitingVerification
                : PaymentStatus.Unpaid
            : balance.AmountDue == 0
                ? PaymentStatus.Paid
                : PaymentStatus.PartiallyPaid;
    }

    private Task AddAuditAsync(
        Booking booking,
        Guid actorId,
        string action,
        FolioEntry entry,
        DateTime now)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ProfileId = actorId,
            Action = action,
            EntityType = "Folio",
            EntityId = entry.FolioId.ToString(),
            NewDataJson = JsonSerializer.Serialize(new
            {
                booking.BookingCode,
                EntryId = entry.Id,
                Type = entry.Type.ToString(),
                Direction = entry.Direction.ToString(),
                entry.Amount,
                entry.Currency,
                entry.SourceType,
                entry.SourceId,
                entry.ExternalReference,
                entry.ReversesEntryId
            }),
            CreatedAt = now
        });
        return Task.CompletedTask;
    }

    private static FolioDto ToDto(Folio folio)
    {
        var balance = FolioAccounting.Calculate(folio.Entries);
        return new FolioDto(
            folio.Id,
            folio.BookingId,
            folio.Booking?.BookingCode ?? string.Empty,
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
            folio.ClosedAtUtc,
            folio.Entries.OrderBy(entry => entry.PostedAtUtc).ThenBy(entry => entry.Id)
                .Select(entry => new FolioEntryDto(
                    entry.Id,
                    entry.Type,
                    entry.Direction,
                    entry.Amount,
                    entry.Currency,
                    entry.Description,
                    entry.SourceType,
                    entry.SourceId,
                    entry.ExternalReference,
                    entry.ReversesEntryId,
                    entry.PostedAtUtc,
                    entry.PostedByUserId,
                    entry.Notes))
                .ToArray());
    }

    public static FolioSummaryDto ToSummary(Folio folio)
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

    private static void EnsureOpen(Folio folio)
    {
        if (folio.Status != FolioStatus.Open)
            throw new BadRequestException("The folio is closed and cannot accept new entries.");
    }

    private static string NormalizeBookingCode(string value) =>
        RequireText(value, "Booking code", 30).ToUpperInvariant();

    private static string BuildIdempotencyKey(Guid folioId, string value) =>
        $"{folioId:N}:{RequireText(value, "Idempotency key", 100)}";

    private static decimal RequirePositiveMoney(decimal value, string field)
    {
        var amount = FolioAccounting.Money(value);
        if (amount <= 0)
            throw new BadRequestException($"{field} must be greater than zero.");
        return amount;
    }

    private static string NormalizePaymentMethod(string? value)
    {
        var method = RequireText(value, "Payment method", 20);
        if (method is not ("BankTransfer" or "Cash" or "Monnify" or "Other"))
            throw new BadRequestException("Payment method must be BankTransfer, Cash, Monnify, or Other.");
        return method;
    }

    private static string RequireText(string? value, string field, int maximumLength)
    {
        var cleaned = value?.Trim() ?? string.Empty;
        if (cleaned.Length == 0 || cleaned.Length > maximumLength)
            throw new BadRequestException($"{field} is required and cannot exceed {maximumLength} characters.");
        return cleaned;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
