using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.Interfaces.Services;

public interface IBookingService
{
    Task<BookingDto> CreateBookingAsync(CreateBookingRequest request, Guid? accountUserId = null);
    Task RequestBookingEmailVerificationAsync(
        string email,
        CancellationToken cancellationToken = default);
    Task<BookingDto?> GetBookingByCodeAsync(string code);
    Task<BookingDto?> GetBookingByCodeAndEmailAsync(
        string code,
        string? email,
        string? guestAccessToken = null,
        Guid? accountUserId = null);
    Task RequestBookingAccessLinkAsync(string code, string email, string requestId);
    Task<IEnumerable<BookingDto>> GetAllBookingsAsync();
    Task<PagedResult<BookingDto>> GetPagedBookingsAsync(
        int pageNumber = 1,
        int pageSize = 20,
        BookingStatus? status = null,
        PaymentStatus? paymentStatus = null,
        string? search = null,
        CancellationToken cancellationToken = default);
    Task<BookingDto> UpdateStatusAsync(Guid bookingId, BookingStatus status, Guid userId);
    Task<ManualTransferConfirmationDto> ConfirmManualTransferAsync(
        string bookingCode,
        ConfirmTransferRequest request,
        Guid actingUserId,
        string requestId,
        CancellationToken cancellationToken = default);
    Task<BookingDto> CancelBookingAsync(Guid bookingId, Guid userId, string? reason = null);
    Task<BookingDto> CancelBookingByGuestAsync(
        string bookingCode,
        string? email,
        string? guestAccessToken,
        Guid? accountUserId,
        string requestId,
        string? reason = null);
    Task<BookingDto> CompleteRefundAsync(Guid bookingId, string transactionRef, Guid adminId);
    Task<IEnumerable<BookingDto>> GetPendingRefundsAsync();
}
