using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.Interfaces.Repositories;

public interface IAddOnRepository
{
    Task<IEnumerable<AddOnService>> GetAllServicesAsync(bool onlyActive = true, AddOnCategory? category = null, CancellationToken cancellationToken = default);
    Task<AddOnService?> GetServiceByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddServiceAsync(AddOnService service, CancellationToken cancellationToken = default);
    Task UpdateServiceAsync(AddOnService service, CancellationToken cancellationToken = default);
    Task DeleteServiceAsync(AddOnService service, CancellationToken cancellationToken = default);

    Task<IEnumerable<BookingAddOn>> GetBookingAddOnsAsync(Guid bookingId, CancellationToken cancellationToken = default);
    Task<BookingAddOn> AddBookingAddOnAndUpdateTotalAsync(
        string bookingCode,
        Guid addOnServiceId,
        int quantity,
        string? notes,
        DateTime addedAtUtc,
        Guid actorId,
        CancellationToken cancellationToken = default);
}
