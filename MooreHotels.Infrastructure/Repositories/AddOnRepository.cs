using Microsoft.EntityFrameworkCore;
using System.Data;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Domain.Common;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.Infrastructure.Repositories;

public class AddOnRepository : IAddOnRepository
{
    private readonly MooreHotelsDbContext _db;

    public AddOnRepository(MooreHotelsDbContext db) => _db = db;

    public async Task<IEnumerable<AddOnService>> GetAllServicesAsync(
        bool onlyActive = true,
        AddOnCategory? category = null,
        CancellationToken cancellationToken = default)
    {
        var query = _db.AddOnServices.AsNoTracking().AsQueryable();

        if (onlyActive) query = query.Where(a => a.IsActive);
        if (category.HasValue) query = query.Where(a => a.Category == category.Value);

        return await query.OrderBy(a => a.Name).ToListAsync(cancellationToken);
    }

    public async Task<AddOnService?> GetServiceByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _db.AddOnServices.FindAsync(new object[] { id }, cancellationToken);

    public async Task AddServiceAsync(AddOnService service, CancellationToken cancellationToken = default)
    {
        await _db.AddOnServices.AddAsync(service, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateServiceAsync(AddOnService service, CancellationToken cancellationToken = default)
    {
        _db.AddOnServices.Update(service);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteServiceAsync(AddOnService service, CancellationToken cancellationToken = default)
    {
        _db.AddOnServices.Remove(service);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IEnumerable<BookingAddOn>> GetBookingAddOnsAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        await _db.BookingAddOns
            .AsNoTracking()
            .Include(b => b.AddOnService)
            .Where(b => b.BookingId == bookingId)
            .OrderBy(b => b.AddedAtUtc)
            .ToListAsync(cancellationToken);

    public async Task<BookingAddOn> AddBookingAddOnAndUpdateTotalAsync(
        string bookingCode,
        Guid addOnServiceId,
        int quantity,
        string? notes,
        DateTime addedAtUtc,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bookingCode) || bookingCode.Length > 30 ||
            addOnServiceId == Guid.Empty || actorId == Guid.Empty || quantity is < 1 or > 1000 ||
            notes?.Length > 500 || notes?.Any(char.IsControl) == true)
        {
            throw new BadRequestException("Add-on booking data is invalid or too long.");
        }
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            // Always lock the booking before the catalogue row. This keeps the
            // transaction short and gives all writers a stable lock order.
            var booking = await _db.Bookings
                .FromSqlInterpolated(
                    $"SELECT * FROM bookings WHERE \"BookingCode\" = {bookingCode} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException("Booking not found.");

            if (booking.Status is BookingStatus.Cancelled or BookingStatus.CheckedOut or BookingStatus.NoShow)
            {
                throw new BadRequestException(
                    "Cannot attach services to a closed booking.");
            }

            if (booking.PaymentStatus is PaymentStatus.RefundPending or PaymentStatus.Refunded)
                throw new BadRequestException("Cannot add charges while a refund is in progress.");

            if (booking.PaymentStatus is not (PaymentStatus.Paid or PaymentStatus.PartiallyPaid) &&
                (!string.IsNullOrWhiteSpace(booking.PaymentCheckoutUrl) ||
                !string.IsNullOrWhiteSpace(booking.PaymentProviderReference))
               )
            {
                throw new BadRequestException(
                    "Cannot change a folio after a hosted payment checkout has been created.");
            }

            var service = await _db.AddOnServices
                .FromSqlInterpolated(
                    $"SELECT * FROM addon_services WHERE \"Id\" = {addOnServiceId} FOR SHARE")
                .SingleOrDefaultAsync(cancellationToken);
            if (service is null || !service.IsActive)
            {
                throw new BadRequestException(
                    "The requested add-on service is invalid or inactive.");
            }

            decimal totalPrice;
            try
            {
                totalPrice = FolioAccounting.Money(checked(service.Price * quantity));
            }
            catch (OverflowException)
            {
                throw new BadRequestException("The add-on total is outside the supported monetary range.");
            }
            if (totalPrice <= 0 || totalPrice > 9999999999999999m)
                throw new BadRequestException("The add-on total is outside the supported monetary range.");
            var bookingAddOn = new BookingAddOn
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                AddOnServiceId = service.Id,
                Quantity = quantity,
                UnitPrice = service.Price,
                TotalPrice = totalPrice,
                Notes = notes,
                AddedAtUtc = addedAtUtc,
                AddOnService = service
            };

            var folio = await _db.Folios.Include(item => item.Entries)
                .SingleOrDefaultAsync(item => item.BookingId == booking.Id, cancellationToken)
                ?? throw new InvalidOperationException("The booking folio is missing.");
            if (folio.Status != FolioStatus.Open)
                throw new BadRequestException("Cannot add a service to a closed folio.");
            var folioEntry = FolioAccounting.NewEntry(
                folio,
                FolioEntryType.AddOnCharge,
                FolioEntryDirection.Debit,
                totalPrice,
                service.Name,
                "BookingAddOn",
                bookingAddOn.Id.ToString(),
                $"addon:{bookingAddOn.Id:N}",
                addedAtUtc,
                actorId,
                notes: notes);
            _db.FolioEntries.Add(folioEntry);
            booking.Amount = FolioAccounting.Money(booking.Amount + totalPrice);
            var balance = FolioAccounting.Calculate(folio.Entries);
            if (balance.Payments - balance.Refunds > 0)
                booking.PaymentStatus = balance.GuestCredit > 0
                    ? PaymentStatus.RefundPending
                    : balance.AmountDue == 0
                        ? PaymentStatus.Paid
                        : PaymentStatus.PartiallyPaid;
            await _db.BookingAddOns.AddAsync(bookingAddOn, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return bookingAddOn;
        });
    }

}
