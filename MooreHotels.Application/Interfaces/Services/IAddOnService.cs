using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.Interfaces.Services;

public interface IAddOnService
{
    Task<IEnumerable<AddOnServiceDto>> GetAllServicesAsync(bool onlyActive = true, AddOnCategory? category = null, CancellationToken cancellationToken = default);
    Task<AddOnServiceDto?> GetServiceByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<AddOnServiceDto> CreateServiceAsync(CreateAddOnServiceRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<AddOnServiceDto> UpdateServiceAsync(Guid id, UpdateAddOnServiceRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task DeleteServiceAsync(Guid id, Guid actorId, CancellationToken cancellationToken = default);

    Task<IEnumerable<BookingAddOnDto>> GetBookingAddOnsAsync(string bookingCode, CancellationToken cancellationToken = default);
    Task<BookingAddOnDto> AddServiceToBookingAsync(string bookingCode, AddServiceToBookingRequest request, Guid actorId, CancellationToken cancellationToken = default);
}
