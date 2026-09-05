using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Application.Interfaces;
using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;
using System.Data;
using System.Text.Json;

namespace MooreHotels.Infrastructure.Services;

public sealed class MonnifyPaymentProcessor : IMonnifyPaymentProcessor
{
    public const string ConfirmationMethod = "MonnifyServerVerification";
    private const string PaidAfterExpiryStatus = "PAID_AFTER_EXPIRY";

    private readonly MooreHotelsDbContext _db;
    private readonly IEmailOutbox _emailOutbox;

    public MonnifyPaymentProcessor(
        MooreHotelsDbContext db,
        IEmailOutbox emailOutbox)
    {
        _db = db;
        _emailOutbox = emailOutbox;
    }

    public async Task<MonnifyPaymentOutcome> ApplyVerifiedPaymentAsync(
        Guid bookingId,
        MonnifyVerificationResult verification,
        string source,
        Guid? actingUserId,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        if (bookingId == Guid.Empty)
            throw new BadRequestException("The booking identifier is invalid.");

        source = NormalizeSource(source);
        requestId = string.IsNullOrWhiteSpace(requestId)
            ? "unavailable"
            : requestId.Trim()[..Math.Min(requestId.Trim().Length, 200)];

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            // The booking row is the serialization point for webhook, staff
            // verification, cancellation, and the expiration worker.
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM bookings WHERE \"Id\" = {bookingId} FOR UPDATE",
                cancellationToken);

            var booking = await _db.Bookings
                .Include(item => item.Guest)
                .SingleOrDefaultAsync(item => item.Id == bookingId, cancellationToken)
                ?? throw new NotFoundException("Booking not found.");

            var now = DateTime.UtcNow;
            ValidateBinding(booking, verification, now);
            var folio = await _db.Folios.Include(item => item.Entries)
                .SingleOrDefaultAsync(item => item.BookingId == booking.Id, cancellationToken)
                ?? throw new InvalidOperationException("The booking folio is missing.");
            if (folio.Status != FolioStatus.Open)
                throw new BadRequestException("The booking folio is closed.");
            var providerReference = verification.TransactionReference!;
            var existingByPaymentReference = await _db.MonnifyTransactions
                .SingleOrDefaultAsync(
                    item => item.TransactionReference == verification.PaymentReference,
                    cancellationToken);
            var existingByProviderReference = await _db.MonnifyTransactions
                .SingleOrDefaultAsync(
                    item => item.MonnifyReference == providerReference,
                    cancellationToken);

            if (existingByPaymentReference is not null &&
                existingByPaymentReference.BookingCode != booking.BookingCode)
            {
                throw new ConflictException(
                    "The verified payment reference is already assigned to another booking.");
            }

            if (existingByProviderReference is not null &&
                existingByProviderReference.TransactionReference !=
                verification.PaymentReference)
            {
                throw new ConflictException(
                    "The provider transaction reference is already assigned to another payment.");
            }

            if (existingByPaymentReference is not null &&
                existingByProviderReference is not null &&
                existingByPaymentReference.Id != existingByProviderReference.Id)
            {
                throw new ConflictException(
                    "The payment references resolve to different transaction records.");
            }

            var providerTransaction =
                existingByPaymentReference ?? existingByProviderReference;
            var transactionWasNew = providerTransaction is null;
            var wasRecordedAfterExpiry = string.Equals(
                providerTransaction?.Status,
                PaidAfterExpiryStatus,
                StringComparison.Ordinal);
            providerTransaction ??= new MonnifyTransaction
            {
                Id = Guid.NewGuid(),
                CreatedAt = now
            };

            booking.PaymentProviderReference ??= providerReference;
            if (!string.Equals(
                    booking.PaymentProviderReference,
                    providerReference,
                    StringComparison.Ordinal))
            {
                throw new BadRequestException(
                    "The provider transaction does not belong to this booking.");
            }

            PopulateTransaction(
                providerTransaction,
                booking,
                verification,
                source,
                now);
            if (transactionWasNew)
            {
                _db.MonnifyTransactions.Add(providerTransaction);
            }

            var paymentEntry = folio.Entries.SingleOrDefault(entry =>
                entry.Type == FolioEntryType.Payment &&
                entry.ExternalReference == verification.PaymentReference);
            if (paymentEntry is null)
            {
                paymentEntry = FolioAccounting.NewEntry(
                    folio,
                    FolioEntryType.Payment,
                    FolioEntryDirection.Credit,
                    verification.AmountPaid,
                    "Monnify payment",
                    "Monnify",
                    booking.Id.ToString(),
                    $"monnify:{BookingGuestAccess.Hash(verification.PaymentReference)}",
                    verification.PaidAtUtc?.ToUniversalTime() ?? now,
                    actingUserId,
                    verification.PaymentReference);
                _db.FolioEntries.Add(paymentEntry);
            }
            else if (paymentEntry.Amount != FolioAccounting.Money(verification.AmountPaid))
            {
                throw new ConflictException(
                    "The payment reference was already recorded with a different amount.");
            }

