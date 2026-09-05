using MooreHotels.Application.DTOs;
using MooreHotels.Domain.Entities;

namespace MooreHotels.Application.Interfaces.Services;

public interface IFolioService
{
    Task<FolioDto> GetByBookingCodeAsync(string bookingCode, CancellationToken cancellationToken = default);
    Task<FolioDto> PostChargeAsync(string bookingCode, PostFolioChargeRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<FolioDto> PostPaymentAsync(string bookingCode, PostFolioPaymentRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<FolioDto> PostCreditAsync(string bookingCode, PostFolioCreditRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<FolioDto> VoidEntryAsync(string bookingCode, Guid entryId, VoidFolioEntryRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<FolioDto> CloseAsync(string bookingCode, Guid actorId, CancellationToken cancellationToken = default);
    Task ApplyCancellationCreditAsync(Booking booking, string reason, Guid actorId, CancellationToken cancellationToken = default);
    Task ApplyNoShowPolicyAsync(Booking booking, string reason, Guid actorId, CancellationToken cancellationToken = default);
    Task ApplyRefundAsync(Booking booking, decimal amount, string reference, string channel, string? notes, Guid actorId, CancellationToken cancellationToken = default);
}
