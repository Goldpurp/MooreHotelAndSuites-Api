using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

public sealed record PublicRoomDto(
    Guid Id,
    string Name,
    RoomCategory Category,
    decimal PricePerNight,
    int Capacity,
    string Size,
    string Description,
    List<string> Amenities,
    List<string> Images);
