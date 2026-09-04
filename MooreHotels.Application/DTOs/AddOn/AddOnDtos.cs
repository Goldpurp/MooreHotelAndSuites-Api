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

public record CreateAddOnServiceRequest(
    string Name,
    string Description,
    AddOnCategory Category,
    decimal Price,
    bool IsActive = true);

public record UpdateAddOnServiceRequest(
    string? Name,
    string? Description,
    AddOnCategory? Category,
    decimal? Price,
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
    Guid AddOnServiceId,
    int Quantity = 1,
    string? Notes = null);
