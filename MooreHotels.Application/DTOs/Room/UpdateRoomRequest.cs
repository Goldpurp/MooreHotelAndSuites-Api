using System.ComponentModel.DataAnnotations;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

public record UpdateRoomRequest(
    [StringLength(120, MinimumLength = 1)] string? Name,
    RoomCategory? Category,
    PropertyFloor? Floor, 
    RoomStatus? Status, 
    [Range(typeof(decimal), "0.01", "100000000")] decimal? PricePerNight,
    [Range(1, 50)] int? Capacity,
    [StringLength(50, MinimumLength = 1)] string? Size,
    bool? IsOnline, 
    [StringLength(4000)] string? Description,
    [MaxLength(50)] List<string>? Amenities,
    [MaxLength(30)] List<string>? Images,
    bool? ReplaceAmenities = null,
    bool? ReplaceImages = null);
