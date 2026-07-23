using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.Interfaces.Services;

public interface IBookingService
{
    Task<BookingDto> CreateBookingAsync(CreateBookingRequest request, Guid? accountUserId = null);
    Task<BookingDto?> GetBookingByCodeAsync(string code);
    Task<BookingDto?> GetBookingByCodeAndEmailAsync(string code, string email);
    Task<IEnumerable<BookingDto>> GetAllBookingsAsync();
    Task<BookingDto> UpdateStatusAsync(Guid bookingId, BookingStatus status, Guid userId);
    Task<ManualTransferConfirmationDto> ConfirmManualTransferAsync(
        string bookingCode,
        ConfirmTransferRequest request,
        Guid actingUserId,
        string requestId,
        CancellationToken cancellationToken = default);
    Task SendPaymentConfirmationAsync(string bookingCode, string reference);
     Task<BookingDto> CancelBookingAsync(Guid bookingId, Guid userId, string? reason = null);
     Task<BookingDto> CancelBookingByGuestAsync(string bookingCode, string email, string? reason = null);
     Task<BookingDto> CompleteRefundAsync(Guid bookingId, string transactionRef, Guid adminId);
     Task<IEnumerable<BookingDto>> GetPendingRefundsAsync();
}
