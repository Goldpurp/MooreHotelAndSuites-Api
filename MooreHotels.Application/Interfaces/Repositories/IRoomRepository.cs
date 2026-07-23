using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.Interfaces.Repositories;

public interface IRoomRepository
{
    Task<Room?> GetByIdAsync(Guid id);
    Task<Room?> GetByRoomNumberAsync(string roomNumber);
    Task<IEnumerable<Room>> GetAllAsync(bool onlyOnline = true);
    Task<IEnumerable<Room>> SearchAsync(
        DateTime? checkIn,
        DateTime? checkOut,
        RoomCategory? category,
        int? capacity,
        string? roomNumber,
        string? amenity);
    Task AddAsync(Room room);
    Task UpdateAsync(Room room);
    Task DeleteAsync(Room room);
    Task<Room?> GetByIdWithImagesAsync(Guid id);

}
