using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

public record BookingDto(
    Guid Id, 
    string BookingCode, 
    Guid RoomId, 
    string GuestId,
    string GuestFirstName,
    string GuestLastName,
    string GuestEmail,
    string GuestPhone,
    DateTime CheckIn, 
    DateTime CheckOut, 
    BookingStatus Status,
    decimal Amount, 
    PaymentStatus PaymentStatus, 
    PaymentMethod? PaymentMethod,
    string? TransactionReference,
    string? Notes,
    DateTime CreatedAt,
    string? PaymentUrl = null,
    string? PaymentInstruction = null,
    string? NotificationMessage = null,
    string? RefundReference = null,
    DateTime? PaymentExpiresAtUtc = null);
