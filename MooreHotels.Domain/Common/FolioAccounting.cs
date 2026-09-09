using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Domain.Common;

public readonly record struct FolioBalance(
    decimal TotalDebits,
    decimal TotalCredits,
    decimal Balance,
    decimal AmountDue,
    decimal GuestCredit,
    decimal Payments,
    decimal Refunds);

public static class FolioAccounting
{
    public static Folio CreateInitial(Booking booking, DateTime openedAtUtc)
    {
        var folio = new Folio
        {
            Id = Guid.NewGuid(),
            BookingId = booking.Id,
            Currency = booking.Currency,
            OpenedAtUtc = openedAtUtc
        };
        Add(folio, FolioEntryType.RoomCharge, FolioEntryDirection.Debit,
            booking.RoomSubtotal, "Room accommodation", "Booking", booking.Id.ToString(),
            $"booking:{booking.Id:N}:room", openedAtUtc);
        Add(folio, FolioEntryType.Discount, FolioEntryDirection.Credit,
            booking.DiscountAmount, "Reservation discount", "Booking", booking.Id.ToString(),
            $"booking:{booking.Id:N}:discount", openedAtUtc);
        Add(folio, FolioEntryType.Tax, FolioEntryDirection.Debit,
            booking.TaxAmount, "Exclusive taxes", "Booking", booking.Id.ToString(),
            $"booking:{booking.Id:N}:tax", openedAtUtc);
        Add(folio, FolioEntryType.Fee, FolioEntryDirection.Debit,
            booking.FeeAmount, "Reservation fees", "Booking", booking.Id.ToString(),
            $"booking:{booking.Id:N}:fee", openedAtUtc);
        return folio;
    }

    public static FolioBalance Calculate(IEnumerable<FolioEntry> entries)
    {
        var materialized = entries.ToArray();
        var debits = Money(materialized.Where(entry => entry.Direction == FolioEntryDirection.Debit).Sum(entry => entry.Amount));
        var credits = Money(materialized.Where(entry => entry.Direction == FolioEntryDirection.Credit).Sum(entry => entry.Amount));
        var balance = Money(debits - credits);
        var originals = materialized.ToDictionary(entry => entry.Id);
        var voidedPayments = materialized
            .Where(entry => entry.Type == FolioEntryType.Void &&
                            entry.ReversesEntryId.HasValue &&
                            originals.TryGetValue(entry.ReversesEntryId.Value, out var original) &&
                            original.Type == FolioEntryType.Payment)
            .Sum(entry => entry.Amount);
        var voidedRefunds = materialized
            .Where(entry => entry.Type == FolioEntryType.Void &&
                            entry.ReversesEntryId.HasValue &&
                            originals.TryGetValue(entry.ReversesEntryId.Value, out var original) &&
                            original.Type == FolioEntryType.Refund)
            .Sum(entry => entry.Amount);
        var payments = Money(materialized
            .Where(entry => entry.Type == FolioEntryType.Payment && entry.Direction == FolioEntryDirection.Credit)
            .Sum(entry => entry.Amount) - voidedPayments);
        var refunds = Money(materialized
            .Where(entry => entry.Type == FolioEntryType.Refund && entry.Direction == FolioEntryDirection.Debit)
            .Sum(entry => entry.Amount) - voidedRefunds);
        return new FolioBalance(
            debits,
            credits,
            balance,
            Math.Max(0, balance),
            Math.Max(0, -balance),
            payments,
            refunds);
    }

    public static FolioEntry NewEntry(
        Folio folio,
        FolioEntryType type,
        FolioEntryDirection direction,
        decimal amount,
        string description,
        string sourceType,
        string? sourceId,
        string idempotencyKey,
        DateTime postedAtUtc,
        Guid? actorId = null,
        string? externalReference = null,
        Guid? reversesEntryId = null,
        string? notes = null) => new()
        {
            Id = Guid.NewGuid(),
            FolioId = folio.Id,
            Type = type,
            Direction = direction,
            Amount = Money(amount),
            Currency = folio.Currency,
            Description = description,
            SourceType = sourceType,
            SourceId = sourceId,
            IdempotencyKey = idempotencyKey,
            ExternalReference = externalReference,
            ReversesEntryId = reversesEntryId,
            PostedAtUtc = postedAtUtc,
            PostedByUserId = actorId,
            Notes = notes
        };

    private static void Add(
        Folio folio,
        FolioEntryType type,
        FolioEntryDirection direction,
        decimal amount,
        string description,
        string sourceType,
        string sourceId,
        string idempotencyKey,
        DateTime postedAtUtc)
    {
        if (amount <= 0) return;
        folio.Entries.Add(NewEntry(
            folio, type, direction, amount, description, sourceType, sourceId,
            idempotencyKey, postedAtUtc));
    }

    public static decimal Money(decimal amount) =>
        decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}
