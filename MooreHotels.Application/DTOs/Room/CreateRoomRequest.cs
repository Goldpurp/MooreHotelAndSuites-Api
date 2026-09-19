using System.ComponentModel.DataAnnotations;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

public record CreateRoomRequest(
    [StringLength(30)] string? RoomNumber,
    [Required, StringLength(120, MinimumLength = 1)] string Name,
    RoomCategory Category,
    PropertyFloor Floor,
    RoomStatus Status,
    [Range(typeof(decimal), "0.01", "100000000")] decimal PricePerNight,
    [Range(1, 50)] int Capacity,
    [StringLength(50)] string? Size,
    [StringLength(4000)] string? Description,
    [MaxLength(50)] List<string> Amenities,
    bool? IsOnline = null,
    Guid? RoomTypeId = null);
