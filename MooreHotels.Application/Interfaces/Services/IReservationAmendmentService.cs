using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Interfaces.Services;

public interface IReservationAmendmentService
{
    Task<ReservationAmendmentDto> AmendAsync(Guid bookingId, AmendReservationRequest request, Guid actorId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReservationAmendmentDto>> GetHistoryAsync(Guid bookingId, CancellationToken cancellationToken = default);
}
