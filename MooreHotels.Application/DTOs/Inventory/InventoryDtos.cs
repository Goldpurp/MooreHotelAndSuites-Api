using System.ComponentModel.DataAnnotations;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

public sealed record RoomTypeDto(
    Guid Id,
    string Code,
    string Name,
    RoomCategory Category,
    int BaseOccupancy,
    int MaxOccupancy,
    decimal BasePricePerNight,
    string Description,
    IReadOnlyList<string> Amenities,
    bool IsActive,
    int PhysicalRoomCount,
    DateTime UpdatedAtUtc);

public sealed record PublicRoomTypeDto(
    Guid Id,
    string Code,
    string Name,
    RoomCategory Category,
    int BaseOccupancy,
    int MaxOccupancy,
    decimal BasePricePerNight,
    string Description,
    IReadOnlyList<string> Amenities);

public sealed record SaveRoomTypeRequest(
    [Required, StringLength(30)] string Code,
    [Required, StringLength(120)] string Name,
    RoomCategory Category,
    [Range(1, 50)] int BaseOccupancy,
    [Range(1, 50)] int MaxOccupancy,
    [Range(typeof(decimal), "0.01", "9999999999999999")] decimal BasePricePerNight,
    [StringLength(2000)] string Description,
    [MaxLength(50)] IReadOnlyList<string>? Amenities,
    bool IsActive = true);

public sealed record InventoryDayDto(
    DateOnly StayDate,
    int PhysicalUnits,
    int ClosedUnits,
    int ReservedUnits,
    int AvailableUnits);

public sealed record RoomTypeAvailabilityDto(
    Guid RoomTypeId,
    string RoomTypeCode,
    string RoomTypeName,
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    int RequestedUnits,
    int AvailableUnits,
    bool Available,
    IReadOnlyList<InventoryDayDto> Days);

public sealed record PublicRoomTypeAvailabilityDto(
    Guid RoomTypeId,
    string RoomTypeCode,
    string RoomTypeName,
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    int RequestedUnits,
    bool Available);

public sealed record CreateInventoryClosureRequest(
    [Required] Guid RoomTypeId,
    Guid? RoomId,
    [Required] DateOnly StartDate,
    [Required] DateOnly EndDate,
    [Range(1, 1000)] int Units,
    [Required, StringLength(500, MinimumLength = 4)] string Reason);

public sealed record InventoryClosureDto(
    Guid Id,
    Guid RoomTypeId,
    string RoomTypeCode,
    Guid? RoomId,
    string? RoomNumber,
    DateOnly StartDate,
    DateOnly EndDate,
    int Units,
    string Reason,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record ReservationRoomDto(
    Guid Id,
    int Sequence,
    Guid RoomTypeId,
    string RoomTypeCode,
    string RoomTypeName,
    Guid? AssignedRoomId,
    string? AssignedRoomNumber,
    DateTime? AssignedAtUtc,
    Guid? AssignedByUserId);

public sealed record PublicReservationRoomDto(
    Guid Id,
    int Sequence,
    Guid RoomTypeId,
    string RoomTypeCode,
    string RoomTypeName);

public sealed record AssignReservationRoomRequest(
    [Required] Guid RoomId,
    [Required, StringLength(500, MinimumLength = 4)] string Reason);
