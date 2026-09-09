using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.Interfaces.Repositories;

public interface IRoomRepository
{
    Task<Room?> GetByIdAsync(Guid id);
    Task<Room?> GetByRoomNumberAsync(string roomNumber);
    Task<RoomType?> GetRoomTypeByIdAsync(Guid id);
    Task<RoomType?> GetDefaultRoomTypeForCategoryAsync(RoomCategory category);
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
    void RemoveInventoryPeriod(RoomInventoryPeriod period);
    Task DeleteAsync(Room room);
    Task<Room?> GetByIdWithImagesAsync(Guid id);
    Task<AssetStatusDistribution> GetAssetStatusDistributionAsync(CancellationToken cancellationToken = default);
    Task<(int TotalRooms, int OccupiedRooms)> GetRoomCountsAsync(CancellationToken cancellationToken = default);
}
