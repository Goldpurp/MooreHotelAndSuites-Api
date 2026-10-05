using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.DTOs;

// Payment operators can reconcile folios without access to reservation contact details or guest access tokens.
public sealed record PaymentBookingDto(
    Guid Id, string BookingCode, string GuestFirstName, string GuestLastName,
    BookingStatus Status, decimal Amount, string Currency, PaymentStatus PaymentStatus,
    PaymentMethod? PaymentMethod, string? TransactionReference, string? PaymentConfirmationMethod,
    DateTime CreatedAt, decimal? RefundAmount, decimal? RefundApprovedAmount, string? RefundReference);
