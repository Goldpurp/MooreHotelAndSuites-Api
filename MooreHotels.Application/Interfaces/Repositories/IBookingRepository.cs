
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Domain.Common;
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
    Task<bool> QueueBookingEmailVerificationAsync(
        BookingEmailVerification verification,
        EmailOutboxMessage emailMessage,
        DateTime throttleCutoffUtc,
        CancellationToken cancellationToken = default);
    Task<bool> IsBookingEmailVerificationValidAsync(
        string email,
        string tokenHash,
        DateTime utcNow,
        CancellationToken cancellationToken = default);
    Task AddAsync(
        Booking booking,
        Guest? newGuest = null,
        IReadOnlyCollection<EmailOutboxMessage>? emailMessages = null,
        BookingEmailVerificationProof? emailVerification = null,
        CancellationToken cancellationToken = default);
    Task UpdateAsync(Booking booking);
    Task<ManualTransferConfirmationDto> ConfirmManualTransferAsync(
        string bookingCode,
        ManualTransferConfirmationActor actor,
        CancellationToken cancellationToken = default);
    Task<int> CancelExpiredUnconfirmedAsync(
        DateTime utcNow,
        int batchSize = 100,
        CancellationToken cancellationToken = default);
    Task<int> DeleteExpiredEmailVerificationsAsync(
        DateTime utcNow,
        int batchSize = 500,
        CancellationToken cancellationToken = default);
    Task<IEnumerable<Booking>> GetPendingRefundsAsync();

    // Aggregations
    Task<decimal> GetNetRevenueAsync(DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken cancellationToken = default);
    Task<int> GetActiveGuestsCountAsync(CancellationToken cancellationToken = default);
    Task<int> GetOccupiedRoomNightsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default);
    Task<decimal> GetAverageNightlyRateAsync(DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RevenuePoint>> GetDailyRevenueDynamicsAsync(int days = 7, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ActiveOperationDto>> GetActiveOperationsAsync(int limit = 5, CancellationToken cancellationToken = default);
    Task<int> GetCheckInsCountAsync(DateTime dateUtc, CancellationToken cancellationToken = default);
    Task<int> GetCheckOutsCountAsync(DateTime dateUtc, CancellationToken cancellationToken = default);
    Task<int> GetTotalBookingsCountAsync(CancellationToken cancellationToken = default);
    Task<PagedResult<Booking>> GetPagedBookingsAsync(
        int pageNumber = 1,
        int pageSize = 20,
        BookingStatus? status = null,
        PaymentStatus? paymentStatus = null,
        string? search = null,
        CancellationToken cancellationToken = default);
}
