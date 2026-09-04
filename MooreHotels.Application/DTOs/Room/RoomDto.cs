using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

public record RoomDto(
    Guid Id,
    string RoomNumber,
    string Name,
    RoomCategory Category,
    PropertyFloor Floor,
    RoomStatus Status,
    decimal PricePerNight,
    int Capacity,
    string Size,
    bool IsOnline,
    string Description,
    List<string> Amenities,
    List<string> Images,
    DateTime CreatedAt);