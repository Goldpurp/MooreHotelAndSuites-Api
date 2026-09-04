using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

public record RoomSearchRequest(
    DateTime? CheckIn = null,
    DateTime? CheckOut = null,
    RoomCategory? Category = null,
    int? Capacity = null,
    string? RoomNumber = null,
    string? Amenity = null);