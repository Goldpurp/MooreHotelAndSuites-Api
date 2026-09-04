using Microsoft.EntityFrameworkCore;
using System.Data;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
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
        CancellationToken cancellationToken = default)
    {
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

            if (booking.PaymentStatus is PaymentStatus.Paid or PaymentStatus.RefundPending or PaymentStatus.Refunded)
            {
                throw new BadRequestException(
                    "Cannot change a folio after payment or refund processing has started.");
            }

            if (!string.IsNullOrWhiteSpace(booking.PaymentCheckoutUrl) ||
                !string.IsNullOrWhiteSpace(booking.PaymentProviderReference))
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

            var totalPrice = service.Price * quantity;
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

            booking.Amount += totalPrice;
            await _db.BookingAddOns.AddAsync(bookingAddOn, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return bookingAddOn;
        });
    }

    public async Task RemoveBookingAddOnAsync(BookingAddOn bookingAddOn, CancellationToken cancellationToken = default)
    {
        _db.BookingAddOns.Remove(bookingAddOn);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
