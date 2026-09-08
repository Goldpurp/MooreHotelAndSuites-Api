using System.ComponentModel.DataAnnotations;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

public record AddOnServiceDto(
    Guid Id,
    string Name,
    string Description,
    AddOnCategory Category,
    decimal Price,
    bool IsActive,
    DateTime CreatedAt);

public sealed record PublicAddOnServiceDto(
    Guid Id,
    string Name,
    string Description,
    AddOnCategory Category,
    decimal Price);

public record CreateAddOnServiceRequest(
    [Required, StringLength(120, MinimumLength = 1)] string Name,
    [StringLength(500)] string Description,
    [EnumDataType(typeof(AddOnCategory))] AddOnCategory Category,
    [Range(typeof(decimal), "0.01", "9999999999999999")] decimal Price,
    bool IsActive = true);

public record UpdateAddOnServiceRequest(
    [StringLength(120, MinimumLength = 1)] string? Name,
    [StringLength(500)] string? Description,
    [EnumDataType(typeof(AddOnCategory))] AddOnCategory? Category,
    [Range(typeof(decimal), "0.01", "9999999999999999")] decimal? Price,
    bool? IsActive);

public record BookingAddOnDto(
    Guid Id,
    Guid BookingId,
    Guid AddOnServiceId,
    string ServiceName,
    AddOnCategory Category,
    int Quantity,
    decimal UnitPrice,
    decimal TotalPrice,
    string? Notes,
    DateTime AddedAtUtc);

public record AddServiceToBookingRequest(
    [Required] Guid AddOnServiceId,
    [Range(1, 1000)] int Quantity = 1,
    [StringLength(300)] string? Notes = null);
