using System.ComponentModel.DataAnnotations;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

public record CreateRoomRequest(
    [Required, StringLength(30, MinimumLength = 1)] string RoomNumber,
    [Required, StringLength(120, MinimumLength = 1)] string Name,
    RoomCategory Category, 
    PropertyFloor Floor, 
    RoomStatus Status,
    [Range(typeof(decimal), "0.01", "100000000")] decimal PricePerNight,
    [Range(1, 50)] int Capacity,
    [Required, StringLength(50)] string Size,
    [Required, StringLength(4000)] string Description,
    [MaxLength(50)] List<string> Amenities,
    bool? IsOnline = null);
