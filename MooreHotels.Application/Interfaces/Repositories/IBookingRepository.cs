
using MooreHotels.Domain.Entities;
using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Interfaces.Repositories;

public interface IBookingRepository
{
    Task<Booking?> GetByIdAsync(Guid id);
    Task<Booking?> GetByCodeAsync(string code);
    Task<Booking?> GetByPaymentReferenceAsync(string paymentReference);
    Task<IEnumerable<Booking>> GetAllAsync();
    Task<IEnumerable<Booking>> GetByGuestIdAsync(string guestId);
    Task<string> GenerateBookingCodeAsync(CancellationToken cancellationToken = default);
    Task<bool> IsRoomBookedAsync(Guid roomId, DateTime checkIn, DateTime checkOut);
    Task AddAsync(Booking booking);
    Task UpdateAsync(Booking booking);
    Task<ManualTransferConfirmationDto> ConfirmManualTransferAsync(
        string bookingCode,
        ManualTransferConfirmationActor actor,
        CancellationToken cancellationToken = default);
    Task<int> CancelExpiredUnconfirmedAsync(
        DateTime utcNow,
        int batchSize = 100,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExpiredBookingNotification>>
        CancelExpiredUnconfirmedWithNotificationsAsync(
            DateTime utcNow,
            int batchSize = 100,
            CancellationToken cancellationToken = default);
    Task<IEnumerable<Booking>> GetPendingRefundsAsync();
}