            var isExpired = booking.Status == BookingStatus.Cancelled ||
                            BookingPaymentPolicy.IsExpiredUnconfirmed(
                                booking.Status,
                                booking.PaymentStatus,
                                booking.CreatedAt,
                                now);

            var previousPaymentStatus = booking.PaymentStatus;
            var previousBookingStatus = booking.Status;
            MonnifyPaymentOutcomeKind outcomeKind;
            if (isExpired)
            {
                outcomeKind = MonnifyPaymentOutcomeKind.PaidAfterExpiry;
                booking.PaymentCheckoutUrl = null;
                booking.Status = BookingStatus.Cancelled;
                booking.CancelledAtUtc ??= now;
                booking.GuestAccessTokenRevokedAtUtc ??= now;
                var cancellationKey = $"booking:{booking.Id:N}:late-payment-cancellation-credit";
                if (!folio.Entries.Any(entry => entry.IdempotencyKey == cancellationKey))
                {
                    var billedAmount = FolioAccounting.Money(folio.Entries
                        .Where(entry => entry.Type is not (FolioEntryType.Payment or FolioEntryType.Refund))
                        .Sum(entry => entry.Direction == FolioEntryDirection.Debit
                            ? entry.Amount
                            : -entry.Amount));
                    if (billedAmount > 0)
                    {
                        _db.FolioEntries.Add(FolioAccounting.NewEntry(
                            folio,
                            FolioEntryType.Credit,
                            FolioEntryDirection.Credit,
                            billedAmount,
                            "Expired reservation release",
                            "Expiration",
                            booking.Id.ToString(),
                            cancellationKey,
                            now));
                    }
                }
                booking.PaymentStatus = PaymentStatus.RefundPending;
                booking.StatusHistoryJson = AppendHistory(
                    booking.StatusHistoryJson,
                    booking.Status,
                    now,
                    source,
                    "Payment arrived after the reservation hold expired; the room remains released and the payment requires refund review.");
                providerTransaction.Status = PaidAfterExpiryStatus;
                providerTransaction.UpdatedAt = now;

                if (!wasRecordedAfterExpiry)
                {
                    AddAudit(
                        booking,
                        providerTransaction,
                        actingUserId,
                        "MONNIFY_PAYMENT_RECEIVED_AFTER_EXPIRY",
                        source,
                        requestId,
                        now,
                        previousPaymentStatus,
                        previousBookingStatus);
                }
            }
            else if (booking.PaymentStatus == PaymentStatus.Paid)
            {
                outcomeKind = MonnifyPaymentOutcomeKind.AlreadyProcessed;
            }
            else
            {
                if (booking.Status != BookingStatus.Pending ||
                    booking.PaymentStatus is
                        PaymentStatus.RefundPending or
                        PaymentStatus.Refunded)
                {
                    throw new ConflictException(
                        "This booking cannot accept a payment in its current state.");
                }

                var folioBalance = FolioAccounting.Calculate(folio.Entries);
                booking.PaymentStatus = folioBalance.GuestCredit > 0
                    ? PaymentStatus.RefundPending
                    : folioBalance.AmountDue == 0
                        ? PaymentStatus.Paid
                        : PaymentStatus.PartiallyPaid;
                booking.Status = BookingStatus.Confirmed;
                booking.PaymentConfirmationMethod = ConfirmationMethod;
                booking.PaymentConfirmedAtUtc = verification.PaidAtUtc?.ToUniversalTime() ?? now;
                booking.PaymentConfirmedByUserId = actingUserId;
                booking.PaymentCheckoutUrl = null;
                booking.StatusHistoryJson = AppendHistory(
                    booking.StatusHistoryJson,
                    booking.Status,
                    now,
                    source,
                    "Payment verified directly with Monnify.");

                providerTransaction.Status =
                    verification.AmountPaid > booking.Amount
                        ? "OVERPAID"
                        : verification.Status.ToUpperInvariant();
                providerTransaction.UpdatedAt = now;
                AddAudit(
                    booking,
                    providerTransaction,
                    actingUserId,
                    "MONNIFY_PAYMENT_CONFIRMED",
                    source,
                    requestId,
                    now,
                    previousPaymentStatus,
                    previousBookingStatus);
                if (booking.Guest is not null)
                {
                    var roomName = await _db.RoomTypes
                        .Where(type => type.Id == booking.RoomTypeId)
                        .Select(type => type.Name)
                        .SingleOrDefaultAsync(cancellationToken)
                        ?? "Reserved Room";
                    _db.EmailOutboxMessages.Add(_emailOutbox.Create(
                        TransactionalEmailTemplates.PaymentSuccess,
                        booking.Guest.Email,
                        new PaymentSuccessEmail(
                            booking.Guest.FirstName,
                            booking.BookingCode,
                            roomName,
                            verification.AmountPaid,
                            verification.PaymentReference)));
                }
                outcomeKind = MonnifyPaymentOutcomeKind.Confirmed;
            }

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new MonnifyPaymentOutcome(
                outcomeKind,
                booking.BookingCode,
                booking.PaymentStatus.ToString(),
                booking.Status.ToString(),
                verification.PaymentReference,
                providerReference,
                now);
        });
    }

    private static void ValidateBinding(
        Booking booking,
        MonnifyVerificationResult verification,
        DateTime now)
    {
        if (booking.PaymentMethod != PaymentMethod.Monnify)
            throw new BadRequestException("This booking does not use Monnify.");

        if (verification.Status is null ||
            !verification.Status.Equals("PAID", StringComparison.OrdinalIgnoreCase) &&
            !verification.Status.Equals("OVERPAID", StringComparison.OrdinalIgnoreCase))
        {
            throw new BadRequestException(
                "Monnify has not confirmed a successful payment.");
        }

        if (string.IsNullOrWhiteSpace(verification.TransactionReference) ||
            verification.TransactionReference.Length > 160 ||
            !string.Equals(
                verification.PaymentReference,
                booking.TransactionReference,
                StringComparison.Ordinal) ||
            !string.Equals(
                verification.CurrencyCode,
                "NGN",
                StringComparison.OrdinalIgnoreCase) ||
            verification.AmountPaid < booking.Amount ||
            string.IsNullOrWhiteSpace(verification.BookingCode) ||
            !string.Equals(
                verification.BookingCode,
                booking.BookingCode,
                StringComparison.Ordinal) ||
            verification.BookingId.HasValue &&
            verification.BookingId.Value != booking.Id ||
            verification.PaidAtUtc.HasValue &&
            (verification.PaidAtUtc.Value.ToUniversalTime() <
             booking.CreatedAt.ToUniversalTime().AddMinutes(-5) ||
             verification.PaidAtUtc.Value.ToUniversalTime() >
             now.AddMinutes(5)) ||
            booking.Guest is null ||
            string.IsNullOrWhiteSpace(verification.CustomerEmail) ||
            !string.Equals(
                verification.CustomerEmail,
                booking.Guest.Email,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new BadRequestException(
                "The verified Monnify payment does not match this booking.");
        }

        if (!string.IsNullOrWhiteSpace(booking.PaymentProviderReference) &&
            !string.Equals(
                booking.PaymentProviderReference,
                verification.TransactionReference,
                StringComparison.Ordinal))
        {
            throw new BadRequestException(
                "The provider transaction does not belong to this booking.");
        }
    }

    private static void PopulateTransaction(
        MonnifyTransaction transaction,
        Booking booking,
        MonnifyVerificationResult verification,
        string source,
        DateTime now)
    {
        transaction.BookingId = booking.Id;
        transaction.BookingCode = booking.BookingCode;
        transaction.TransactionReference = verification.PaymentReference;
        transaction.MonnifyReference = verification.TransactionReference;
        transaction.Amount = verification.AmountPaid;
        transaction.Fee = verification.Fee;
        transaction.SettledAmount = verification.SettlementAmount;
        transaction.PaymentMethod = verification.PaymentMethod;
        transaction.Status = verification.AmountPaid > booking.Amount
            ? "OVERPAID"
            : verification.Status.ToUpperInvariant();
        transaction.Source = source;
        transaction.VerifiedAtUtc = now;
    }

    private void AddAudit(
        Booking booking,
        MonnifyTransaction transaction,
        Guid? actingUserId,
        string action,
        string source,
        string requestId,
        DateTime now,
        PaymentStatus previousPaymentStatus,
        BookingStatus previousBookingStatus)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ProfileId = actingUserId ?? BookingPaymentPolicy.SystemActorId,
            Action = action,
            EntityType = "Booking",
            EntityId = booking.Id.ToString(),
            OldDataJson = JsonSerializer.Serialize(new
            {
                booking.Id,
                booking.BookingCode,
                PaymentStatus = previousPaymentStatus.ToString(),
                Status = previousBookingStatus.ToString()
            }),
            NewDataJson = JsonSerializer.Serialize(new
            {
                booking.Id,
                booking.BookingCode,
                booking.GuestId,
                booking.Amount,
                AmountPaid = transaction.Amount,
                Currency = "NGN",
                PaymentStatus = booking.PaymentStatus.ToString(),
                Status = booking.Status.ToString(),
                PaymentReference = transaction.TransactionReference,
                ProviderTransactionReference = transaction.MonnifyReference,
                ProviderStatus = transaction.Status,
                ConfirmationMethod,
                Source = source,
                ConfirmingUserId = actingUserId,
                ProcessedAtUtc = now,
                RequestId = requestId
            }),
            CreatedAt = now
        });
    }

    private static string AppendHistory(
        string? historyJson,
        BookingStatus status,
        DateTime timestamp,
        string actor,
        string reason)
    {
        var history = JsonSerializer.Deserialize<List<object>>(
                          string.IsNullOrWhiteSpace(historyJson)
                              ? "[]"
                              : historyJson)
                      ?? [];
        history.Add(new
        {
            Status = status.ToString(),
            Timestamp = timestamp,
            Actor = actor,
            Reason = reason
        });
        return JsonSerializer.Serialize(history);
    }

    private static string NormalizeSource(string source) =>
        source switch
        {
            "Webhook" => source,
            "AdminVerification" => source,
            _ => throw new ArgumentException(
                "The Monnify payment source is invalid.",
                nameof(source))
        };
}
