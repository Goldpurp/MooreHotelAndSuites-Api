using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Interfaces.Services;

public interface IOperationService
{
    Task<IEnumerable<OperationLogEntryDto>> GetLedgerAsync(
        string? filter = null,
        string? search = null,
        int limit = 200,
        DateTime? beforeUtc = null,
        Guid? beforeId = null,
        CancellationToken cancellationToken = default);
    Task<DashboardKpis> GetOperationalKpisAsync();
}
