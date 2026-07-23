using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

public sealed record PublicBookingDto(
    Guid Id,
    string BookingCode,
    Guid RoomId,
    string GuestFirstName,
    string GuestLastName,
    string GuestEmail,
    DateTime CheckIn,
    DateTime CheckOut,
    BookingStatus Status,
    decimal Amount,
    PaymentStatus PaymentStatus,
    PaymentMethod? PaymentMethod,
    DateTime CreatedAt,
    string? PaymentUrl = null,
    string? PaymentInstruction = null,
    string? NotificationMessage = null,
    DateTime? PaymentExpiresAtUtc = null);
