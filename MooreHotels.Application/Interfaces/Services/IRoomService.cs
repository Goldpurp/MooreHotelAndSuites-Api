using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.Interfaces.Services;

public interface IRoomService
{
    Task<IEnumerable<RoomDto>> GetAllRoomsAsync(RoomCategory? category = null, bool includeOffline = false);
    Task<IEnumerable<RoomDto>> SearchRoomsAsync(RoomSearchRequest request);
    Task<RoomDto?> GetRoomByIdAsync(Guid id);
    Task<RoomDto> CreateRoomAsync(CreateRoomRequest request);
    Task UpdateRoomAsync(Guid id, UpdateRoomRequest request);
    Task<List<string>> DeleteRoomAsync(Guid id);
    Task<RoomAvailabilityResponse> CheckAvailabilityAsync(Guid roomId, DateTime checkIn, DateTime checkOut);
}
