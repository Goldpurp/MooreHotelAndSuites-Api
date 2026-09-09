using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Interfaces.Services;

public interface IInventoryService
{
    Task<IReadOnlyList<RoomTypeDto>> GetRoomTypesAsync(bool includeInactive, CancellationToken cancellationToken = default);
    Task<RoomTypeDto> SaveRoomTypeAsync(Guid? id, SaveRoomTypeRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<RoomTypeAvailabilityDto> GetAvailabilityAsync(Guid roomTypeId, DateOnly checkInDate, DateOnly checkOutDate, int requestedUnits, CancellationToken cancellationToken = default);
    Task<RoomTypeAvailabilityDto> GetAvailabilityExcludingBookingAsync(Guid roomTypeId, DateOnly checkInDate, DateOnly checkOutDate, int requestedUnits, Guid excludedBookingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InventoryClosureDto>> GetClosuresAsync(DateOnly? fromDate, DateOnly? toDate, CancellationToken cancellationToken = default);
    Task<InventoryClosureDto> CreateClosureAsync(CreateInventoryClosureRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task DeactivateClosureAsync(Guid id, Guid actorId, CancellationToken cancellationToken = default);
    Task<ReservationRoomDto> AssignRoomAsync(Guid bookingId, Guid reservationRoomId, AssignReservationRoomRequest request, Guid actorId, CancellationToken cancellationToken = default);
}
