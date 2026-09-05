using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Interfaces.Services;

public interface IOperationalReportingService
{
    Task<OperationsBoardDto> GetBoardAsync(DateOnly businessDate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReservationCalendarItemDto>> GetCalendarAsync(DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default);
    Task<OperationalReportDto> GetReportAsync(DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default);
    Task<NightAuditDto> CloseNightAuditAsync(DateOnly businessDate, Guid actorId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NightAuditDto>> GetNightAuditsAsync(CancellationToken cancellationToken = default);
}
